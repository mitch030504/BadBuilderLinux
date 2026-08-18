using BadBuilder.Configuration;
using BadBuilder.Services;

namespace BadBuilder.Tests;

public sealed class LiveSourceSmokeTests
{
    [Fact]
    public async Task OptIn_AllCatalogSourcesResolveWithoutDownloadingAssets()
    {
        if (Environment.GetEnvironmentVariable("BADBUILDER_LIVE_SMOKE") != "1")
            return;

        using HttpClient client = new();
        GitHubReleaseClient github = new(client);
        foreach (ArtifactDefinition artifact in ArtifactCatalog.GetAllBuiltInArtifacts())
        {
            switch (artifact.Source)
            {
                case GitHubReleaseSource source:
                    ArtifactReference reference = await github.ResolveAsync(artifact, source, CancellationToken.None);
                    Assert.StartsWith("https://", reference.DownloadUrl, StringComparison.Ordinal);
                    break;
                case DirectSource source:
                    using (HttpRequestMessage request = new(HttpMethod.Head, source.URL))
                    using (HttpResponseMessage response = await client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None))
                    {
                        Assert.True(response.IsSuccessStatusCode, $"{artifact.DisplayName}: {(int)response.StatusCode}");
                    }
                    break;
            }
        }
    }
}
