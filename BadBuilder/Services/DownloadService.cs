using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using BadBuilder.Configuration;
using BadBuilder.UI;

namespace BadBuilder.Services;

internal sealed record UntrustedArtifact(
    string DisplayName,
    string Source,
    string Release,
    string Asset,
    string SHA256,
    bool BytesChanged);

internal static class DownloadService
{
    private const long MaximumArchiveDownloadBytes = 16L * 1024 * 1024 * 1024;
    private const long MaximumManifestBytes = 64 * 1024;
    private const long DownloadSpaceMarginBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(10);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly GitHubReleaseClient GitHubClient = new(HttpClient);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    internal static async Task<DownloadResult> DownloadAsync(
        IReadOnlyList<ArtifactDefinition> artifacts,
        string downloadRoot,
        CancellationToken cancellationToken,
        Func<UntrustedArtifact, bool>? approveUntrusted = null,
        Func<IReadOnlyList<ArchivePathEntry>, Dictionary<string, string>>? promptLocalArchives = null,
        HttpClient? httpClient = null)
    {
        approveUntrusted ??= PromptForUntrustedArtifact;
        promptLocalArchives ??= entries => Controls.PromptArchivePathOverrides(
            "Required artifacts are unavailable online",
            entries,
            "Provide a local archive for every configured artifact. Nothing will be skipped.");
        HttpClient client = httpClient ?? HttpClient;
        GitHubReleaseClient githubClient = ReferenceEquals(client, HttpClient) ? GitHubClient : new(client);

        Directory.CreateDirectory(downloadRoot);
        EnsurePrivateCacheDirectory(downloadRoot);
        Controls.WriteInfo("Resolving artifact releases.");

        IReadOnlyList<ResolvedArtifact> resolved = await ResolveArtifactsAsync(artifacts, githubClient, cancellationToken);
        HashSet<string> prompted = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> overrides = await CollectLocalOverridesAsync(
            resolved, downloadRoot, promptLocalArchives, prompted, cancellationToken);
        List<DownloadPlan> plans = [];

        foreach (ResolvedArtifact item in resolved)
        {
            string? localOverride = overrides.GetValueOrDefault(item.Artifact.ID);
            try
            {
                plans.Add(await CreatePlanAsync(item, downloadRoot, localOverride, cancellationToken));
            }
            catch (Exception ex) when (IsRecoverableFailure(ex) && string.IsNullOrWhiteSpace(localOverride))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Controls.WriteWarning($"{item.Artifact.DisplayName} could not be prepared from the configured sources: {ex.Message}");
                DownloadPlan? fallback = await PromptForLocalPlanAsync(
                    item.Artifact, item.Reference, downloadRoot, promptLocalArchives, prompted, cancellationToken);
                if (fallback is null)
                    throw;
                plans.Add(fallback);
            }
        }

        int downloaded = 0;
        for (int index = 0; index < plans.Count; index++)
        {
            DownloadPlan plan = plans[index];
            cancellationToken.ThrowIfCancellationRequested();
            bool countedAsDownload = plan.Action == PlanAction.Download;
            if (countedAsDownload)
            {
                Controls.WriteInfo($"Downloading {plan.Artifact.DisplayName}.");
                downloaded++;
            }

            try
            {
                await ExecutePlanAsync(plan, client, approveUntrusted, cancellationToken);
            }
            catch (Exception ex) when (IsRecoverableFailure(ex) &&
                                       string.IsNullOrWhiteSpace(plan.Artifact.LocalArchivePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Controls.WriteWarning($"{plan.Artifact.DisplayName} failed: {ex.Message}");
                DownloadPlan? fallback = await PromptForLocalPlanAsync(
                    plan.Artifact, plan.Reference, downloadRoot, promptLocalArchives, prompted, cancellationToken);
                if (fallback is null)
                    throw;
                if (countedAsDownload)
                    downloaded--;
                plans[index] = fallback;
                await ExecutePlanAsync(fallback, client, approveUntrusted, cancellationToken);
            }
        }

        return new DownloadResult(
            downloaded,
            [..plans.Select(plan => (plan.Artifact, ArchivePath: plan.Target))]);
    }

    private static void EnsurePrivateCacheDirectory(string downloadRoot)
    {
        // The manifest records a previous approval. That record is only meaningful
        // when no other user can write the directory holding it.
        if (OperatingSystem.IsWindows())
            return;

        UnixFileMode mode = File.GetUnixFileMode(downloadRoot);
        const UnixFileMode writableByOthers = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if ((mode & writableByOthers) != 0)
            throw new IOException(
                $"The download cache {downloadRoot} is writable by other users. Choose a private cache directory before continuing.");
    }

    private static async Task<IReadOnlyList<ResolvedArtifact>> ResolveArtifactsAsync(
        IReadOnlyList<ArtifactDefinition> artifacts,
        GitHubReleaseClient githubClient,
        CancellationToken cancellationToken)
    {
        List<ResolvedArtifact> resolved = new(artifacts.Count);
        foreach (ArtifactDefinition artifact in artifacts)
        {
            if (artifact.Source is null)
            {
                resolved.Add(new ResolvedArtifact(artifact, null, null));
                continue;
            }

            try
            {
                ArtifactReference reference = await ResolveReferenceAsync(artifact, githubClient, cancellationToken);
                resolved.Add(new ResolvedArtifact(artifact, reference, null));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or TaskCanceledException or TimeoutException or JsonException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Controls.WriteWarning($"{artifact.DisplayName} could not be resolved online: {ex.Message}");
                resolved.Add(new ResolvedArtifact(artifact, null, ex.Message));
            }
        }
        return resolved;
    }

    private static async Task<ArtifactReference> ResolveReferenceAsync(
        ArtifactDefinition artifact,
        GitHubReleaseClient githubClient,
        CancellationToken cancellationToken)
    {
        return artifact.Source switch
        {
            GitHubReleaseSource source => await githubClient.ResolveAsync(artifact, source, cancellationToken),
            DirectSource source => ResolveDirectSource(artifact, source),
            null => throw new InvalidOperationException($"No source is configured for {artifact.DisplayName}."),
            _ => throw new InvalidOperationException($"Unsupported source type for {artifact.DisplayName}."),
        };
    }

    private static ArtifactReference ResolveDirectSource(ArtifactDefinition artifact, DirectSource source)
    {
        if (!Uri.TryCreate(source.URL, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"The direct source for {artifact.DisplayName} must be an absolute HTTPS URL.");

        string fileName = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException($"The direct source for {artifact.DisplayName} has no file name.");

        return new ArtifactReference(
            "direct",
            fileName,
            uri.AbsoluteUri,
            GitHubReleaseClient.ValidatePinnedHash(source.TrustedSHA256, artifact.DisplayName),
            uri.GetLeftPart(UriPartial.Authority),
            null,
            null,
            source.ExpectedSize);
    }

    private static async Task<Dictionary<string, string>> CollectLocalOverridesAsync(
        IReadOnlyList<ResolvedArtifact> resolved,
        string downloadRoot,
        Func<IReadOnlyList<ArchivePathEntry>, Dictionary<string, string>> promptLocalArchives,
        HashSet<string> prompted,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> paths = resolved
            .Where(item => !string.IsNullOrWhiteSpace(item.Artifact.LocalArchivePath))
            .ToDictionary(
                item => item.Artifact.ID,
                item => Path.GetFullPath(FileServices.NormalizeUserPath(item.Artifact.LocalArchivePath!)),
                StringComparer.OrdinalIgnoreCase);

        List<ArchivePathEntry> missing = [];
        foreach (ResolvedArtifact item in resolved.Where(item => item.Reference is null && !paths.ContainsKey(item.Artifact.ID)))
        {
            string? cached = await GetCachedArchivePathAsync(item.Artifact, downloadRoot, cancellationToken);
            if (cached is null)
                missing.Add(new ArchivePathEntry(item.Artifact.ID, item.Artifact.DisplayName, null, Required: true));
        }

        if (missing.Count == 0)
            return paths;

        foreach (ArchivePathEntry entry in missing)
            prompted.Add(entry.ArtifactID);

        Dictionary<string, string> selected = promptLocalArchives(missing);
        foreach ((string id, string path) in selected)
            paths[id] = Path.GetFullPath(FileServices.NormalizeUserPath(path));
        return paths;
    }

    private static async Task<DownloadPlan?> PromptForLocalPlanAsync(
        ArtifactDefinition artifact,
        ArtifactReference? reference,
        string downloadRoot,
        Func<IReadOnlyList<ArchivePathEntry>, Dictionary<string, string>> promptLocalArchives,
        HashSet<string> prompted,
        CancellationToken cancellationToken)
    {
        if (!prompted.Add(artifact.ID))
            return null;

        Dictionary<string, string> selected = promptLocalArchives(
            [new ArchivePathEntry(artifact.ID, artifact.DisplayName, null, Required: true)]);
        if (!selected.TryGetValue(artifact.ID, out string? path) || string.IsNullOrWhiteSpace(path))
            return null;

        string localPath = Path.GetFullPath(FileServices.NormalizeUserPath(path));
        ResolvedArtifact item = new(artifact, reference, null);
        return await CreatePlanAsync(item, downloadRoot, localPath, cancellationToken);
    }

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is not OperationCanceledException &&
        exception is InvalidOperationException
            or InvalidDataException
            or IOException
            or HttpRequestException
            or TimeoutException
            or UnauthorizedAccessException;

    private static async Task<DownloadPlan> CreatePlanAsync(
        ResolvedArtifact item,
        string downloadRoot,
        string? localOverride,
        CancellationToken cancellationToken)
    {
        string artifactRoot = Path.Combine(downloadRoot, FileServices.ValidateIdentifier(item.Artifact.ID));
        Directory.CreateDirectory(artifactRoot);
        string manifestPath = Path.Combine(artifactRoot, "download-manifest.json");
        DownloadManifest? manifest = await ReadManifestAsync(manifestPath, cancellationToken);

        if (!string.IsNullOrWhiteSpace(localOverride))
        {
            FileServices.EnsureFile(localOverride);
            string imported = await ImportLocalArchiveAsync(item.Artifact, localOverride, artifactRoot, cancellationToken);
            return CreateExistingPlan(item, imported, manifestPath, manifest, PlanAction.Use);
        }

        if (item.Reference is null)
        {
            string? cached = await GetCachedArchivePathAsync(item.Artifact, downloadRoot, cancellationToken);
            if (cached is null)
                throw new InvalidOperationException($"No archive is available for required artifact {item.Artifact.DisplayName}.");
            return CreateExistingPlan(item, cached, manifestPath, manifest, PlanAction.VerifyExisting);
        }

        string safeName = Path.GetFileName(item.Reference.Name);
        if (!string.Equals(safeName, item.Reference.Name, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(safeName) ||
            item.Reference.Name.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidOperationException($"The release asset name for {item.Artifact.DisplayName} is unsafe.");

        string target = Path.Combine(artifactRoot, safeName);
        if (File.Exists(target))
        {
            string actual = FileServices.ComputeSHA256(target);
            if (item.Reference.ExpectedSHA256 is not null &&
                string.Equals(actual, item.Reference.ExpectedSHA256, StringComparison.OrdinalIgnoreCase))
            {
                return new DownloadPlan(item.Artifact, PlanAction.VerifyExisting, target, manifestPath, item.Reference, manifest, BytesChanged: false);
            }

            if (item.Reference.ExpectedSHA256 is null &&
                ManifestMatches(manifest, item.Reference) &&
                string.Equals(actual, manifest!.ApprovedSHA256, StringComparison.OrdinalIgnoreCase))
            {
                return new DownloadPlan(item.Artifact, PlanAction.Reuse, target, manifestPath, item.Reference, manifest, BytesChanged: false);
            }

            bool changed = manifest?.ApprovedSHA256 is not null &&
                !string.Equals(actual, manifest.ApprovedSHA256, StringComparison.OrdinalIgnoreCase);

            if (item.Reference.ExpectedSHA256 is null && ManifestMatches(manifest, item.Reference))
                return new DownloadPlan(item.Artifact, PlanAction.VerifyExisting, target, manifestPath, item.Reference, manifest, changed);
        }

        return new DownloadPlan(item.Artifact, PlanAction.Download, target, manifestPath, item.Reference, manifest, BytesChanged: false);
    }

    private static async Task<string> ImportLocalArchiveAsync(
        ArtifactDefinition artifact,
        string localPath,
        string artifactRoot,
        CancellationToken cancellationToken)
    {
        // A user-supplied archive lives outside the cache root. Copy it in so the
        // manifest can keep a confined relative path and later runs can reuse it.
        string fileName = Path.GetFileName(localPath);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidDataException($"The local archive for {artifact.DisplayName} has no file name.");

        string localHash = FileServices.ComputeSHA256(localPath);
        EnsureHashMatches(artifact.DisplayName, localHash, artifact.Source?.TrustedSHA256);

        string target = Path.Combine(artifactRoot, fileName);
        if (File.Exists(target) &&
            string.Equals(FileServices.ComputeSHA256(target), localHash, StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        string temporary = target + $".{Guid.NewGuid():N}.import";
        try
        {
            await using (FileStream input = new(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (FileStream output = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static DownloadPlan CreateExistingPlan(
        ResolvedArtifact item,
        string path,
        string manifestPath,
        DownloadManifest? manifest,
        PlanAction defaultAction)
    {
        ArtifactReference? reference = item.Reference;
        string actual = FileServices.ComputeSHA256(path);
        // Trust comes only from the compiled catalog. A manifest is a record of a
        // previous decision, never a source of authority.
        string? expected = reference?.ExpectedSHA256 ?? item.Artifact.Source?.TrustedSHA256;
        EnsureHashMatches(item.Artifact.DisplayName, actual, expected);

        bool approved = manifest is not null &&
            (reference is null || ManifestMatches(manifest, reference)) &&
            string.Equals(Path.GetFullPath(manifest.ArchivePath), Path.GetFullPath(path), PathComparison) &&
            string.Equals(manifest.ApprovedSHA256, actual, StringComparison.OrdinalIgnoreCase);
        bool changed = manifest?.ApprovedSHA256 is not null &&
            !string.Equals(manifest.ApprovedSHA256, actual, StringComparison.OrdinalIgnoreCase);

        PlanAction action = expected is not null || approved ? PlanAction.VerifyExisting : defaultAction;
        if (approved)
            action = PlanAction.Reuse;

        return new DownloadPlan(item.Artifact, action, path, manifestPath, reference, manifest, changed);
    }

    private static async Task ExecutePlanAsync(
        DownloadPlan plan,
        HttpClient httpClient,
        Func<UntrustedArtifact, bool> approveUntrusted,
        CancellationToken cancellationToken)
    {
        if (plan.Action == PlanAction.Reuse)
            return;

        string actualHash;
        if (plan.Action == PlanAction.Download)
        {
            ArtifactReference reference = plan.Reference
                ?? throw new InvalidOperationException("A download plan has no remote reference.");
            string partial = GetPartialPath(plan.Target);
            try
            {
                actualHash = await DownloadToPartialAsync(httpClient, reference, plan.Target, cancellationToken);
                EnsureHashMatches(plan.Artifact.DisplayName, actualHash, reference.ExpectedSHA256);
                EnsureTransportIntegrity(plan, actualHash);

                if (reference.ExpectedSHA256 is null)
                    RequireUntrustedApproval(plan, actualHash, approveUntrusted);

                File.Move(partial, plan.Target, overwrite: true);
            }
            finally
            {
                if (File.Exists(partial))
                    File.Delete(partial);
            }
        }
        else
        {
            actualHash = FileServices.ComputeSHA256(plan.Target);
            string? expected = plan.Reference?.ExpectedSHA256 ?? plan.Artifact.Source?.TrustedSHA256;
            EnsureHashMatches(plan.Artifact.DisplayName, actualHash, expected);
            if (expected is null)
                RequireUntrustedApproval(plan, actualHash, approveUntrusted);
        }

        await WriteManifestAtomicallyAsync(plan, actualHash, cancellationToken);
    }

    internal static async Task<string> DownloadToPartialAsync(
        HttpClient httpClient,
        ArtifactReference reference,
        string target,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        string partial = GetPartialPath(target);
        if (File.Exists(partial))
            File.Delete(partial);

        TimeSpan requestTimeout = timeout ?? DefaultRequestTimeout;
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(requestTimeout);
        CancellationToken requestToken = timeoutSource.Token;

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(
                reference.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken);
            response.EnsureSuccessStatusCode();

            long? expectedLength = response.Content.Headers.ContentLength ?? reference.ContentLength ?? reference.ExpectedSize;
            if (expectedLength > MaximumArchiveDownloadBytes)
                throw new InvalidDataException($"The archive exceeds the {MaximumArchiveDownloadBytes} byte download limit.");

            long reservedBytes = reference.ExpectedSize ?? expectedLength ?? 0;
            if (reservedBytes > 0)
            {
                string downloadDirectory = Path.GetDirectoryName(Path.GetFullPath(target))
                    ?? throw new InvalidDataException("The download target has no parent directory.");
                long available = FileSystemSpace.GetAvailableBytes(downloadDirectory);
                if (available < reservedBytes + DownloadSpaceMarginBytes)
                    throw new IOException(
                        $"Not enough free space to download {reference.Name}: {reservedBytes} bytes are required and {available} are available.");
            }

            long bytesWritten = 0;
            using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream input = await response.Content.ReadAsStreamAsync(requestToken);
            await using FileStream output = new(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);

            byte[] buffer = new byte[81920];
            while (true)
            {
                int read = await input.ReadAsync(buffer, requestToken);
                if (read == 0)
                    break;
                await output.WriteAsync(buffer.AsMemory(0, read), requestToken);
                hasher.AppendData(buffer, 0, read);
                bytesWritten += read;
                if (bytesWritten > MaximumArchiveDownloadBytes)
                    throw new InvalidDataException($"The archive exceeded the {MaximumArchiveDownloadBytes} byte download limit.");
            }
            await output.FlushAsync(requestToken);

            if (reference.ExpectedSize is long pinnedSize && bytesWritten != pinnedSize)
                throw new InvalidDataException(
                    $"The download of {reference.Name} has the wrong size: expected {pinnedSize} bytes, received {bytesWritten}.");

            if (expectedLength is not null && bytesWritten != expectedLength.Value)
                throw new InvalidDataException($"The download was truncated: expected {expectedLength.Value} bytes, received {bytesWritten}.");

            return Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch (Exception ex)
        {
            if (File.Exists(partial))
                File.Delete(partial);

            if (ex is OperationCanceledException &&
                !cancellationToken.IsCancellationRequested &&
                timeoutSource.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The download of {reference.Name} timed out after {requestTimeout}.",
                    ex);
            }

            throw;
        }
    }

    private static void RequireUntrustedApproval(
        DownloadPlan plan,
        string actualHash,
        Func<UntrustedArtifact, bool> approveUntrusted)
    {
        ArtifactReference? reference = plan.Reference;
        UntrustedArtifact warning = new(
            plan.Artifact.DisplayName,
            reference?.SourceDescription ?? "local/offline archive",
            reference?.VersionTag ?? "unknown",
            reference?.Name ?? Path.GetFileName(plan.Target),
            actualHash,
            plan.BytesChanged ||
                (plan.PreviousManifest?.ApprovedSHA256 is string previousHash &&
                 !string.Equals(previousHash, actualHash, StringComparison.OrdinalIgnoreCase)));

        if (!approveUntrusted(warning))
            throw new OperationCanceledException($"Approval was declined for unverified artifact {plan.Artifact.DisplayName}.");
    }

    private static bool PromptForUntrustedArtifact(UntrustedArtifact artifact)
    {
        Controls.WriteWarning(artifact.BytesChanged
            ? $"The bytes for {artifact.DisplayName} changed since the last approval."
            : $"No trusted checksum is published for {artifact.DisplayName}.");
        Controls.WriteInfo($"Source: {artifact.Source}");
        Controls.WriteInfo($"Release: {artifact.Release}");
        Controls.WriteInfo($"Asset: {artifact.Asset}");
        Controls.WriteInfo($"Computed SHA-256: {artifact.SHA256}");
        return Controls.Confirm("Use these exact unverified bytes?", defaultValue: false, warning: true);
    }

    internal static void EnsureHashMatches(string displayName, string actual, string? expected)
    {
        if (expected is not null && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The SHA-256 for {displayName} does not match its trusted checksum.");
    }

    private static bool ManifestMatches(DownloadManifest? manifest, ArtifactReference reference) =>
        manifest is not null &&
        string.Equals(manifest.VersionTag, reference.VersionTag, StringComparison.Ordinal) &&
        string.Equals(manifest.AssetName, reference.Name, StringComparison.Ordinal);

    private static void EnsureTransportIntegrity(DownloadPlan plan, string actualHash)
    {
        string? transport = plan.Reference?.TransportSHA256;
        if (transport is null)
            return;
        if (!string.Equals(transport, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The bytes received for {plan.Artifact.DisplayName} do not match the digest reported by the source. The transfer is corrupt.");
    }

    internal static async Task<string?> GetCachedArchivePathAsync(
        ArtifactDefinition artifact,
        string downloadRoot,
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(downloadRoot, FileServices.ValidateIdentifier(artifact.ID));
        if (!Directory.Exists(root))
            return null;

        DownloadManifest? manifest = await ReadManifestAsync(Path.Combine(root, "download-manifest.json"), cancellationToken);
        if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.ArchivePath) && File.Exists(manifest.ArchivePath))
            return manifest.ArchivePath;

        string[] candidates = [..Directory.EnumerateFiles(root)
            .Where(path => !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                           !path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))];
        return candidates.Length == 1 ? Path.GetFullPath(candidates[0]) : null;
    }

    private static async Task<DownloadManifest?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        // A manifest is attacker-writable if the cache directory is shared. Reject
        // links, oversized files, and any archive path that escapes the artifact root.
        FileInfo info = new(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > MaximumManifestBytes)
            return null;
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            DownloadManifest? manifest = await JsonSerializer.DeserializeAsync<DownloadManifest>(stream, cancellationToken: cancellationToken);
            if (!IsValidManifest(manifest))
                return null;

            string root = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidDataException("The download manifest has no parent directory.");
            string? resolved = ResolveManifestArchivePath(root, manifest!.ArchivePath);
            if (resolved is null)
                return null;

            manifest.ArchivePath = resolved;
            return manifest;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ResolveManifestArchivePath(string artifactRoot, string archivePath)
    {
        try
        {
            string fullRoot = Path.GetFullPath(artifactRoot);
            string candidate = Path.IsPathFullyQualified(archivePath)
                ? Path.GetFullPath(archivePath)
                : PathSafety.ResolveInside(fullRoot, archivePath);
            if (!PathSafety.IsWithinRoot(fullRoot, candidate))
                return null;

            // A link inside the cache can still point anywhere.
            FileInfo info = new(candidate);
            return info.LinkTarget is null ? candidate : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsValidManifest(DownloadManifest? manifest)
    {
        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.VersionTag) ||
            string.IsNullOrWhiteSpace(manifest.AssetName) ||
            string.IsNullOrWhiteSpace(manifest.ArchivePath) ||
            !IsSha256(manifest.ApprovedSHA256) ||
            manifest.TrustedSHA256 is not null && !IsSha256(manifest.TrustedSHA256))
        {
            return false;
        }

        // A trusted hash is only ever written from a catalog pin, and a pin must
        // match the bytes. Any divergence means the manifest was edited or corrupted.
        if (manifest.TrustedSHA256 is not null &&
            !string.Equals(manifest.TrustedSHA256, manifest.ApprovedSHA256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            _ = PathSafety.NormalizeRelativePath(manifest.ArchivePath);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        return true;
    }

    private static bool IsSha256(string? value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static async Task WriteManifestAtomicallyAsync(
        DownloadPlan plan,
        string actualHash,
        CancellationToken cancellationToken)
    {
        DownloadManifest manifest = new()
        {
            VersionTag = plan.Reference?.VersionTag ?? "local",
            AssetName = plan.Reference?.Name ?? Path.GetFileName(plan.Target),
            ArchivePath = Path.GetRelativePath(
                Path.GetDirectoryName(plan.ManifestPath)
                    ?? throw new InvalidOperationException("The download manifest has no parent directory."),
                plan.Target),
            TrustedSHA256 = plan.Artifact.Source?.TrustedSHA256,
            ApprovedSHA256 = actualHash,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        string temporary = plan.ManifestPath + $".{Guid.NewGuid():N}.partial";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, plan.ManifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string GetPartialPath(string target) => target + ".partial";

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BadBuilder", "2"));
        return client;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record ResolvedArtifact(ArtifactDefinition Artifact, ArtifactReference? Reference, string? ResolutionError);

    private sealed record DownloadPlan(
        ArtifactDefinition Artifact,
        PlanAction Action,
        string Target,
        string ManifestPath,
        ArtifactReference? Reference,
        DownloadManifest? PreviousManifest,
        bool BytesChanged);

    private sealed class DownloadManifest
    {
        public string VersionTag { get; set; } = string.Empty;
        public string AssetName { get; set; } = string.Empty;
        public string ArchivePath { get; set; } = string.Empty;
        public string? TrustedSHA256 { get; set; }
        public string? ApprovedSHA256 { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private enum PlanAction
    {
        Reuse,
        VerifyExisting,
        Use,
        Download,
    }
}

internal sealed record DownloadResult(
    int DownloadedCount,
    IReadOnlyList<(ArtifactDefinition Artifact, string ArchivePath)> Artifacts);
