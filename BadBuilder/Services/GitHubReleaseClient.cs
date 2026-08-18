using System.IO.Enumeration;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BadBuilder.Configuration;

namespace BadBuilder.Services;

internal sealed class GitHubReleaseClient
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    internal GitHubReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BadBuilder", "2"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    internal async Task<ArtifactReference> ResolveAsync(
        ArtifactDefinition artifact,
        GitHubReleaseSource source,
        CancellationToken cancellationToken)
    {
        string owner = Uri.EscapeDataString(source.Owner);
        string repo = Uri.EscapeDataString(source.Repo);
        IReadOnlyList<GitHubReleaseInfo> releases;

        if (source.ReleasePolicy == ReleaseSelectionPolicy.ExactTag)
        {
            if (string.IsNullOrWhiteSpace(source.ReleaseTag))
                throw new InvalidOperationException($"{artifact.DisplayName} has an exact-tag policy without a release tag.");

            string tag = Uri.EscapeDataString(source.ReleaseTag);
            GitHubReleaseInfo exact = await GetAsync<GitHubReleaseInfo>(
                $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}", cancellationToken);
            releases = [exact];
        }
        else
        {
            releases = await GetAsync<List<GitHubReleaseInfo>>(
                $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100", cancellationToken);
        }

        GitHubReleaseInfo release = SelectRelease(releases, source);
        GitHubAssetInfo asset = SelectAsset(release.Assets, source.AssetPattern, artifact.DisplayName);
        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri? downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"GitHub returned an unsafe asset URL for {artifact.DisplayName}.");
        string? digest = ParseDigest(asset.Digest);
        string? expectedHash = ValidatePinnedHash(source.TrustedSHA256, artifact.DisplayName) ?? digest;

        return new ArtifactReference(
            release.TagName,
            asset.Name,
            downloadUri.AbsoluteUri,
            expectedHash,
            $"GitHub {source.Owner}/{source.Repo}",
            asset.Size);
    }

    internal static GitHubReleaseInfo SelectRelease(
        IReadOnlyList<GitHubReleaseInfo> releases,
        GitHubReleaseSource source)
    {
        IEnumerable<GitHubReleaseInfo> candidates = releases.Where(release => !release.Draft);

        candidates = source.ReleasePolicy switch
        {
            ReleaseSelectionPolicy.LatestStable => candidates.Where(release => !release.Prerelease),
            ReleaseSelectionPolicy.LatestIncludingPrerelease => candidates,
            ReleaseSelectionPolicy.ExactTag => candidates.Where(release =>
                string.Equals(release.TagName, source.ReleaseTag, StringComparison.Ordinal)),
            _ => throw new InvalidOperationException($"Unsupported release policy: {source.ReleasePolicy}."),
        };

        return candidates
            .OrderByDescending(release => release.PublishedAt ?? release.CreatedAt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No release matched the configured policy for {source.Owner}/{source.Repo}.");
    }

    internal static GitHubAssetInfo SelectAsset(
        IReadOnlyList<GitHubAssetInfo> assets,
        string pattern,
        string displayName)
    {
        GitHubAssetInfo[] matches = [..assets.Where(asset =>
            FileSystemName.MatchesSimpleExpression(pattern, asset.Name, ignoreCase: true))];

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"No release asset for {displayName} matched '{pattern}'."),
            _ => throw new InvalidOperationException($"Multiple release assets for {displayName} matched '{pattern}': {string.Join(", ", matches.Select(asset => asset.Name))}."),
        };
    }

    internal static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string hash = digest[prefix.Length..];
        return hash.Length == 64 && hash.All(Uri.IsHexDigit) ? hash.ToUpperInvariant() : null;
    }

    internal static string? ValidatePinnedHash(string? hash, string displayName)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return null;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidOperationException($"The catalog SHA-256 for {displayName} is invalid.");
        return hash.ToUpperInvariant();
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub returned {(int)response.StatusCode} ({response.ReasonPhrase}) for release metadata.",
                null,
                response.StatusCode);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken)
            ?? throw new InvalidDataException("GitHub returned an empty release response.");
    }
}

internal sealed record ArtifactReference(
    string VersionTag,
    string Name,
    string DownloadUrl,
    string? ExpectedSHA256,
    string SourceDescription,
    long? ContentLength);

internal sealed record GitHubReleaseInfo(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAssetInfo> Assets);

internal sealed record GitHubAssetInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest);
