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
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly GitHubReleaseClient GitHubClient = new(HttpClient);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    internal static async Task<DownloadResult> DownloadAsync(
        IReadOnlyList<ArtifactDefinition> artifacts,
        string downloadRoot,
        CancellationToken cancellationToken,
        Func<UntrustedArtifact, bool>? approveUntrusted = null)
    {
        approveUntrusted ??= PromptForUntrustedArtifact;
        Directory.CreateDirectory(downloadRoot);
        Controls.WriteInfo("Resolving artifact releases.");

        IReadOnlyList<ResolvedArtifact> resolved = await ResolveArtifactsAsync(artifacts, cancellationToken);
        Dictionary<string, string> overrides = await CollectLocalOverridesAsync(resolved, downloadRoot, cancellationToken);
        List<DownloadPlan> plans = [];

        foreach (ResolvedArtifact item in resolved)
        {
            string? localOverride = overrides.GetValueOrDefault(item.Artifact.ID);
            plans.Add(await CreatePlanAsync(item, downloadRoot, localOverride, cancellationToken));
        }

        int downloaded = 0;
        foreach (DownloadPlan plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Action == PlanAction.Download)
            {
                Controls.WriteInfo($"Downloading {plan.Artifact.DisplayName}.");
                downloaded++;
            }
            await ExecutePlanAsync(plan, approveUntrusted, cancellationToken);
        }

        return new DownloadResult(
            downloaded,
            [..plans.Select(plan => (plan.Artifact, ArchivePath: plan.Target))]);
    }

    private static async Task<IReadOnlyList<ResolvedArtifact>> ResolveArtifactsAsync(
        IReadOnlyList<ArtifactDefinition> artifacts,
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
                ArtifactReference reference = await ResolveReferenceAsync(artifact, cancellationToken);
                resolved.Add(new ResolvedArtifact(artifact, reference, null));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or TaskCanceledException)
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
        CancellationToken cancellationToken)
    {
        return artifact.Source switch
        {
            GitHubReleaseSource source => await GitHubClient.ResolveAsync(artifact, source, cancellationToken),
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
            null);
    }

    private static async Task<Dictionary<string, string>> CollectLocalOverridesAsync(
        IReadOnlyList<ResolvedArtifact> resolved,
        string downloadRoot,
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

        Dictionary<string, string> prompted = Controls.PromptArchivePathOverrides(
            "Required artifacts are unavailable online",
            missing,
            "Provide a local archive for every configured artifact. Nothing will be skipped.");
        foreach ((string id, string path) in prompted)
            paths[id] = Path.GetFullPath(FileServices.NormalizeUserPath(path));
        return paths;
    }

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
            return CreateExistingPlan(item, Path.GetFullPath(localOverride), manifestPath, manifest, PlanAction.Use);
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

    private static DownloadPlan CreateExistingPlan(
        ResolvedArtifact item,
        string path,
        string manifestPath,
        DownloadManifest? manifest,
        PlanAction defaultAction)
    {
        ArtifactReference? reference = item.Reference;
        string actual = FileServices.ComputeSHA256(path);
        string? expected = reference?.ExpectedSHA256 ?? item.Artifact.Source?.TrustedSHA256 ?? manifest?.TrustedSHA256;
        EnsureHashMatches(item.Artifact.DisplayName, actual, expected);

        bool trusted = expected is not null;
        bool approved = !trusted && manifest is not null &&
            string.Equals(Path.GetFullPath(manifest.ArchivePath), Path.GetFullPath(path), PathComparison) &&
            string.Equals(manifest.ApprovedSHA256, actual, StringComparison.OrdinalIgnoreCase);
        bool changed = manifest?.ApprovedSHA256 is not null &&
            !string.Equals(manifest.ApprovedSHA256, actual, StringComparison.OrdinalIgnoreCase);

        PlanAction action = trusted || approved ? PlanAction.VerifyExisting : defaultAction;
        if (approved)
            action = PlanAction.Reuse;

        return new DownloadPlan(item.Artifact, action, path, manifestPath, reference, manifest, changed);
    }

    private static async Task ExecutePlanAsync(
        DownloadPlan plan,
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
                actualHash = await DownloadToPartialAsync(HttpClient, reference, plan.Target, cancellationToken);
                EnsureHashMatches(plan.Artifact.DisplayName, actualHash, reference.ExpectedSHA256);

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
            string? expected = plan.Reference?.ExpectedSHA256 ?? plan.Artifact.Source?.TrustedSHA256 ?? plan.PreviousManifest?.TrustedSHA256;
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
        CancellationToken cancellationToken)
    {
        string partial = GetPartialPath(target);
        if (File.Exists(partial))
            File.Delete(partial);

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(
                reference.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            long? expectedLength = response.Content.Headers.ContentLength ?? reference.ContentLength;
            if (expectedLength > MaximumArchiveDownloadBytes)
                throw new InvalidDataException($"The archive exceeds the {MaximumArchiveDownloadBytes} byte download limit.");
            long bytesWritten = 0;
            using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
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
                int read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hasher.AppendData(buffer, 0, read);
                bytesWritten += read;
                if (bytesWritten > MaximumArchiveDownloadBytes)
                    throw new InvalidDataException($"The archive exceeded the {MaximumArchiveDownloadBytes} byte download limit.");
            }
            await output.FlushAsync(cancellationToken);

            if (expectedLength is not null && bytesWritten != expectedLength.Value)
                throw new InvalidDataException($"The download was truncated: expected {expectedLength.Value} bytes, received {bytesWritten}.");

            return Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch
        {
            if (File.Exists(partial))
                File.Delete(partial);
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

    internal static async Task<string?> GetCachedArchivePathAsync(
        ArtifactDefinition artifact,
        string downloadRoot,
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(downloadRoot, FileServices.ValidateIdentifier(artifact.ID));
        if (!Directory.Exists(root))
            return null;

        DownloadManifest? manifest = await ReadManifestAsync(Path.Combine(root, "download-manifest.json"), cancellationToken);
        if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.ArchivePath))
        {
            string path = Path.IsPathFullyQualified(manifest.ArchivePath)
                ? manifest.ArchivePath
                : Path.Combine(root, manifest.ArchivePath);
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }

        string[] candidates = [..Directory.EnumerateFiles(root)
            .Where(path => !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                           !path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))];
        return candidates.Length == 1 ? Path.GetFullPath(candidates[0]) : null;
    }

    private static async Task<DownloadManifest?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            DownloadManifest? manifest = await JsonSerializer.DeserializeAsync<DownloadManifest>(stream, cancellationToken: cancellationToken);
            if (!IsValidManifest(manifest))
                return null;
            return manifest;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
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

        _ = Path.GetFullPath(manifest.ArchivePath);
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
            ArchivePath = plan.Target,
            TrustedSHA256 = plan.Reference is null
                ? plan.Artifact.Source?.TrustedSHA256 ?? plan.PreviousManifest?.TrustedSHA256
                : plan.Reference.ExpectedSHA256 ?? plan.Artifact.Source?.TrustedSHA256,
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
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
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
