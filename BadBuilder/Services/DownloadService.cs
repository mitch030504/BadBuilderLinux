using System.Text.Json;
using BadBuilder.Configuration;
using BadBuilder.UI;
using Octokit;

namespace BadBuilder.Services;

internal static class DownloadService
{
    private static readonly HttpClient HttpClient = new();
    private static readonly GitHubClient GitHubClient = new(new ProductHeaderValue("BadBuilder"));

    public static async Task<DownloadResult> DownloadAsync(IReadOnlyList<ArtifactDefinition> artifacts, string downloadRoot, CancellationToken cancellationToken)
    {
        Controls.PadLine();
        Controls.WriteInfo("Resolving latest releases.");

        var resolved = new List<(ArtifactDefinition Artifact, ArtifactReference? Release)>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            if (artifact.Source is null)
            {
                resolved.Add((artifact, null));
                continue;
            }

            try
            {
                resolved.Add((artifact, await ResolveReleaseAsync(artifact, cancellationToken)));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                resolved.Add((artifact, null));
                Controls.WriteWarning($"{artifact.DisplayName} could not be resolved online: {ex.Message}");
            }
        }

        var overrides = artifacts
            .Where(artifact => !string.IsNullOrWhiteSpace(artifact.LocalArchivePath))
            .ToDictionary(artifact => artifact.ID, artifact => artifact.LocalArchivePath!, StringComparer.OrdinalIgnoreCase);
        foreach (var overridePath in await PromptForUnavailableAssetsAsync(resolved, downloadRoot, cancellationToken))
            overrides[overridePath.Key] = overridePath.Value;
        var plans = new DownloadPlan?[resolved.Count];
        for (var index = 0; index < resolved.Count; index++)
        {
            var item = resolved[index];
            var overridePath = overrides.TryGetValue(item.Artifact.ID, out var path) ? path : null;
            if (item.Release is null && !IsRequiredArtifact(item.Artifact) && string.IsNullOrWhiteSpace(overridePath) &&
                await GetCachedArchivePathAsync(item.Artifact, downloadRoot, cancellationToken) is null)
                continue;

            plans[index] = await CreatePlanAsync(item, downloadRoot, overridePath, cancellationToken);
        }

        var work = plans.Select((plan, index) => (plan, index))
            .Where(item => item.plan?.Action == PlanAction.Download)
            .Select(item => new ProgressOperation(
                resolved[item.index].Artifact.DisplayName,
                (progress, token) => ExecutePlanAsync(item.plan!, progress, token)))
            .ToArray();

        if (work.Length > 0)
        {
            Controls.WriteInfo("Downloading required files.");
            await Controls.RunProgressAsync(work, cancellationToken);
        }

        return new DownloadResult(
            plans.Count(plan => plan?.Action == PlanAction.Download),
            plans.Select((plan, index) => (plan, index))
                .Where(item => item.plan is not null)
                .Select(item => (resolved[item.index].Artifact, ArchivePath: item.plan!.Target))
                .ToArray());
    }

    private static async Task<Dictionary<string, string>> PromptForUnavailableAssetsAsync(IReadOnlyList<(ArtifactDefinition Artifact, ArtifactReference? Release)> resolved, string downloadRoot, CancellationToken cancellationToken)
    {
        var entries = new List<ArchivePathEntry>();
        foreach (var item in resolved.Where(item => item.Release is null))
        {
            if (!string.IsNullOrWhiteSpace(item.Artifact.LocalArchivePath))
                continue;

            var cachedPath = await GetCachedArchivePathAsync(item.Artifact, downloadRoot, cancellationToken);
            if (cachedPath is not null && !IsRequiredArtifact(item.Artifact))
                continue;

            entries.Add(new(item.Artifact.ID, item.Artifact.DisplayName, cachedPath, IsRequiredArtifact(item.Artifact)));
        }

        if (entries.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return Controls.PromptArchivePathOverrides("Review local archive paths", entries, "Set the paths of any required assets.");
    }

    private static async Task<DownloadPlan> CreatePlanAsync((ArtifactDefinition Artifact, ArtifactReference? Release) item, string downloadRoot, string? localOverride, CancellationToken cancellationToken)
    {
        var artifactRoot = Path.Combine(downloadRoot, item.Artifact.ID);
        Directory.CreateDirectory(artifactRoot);
        var manifestPath = Path.Combine(artifactRoot, "download-manifest.json");
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        var overridePath = NormalizeFullPath(localOverride);

        if (item.Release is null)
        {
            var cachedPath = manifest is null ? null : GetManifestArchivePath(manifest, artifactRoot);
            if (cachedPath is not null && File.Exists(cachedPath) &&
                (overridePath is null || IsSamePath(overridePath, cachedPath)))
                return new(PlanAction.Reuse, cachedPath, cachedPath, manifestPath, null);

            if (overridePath is null)
                throw new InvalidOperationException($"No cached copy is available for {item.Artifact.DisplayName}. Provide a local archive path in the review table.");

            EnsureFileExists(overridePath);
            return new(PlanAction.Use, overridePath, overridePath, manifestPath, null);
        }

        var archivePath = Path.Combine(artifactRoot, item.Release.Name);
        if (overridePath is not null && IsSamePath(overridePath, archivePath) && File.Exists(archivePath))
            overridePath = null;

        if (overridePath is not null)
        {
            EnsureFileExists(overridePath);
            EnsureHashMatches(item.Artifact.DisplayName, FileServices.ComputeSHA256(overridePath), item.Release.ExpectedSha256);
            return new(PlanAction.Use, overridePath, overridePath, manifestPath, item.Release);
        }

        if (File.Exists(archivePath))
        {
            if (item.Release.ExpectedSha256 is not null &&
                string.Equals(FileServices.ComputeSHA256(archivePath), item.Release.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return new(PlanAction.Reuse, archivePath, archivePath, manifestPath, item.Release);

            if (item.Release.ExpectedSha256 is null && manifest is not null &&
                string.Equals(manifest.VersionTag, item.Release.VersionTag, StringComparison.OrdinalIgnoreCase))
                return new(PlanAction.Reuse, archivePath, archivePath, manifestPath, item.Release);
        }

        return new(PlanAction.Download, item.Release.DownloadUrl, archivePath, manifestPath, item.Release);
    }

    private static async Task<ArtifactReference> ResolveReleaseAsync(ArtifactDefinition artifact, CancellationToken cancellationToken)
    {
        GitHubReleaseSource source = artifact.Source
            ?? throw new InvalidOperationException($"No remote source is configured for {artifact.DisplayName}.");
        try
        {
            Release release;

            if (!string.IsNullOrWhiteSpace(source.ReleaseTag))
                release = await GitHubClient.Repository.Release.Get(source.Owner, source.Repo, source.ReleaseTag);
            else
            {
                try
                {
                    release = await GitHubClient.Repository.Release.GetLatest(source.Owner, source.Repo);
                }
                catch (NotFoundException)
                {
                    var allReleases = await GitHubClient.Repository.Release.GetAll(source.Owner, source.Repo);
                    release = allReleases.FirstOrDefault()
                        ?? throw new InvalidOperationException($"No releases found for {source.Owner}/{source.Repo}.");
                }
            }

            ReleaseAsset? asset = source.AssetName is null
                ? release.Assets.FirstOrDefault(asset => !IsChecksumAsset(asset.Name))
                : release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, source.AssetName, StringComparison.OrdinalIgnoreCase));

            return asset is null
                ? throw new InvalidOperationException($"No downloadable asset found for {source.Owner}/{source.Repo}.")
                : new ArtifactReference(release.TagName, asset.Name, asset.BrowserDownloadUrl, ParseSha256(release.Body));
        }
        catch (NotFoundException)
        {
            throw new InvalidOperationException(
                $"GitHub release could not be found for {artifact.DisplayName}. Check the owner, repository, and release tag in the catalog.");
        }
    }

    private static bool IsChecksumAsset(string name) =>
        name.Contains("sha256", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("checksum", StringComparison.OrdinalIgnoreCase);

    private static string? ParseSha256(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var marker = body.IndexOf("sha256:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return null;

        var hash = body[(marker + "sha256:".Length)..].TrimStart();
        return hash.Length >= 64 && hash[..64].All(Uri.IsHexDigit) ? hash[..64].ToUpperInvariant() : null;
    }

    private static async Task ExecutePlanAsync(DownloadPlan plan, IProgressTask? progress, CancellationToken cancellationToken)
    {
        if (plan.Action == PlanAction.Download)
            await DownloadArchiveAsync(plan.Source, plan.Target, progress, cancellationToken);

        var actualSha256 = FileServices.ComputeSHA256(plan.Target);
        EnsureHashMatches("downloaded archive", actualSha256, plan.Release?.ExpectedSha256);
        await WriteManifestAsync(plan.ManifestPath, plan.Release?.VersionTag ?? "local", plan.Target, plan.Release?.ExpectedSha256, actualSha256, cancellationToken);
    }

    private static void EnsureHashMatches(string name, string actualSha256, string? expectedSha256)
    {
        if (expectedSha256 is not null && !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The SHA-256 for {name} does not match the expected release hash.");
    }

    private static async Task<string?> GetCachedArchivePathAsync(ArtifactDefinition artifact, string downloadRoot, CancellationToken cancellationToken)
    {
        var root = Path.Combine(downloadRoot, artifact.ID);
        var manifest = await ReadManifestAsync(Path.Combine(root, "download-manifest.json"), cancellationToken);
        if (manifest is null)
            return null;
        var path = GetManifestArchivePath(manifest, root);
        return File.Exists(path) ? path : null;
    }

    private static string GetManifestArchivePath(DownloadManifest manifest, string artifactRoot) =>
        string.IsNullOrWhiteSpace(manifest.ArchivePath)
            ? Path.Combine(artifactRoot, manifest.ArchiveName)
            : manifest.ArchivePath;

    private static bool IsRequiredArtifact(ArtifactDefinition artifact) => !artifact.DestinationRelativePath.StartsWith("homebrew/", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeFullPath(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(FileServices.NormalizeUserPath(path));

    private static bool IsSamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static void EnsureFileExists(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The selected local archive was not found.", path);
    }

    private static async Task DownloadArchiveAsync(string url, string target, IProgressTask? progress, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        progress?.SetMaxValue(response.Content.Headers.ContentLength ?? 1);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(target, System.IO.FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Increment(read);
        }
    }

    private static async Task<DownloadManifest?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<DownloadManifest>(await File.ReadAllTextAsync(path, cancellationToken));
    }

    private static async Task WriteManifestAsync(string path, string versionTag, string archivePath, string? expectedSha256, string actualSha256, CancellationToken cancellationToken)
    {
        var manifest = new DownloadManifest { VersionTag = versionTag, ArchivePath = archivePath, ExpectedSha256 = expectedSha256, Sha256 = actualSha256, DownloadedAtUtc = DateTimeOffset.UtcNow };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private sealed class DownloadManifest
    {
        public string VersionTag { get; set; } = string.Empty;
        public string ArchivePath { get; set; } = string.Empty;
        public string ArchiveName { get; set; } = string.Empty;
        public string? ExpectedSha256 { get; set; }
        public string? Sha256 { get; set; }
        public DateTimeOffset DownloadedAtUtc { get; set; }
    }

    private sealed record DownloadPlan(PlanAction Action, string Source, string Target, string ManifestPath, ArtifactReference? Release);
    private sealed record ArtifactReference(string VersionTag, string Name, string DownloadUrl, string? ExpectedSha256);
    private enum PlanAction { Reuse, Use, Download }
}

internal sealed record DownloadResult(
    int DownloadedCount,
    IReadOnlyList<(ArtifactDefinition Artifact, string ArchivePath)> Artifacts);