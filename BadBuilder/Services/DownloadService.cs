using Octokit;
using BadBuilder.UI;
using Spectre.Console;
using System.Text.Json;
using BadBuilder.Configuration;

namespace BadBuilder.Services;

internal static class DownloadService
{
    private static readonly HttpClient HttpClient     = new();
    private static readonly GitHubClient GitHubClient = new(new ProductHeaderValue("BadBuilder"));

    internal static async Task<DownloadResult> DownloadAsync(
        IReadOnlyList<ArtifactDefinition> artifacts,
        string downloadRoot,
        CancellationToken cancellationToken)
    {
        Controls.WriteInfo("Resolving latest releases.");

        var resolvedArtifacts = await ResolveArtifactsAsync(artifacts, cancellationToken);
        var overrides         = await CollectLocalOverridesAsync(artifacts, resolvedArtifacts, downloadRoot, cancellationToken);
        var plans             = await BuildDownloadPlansAsync(resolvedArtifacts, overrides, downloadRoot, cancellationToken);

        await RunDownloadsAsync(resolvedArtifacts, plans, cancellationToken);
        return BuildResult(resolvedArtifacts, plans);
    }


    private static async Task<IReadOnlyList<ResolvedArtifact>> ResolveArtifactsAsync(IReadOnlyList<ArtifactDefinition> artifacts, CancellationToken cancellationToken)
    {
        List<ResolvedArtifact> resolved = new(artifacts.Count);

        foreach (var artifact in artifacts)
        {
            if (artifact.Source is null)
            {
                resolved.Add(new ResolvedArtifact(artifact, null));
                continue;
            }

            try
            {
                ArtifactReference release = await ResolveReleaseAsync(artifact, cancellationToken);
                resolved.Add(new ResolvedArtifact(artifact, release));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                resolved.Add(new ResolvedArtifact(artifact, null));
                Controls.WriteWarning($"{artifact.DisplayName} could not be resolved online: {ex.Message}");
            }
        }

        return resolved;
    }


    private static async Task<Dictionary<string, string>> CollectLocalOverridesAsync(
        IReadOnlyList<ArtifactDefinition> artifacts,
        IReadOnlyList<ResolvedArtifact> resolvedArtifacts,
        string downloadRoot,
        CancellationToken cancellationToken)
    {
        var overrides = artifacts
            .Where(artifact => !string.IsNullOrWhiteSpace(artifact.LocalArchivePath))
            .ToDictionary(artifact => artifact.ID, artifact => artifact.LocalArchivePath!, StringComparer.OrdinalIgnoreCase);

        var promptedOverrides = await PromptForUnavailableAssetsAsync(resolvedArtifacts, downloadRoot, cancellationToken);

        foreach (var (artifactId, path) in promptedOverrides)
            overrides[artifactId] = path;

        return overrides;
    }

    private static async Task<DownloadPlan?[]> BuildDownloadPlansAsync(
        IReadOnlyList<ResolvedArtifact> resolvedArtifacts,
        Dictionary<string, string> overrides,
        string downloadRoot,
        CancellationToken cancellationToken)
    {
        var plans = new DownloadPlan?[resolvedArtifacts.Count];

        for (int index = 0; index < resolvedArtifacts.Count; index++)
        {
            ResolvedArtifact item = resolvedArtifacts[index];
            string? overridePath  = overrides.TryGetValue(item.Artifact.ID, out var path) ? path : null;

            bool canSkipEntirely = item.Release is null
                && !IsRequiredArtifact(item.Artifact)
                && string.IsNullOrWhiteSpace(overridePath)
                && await GetCachedArchivePathAsync(item.Artifact, downloadRoot, cancellationToken) is null;

            if (canSkipEntirely)
                continue;

            plans[index] = await CreatePlanAsync(item, downloadRoot, overridePath, cancellationToken);
        }

        return plans;
    }

    private static async Task RunDownloadsAsync(IReadOnlyList<ResolvedArtifact> resolvedArtifacts, DownloadPlan?[] plans, CancellationToken cancellationToken)
    {
        ProgressOperation[] work = [..plans
            .Select((plan, index) => (plan, index))
            .Where(item           => item.plan?.Action == PlanAction.Download)
            .Select(item          => new ProgressOperation(
                resolvedArtifacts[item.index].Artifact.DisplayName,
                (progress, token) => ExecutePlanAsync(item.plan!, progress, token)
                )
            )];

        if (work.Length == 0)
            return;

        Controls.WriteInfo("Downloading required files.");
        await Controls.RunProgressAsync(work, cancellationToken);
    }

    private static DownloadResult BuildResult(IReadOnlyList<ResolvedArtifact> resolvedArtifacts, DownloadPlan?[] plans)
    {
        int downloadedCount = plans.Count(plan => plan?.Action == PlanAction.Download);

        var artifactPaths = plans
            .Select((plan, index) => (plan, index))
            .Where(item => item.plan is not null)
            .Select(item => (resolvedArtifacts[item.index].Artifact, ArchivePath: item.plan!.Target))
            .ToArray();

        return new DownloadResult(downloadedCount, artifactPaths);
    }


    private static async Task<Dictionary<string, string>> PromptForUnavailableAssetsAsync(
        IReadOnlyList<ResolvedArtifact> resolvedArtifacts,
        string downloadRoot,
        CancellationToken cancellationToken)
    {
        List<ArchivePathEntry> entries = [];

        foreach (var item in resolvedArtifacts.Where(item => item.Release is null))
        {
            if (!string.IsNullOrWhiteSpace(item.Artifact.LocalArchivePath))
                continue;

            string? cachedPath = await GetCachedArchivePathAsync(item.Artifact, downloadRoot, cancellationToken);

            if (cachedPath is not null && !IsRequiredArtifact(item.Artifact))
                continue;

            entries.Add(new ArchivePathEntry(item.Artifact.ID, item.Artifact.DisplayName, cachedPath, IsRequiredArtifact(item.Artifact)));
        }

        if (entries.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return Controls.PromptArchivePathOverrides("Review local archive paths", entries, "Set the paths of any required assets.");
    }


    private static async Task<DownloadPlan> CreatePlanAsync(ResolvedArtifact item, string downloadRoot, string? localOverride, CancellationToken cancellationToken)
    {
        string artifactRoot = Path.Combine(downloadRoot, item.Artifact.ID);
        Directory.CreateDirectory(artifactRoot);

        string manifestPath        = Path.Combine(artifactRoot, "download-manifest.json");
        DownloadManifest? manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        string? overridePath       = NormalizeFullPath(localOverride);

        if (item.Release is null)
            return CreateOfflinePlan(item.Artifact, manifest, artifactRoot, manifestPath, overridePath);
        else
            return CreateOnlinePlan(item.Artifact, item.Release, manifest, artifactRoot, manifestPath, overridePath);
    }

    private static DownloadPlan CreateOfflinePlan(
        ArtifactDefinition artifact,
        DownloadManifest? manifest,
        string artifactRoot,
        string manifestPath,
        string? overridePath)
    {
        string? cachedPath = manifest is null ? null : GetManifestArchivePath(manifest, artifactRoot);

        if (cachedPath is not null && File.Exists(cachedPath) && (overridePath is null || IsSamePath(overridePath, cachedPath)))
            return new DownloadPlan(PlanAction.Reuse, cachedPath, cachedPath, manifestPath, null);

        if (overridePath is null)
            throw new InvalidOperationException($"No cached copy is available for {artifact.DisplayName}. Provide a local archive path in the review table.");

        FileServices.EnsureFile(overridePath);
        return new DownloadPlan(PlanAction.Use, overridePath, overridePath, manifestPath, null);
    }

    private static DownloadPlan CreateOnlinePlan(
        ArtifactDefinition artifact,
        ArtifactReference release,
        DownloadManifest? manifest,
        string artifactRoot,
        string manifestPath,
        string? overridePath)
    {
        string archivePath = Path.Combine(artifactRoot, release.Name);

        if (overridePath is not null && IsSamePath(overridePath, archivePath) && File.Exists(archivePath))
            overridePath = null;

        if (overridePath is not null)
        {
            FileServices.EnsureFile(overridePath);
            EnsureHashMatches(artifact.DisplayName, FileServices.ComputeSHA256(overridePath), release.ExpectedSHA256);
            return new DownloadPlan(PlanAction.Use, overridePath, overridePath, manifestPath, release);
        }

        if (File.Exists(archivePath) && IsArchiveStillValid(archivePath, release, manifest))
            return new DownloadPlan(PlanAction.Reuse, archivePath, archivePath, manifestPath, release);

        return new DownloadPlan(PlanAction.Download, release.DownloadUrl, archivePath, manifestPath, release);
    }

    private static bool IsArchiveStillValid(string archivePath, ArtifactReference release, DownloadManifest? manifest)
    {
        if (release.ExpectedSHA256 is not null)
        {
            return string.Equals(
                FileServices.ComputeSHA256(archivePath),
                release.ExpectedSHA256,
                StringComparison.OrdinalIgnoreCase);
        }

        return manifest is not null && string.Equals(manifest.VersionTag, release.VersionTag, StringComparison.OrdinalIgnoreCase);
    }


    private static async Task<ArtifactReference> ResolveReleaseAsync(ArtifactDefinition artifact, CancellationToken cancellationToken)
    {
        Source source = artifact.Source ?? throw new InvalidOperationException($"No source is configured for {artifact.DisplayName}.");

        return source switch
        {
            GitHubReleaseSource githubSource => await ResolveGitHubReleaseAsync(artifact, githubSource, cancellationToken),
            DirectSource directSource        => ResolveDirectSource(artifact, directSource),
            _                                => throw new InvalidOperationException($"Unsupported source type for {artifact.DisplayName}: {source.GetType().Name}."),
        };
    }

    private static async Task<ArtifactReference> ResolveGitHubReleaseAsync(ArtifactDefinition artifact, GitHubReleaseSource source, CancellationToken cancellationToken)
    {
        try
        {
            Release release = await GetGitHubReleaseAsync(source);

            ReleaseAsset asset = (source.AssetName is null
                ? release.Assets.FirstOrDefault(asset => !IsChecksumAsset(asset.Name))
                : release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, source.AssetName, StringComparison.OrdinalIgnoreCase))) 
                    ?? throw new InvalidOperationException($"No downloadable asset found for {source.Owner}/{source.Repo}.");

            return new ArtifactReference(release.TagName, asset.Name, asset.BrowserDownloadUrl, ParseSHA256(release.Body));
        }
        catch (NotFoundException)
        {
            throw new InvalidOperationException($"GitHub release could not be found for {artifact.DisplayName}. Check the owner, repository, and release tag in the catalog.");
        }
    }

    private static async Task<Release> GetGitHubReleaseAsync(GitHubReleaseSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.ReleaseTag))
            return await GitHubClient.Repository.Release.Get(source.Owner, source.Repo, source.ReleaseTag);

        try
        {
            return await GitHubClient.Repository.Release.GetLatest(source.Owner, source.Repo);
        }
        catch (NotFoundException)
        {
            var allReleases = await GitHubClient.Repository.Release.GetAll(source.Owner, source.Repo);
            return allReleases.Count > 0 
                ? allReleases[0]
                : throw new InvalidOperationException($"No releases found for {source.Owner}/{source.Repo}.");
        }
    }

    private static ArtifactReference ResolveDirectSource(ArtifactDefinition artifact, DirectSource source)
    {
        if (string.IsNullOrWhiteSpace(source.URL))
            throw new InvalidOperationException($"The direct download URL for {artifact.DisplayName} is empty.");

        Uri uri;
        try
        {
            uri = new Uri(source.URL, UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            throw new InvalidOperationException($"The direct download URL for {artifact.DisplayName} is invalid: {source.URL}");
        }

        string fileName = Path.GetFileName(uri.GetLeftPart(UriPartial.Path));

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"{artifact.ID}.bin";

        return new ArtifactReference("direct", fileName, source.URL, null);
    }


    private static async Task ExecutePlanAsync(DownloadPlan plan, ProgressTask? progress, CancellationToken cancellationToken)
    {
        if (plan.Action == PlanAction.Download)
            await DownloadArchiveAsync(plan.Source, plan.Target, progress, cancellationToken);

        string actualSHA256 = FileServices.ComputeSHA256(plan.Target);
        EnsureHashMatches("downloaded archive", actualSHA256, plan.Release?.ExpectedSHA256);
        await WriteManifestAsync(plan.ManifestPath, plan.Release?.VersionTag ?? "local", plan.Target, plan.Release?.ExpectedSHA256, actualSHA256, cancellationToken);
    }

    private static async Task DownloadArchiveAsync(string url, string target, ProgressTask? progress, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        progress?.MaxValue = response.Content.Headers.ContentLength ?? 1;

        await using Stream input      = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream output = new(target, System.IO.FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        byte[] buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            progress?.Increment(bytesRead);
        }
    }


    private static bool IsChecksumAsset(string name) =>
        name.Contains("sha256", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("checksum", StringComparison.OrdinalIgnoreCase);

    private static string? ParseSHA256(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        int marker = body.IndexOf("sha256:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return null;

        string hash = body[(marker + "sha256:".Length)..].TrimStart();
        return hash.Length >= 64 && hash[..64].All(Uri.IsHexDigit) ? hash[..64].ToUpperInvariant() : null;
    }

    private static void EnsureHashMatches(string name, string actualSHA256, string? expectedSHA256)
    {
        if (expectedSHA256 is not null && !string.Equals(actualSHA256, expectedSHA256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The SHA-256 for {name} does not match the expected release hash.");
    }


    private static async Task<string?> GetCachedArchivePathAsync(ArtifactDefinition artifact, string downloadRoot, CancellationToken cancellationToken)
    {
        string root = Path.Combine(downloadRoot, artifact.ID);

        DownloadManifest? manifest = await ReadManifestAsync(Path.Combine(root, "download-manifest.json"), cancellationToken);
        if (manifest is null)
            return null;

        string path = GetManifestArchivePath(manifest, root);
        return File.Exists(path) ? path : null;
    }

    private static string GetManifestArchivePath(DownloadManifest manifest, string artifactRoot) =>
        string.IsNullOrWhiteSpace(manifest.ArchivePath)
            ? Path.Combine(artifactRoot, manifest.ArchiveName)
            : manifest.ArchivePath;

    private static async Task<DownloadManifest?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        string json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<DownloadManifest>(json);
    }

    private static async Task WriteManifestAsync(
        string path,
        string versionTag,
        string archivePath,
        string? expectedSHA256,
        string actualSHA256,
        CancellationToken cancellationToken)
    {
        DownloadManifest manifest = new()
        {
            VersionTag      = versionTag,
            ArchivePath     = archivePath,
            ExpectedSHA256  = expectedSHA256,
            SHA256          = actualSHA256,
            DownloadedAtUtc = DateTimeOffset.UtcNow,
        };

        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }


    private static bool IsRequiredArtifact(ArtifactDefinition artifact) =>
        !artifact.DestinationRelativePath.StartsWith("homebrew/", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeFullPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(FileServices.NormalizeUserPath(path));

    private static bool IsSamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);


    private sealed class DownloadManifest
    {
        public string VersionTag              { get; set; } = string.Empty;
        public string ArchivePath             { get; set; } = string.Empty;
        public string ArchiveName             { get; set; } = string.Empty;
        public string? ExpectedSHA256         { get; set; }
        public string? SHA256                 { get; set; }
        public DateTimeOffset DownloadedAtUtc { get; set; }
    }

    private sealed record ResolvedArtifact(ArtifactDefinition Artifact, ArtifactReference? Release);
    private sealed record DownloadPlan(PlanAction Action, string Source, string Target, string ManifestPath, ArtifactReference? Release);
    private sealed record ArtifactReference(string VersionTag, string Name, string DownloadUrl, string? ExpectedSHA256);
    private enum PlanAction { Reuse, Use, Download }
}

internal sealed record DownloadResult(int DownloadedCount, IReadOnlyList<(ArtifactDefinition Artifact, string ArchivePath)> Artifacts);