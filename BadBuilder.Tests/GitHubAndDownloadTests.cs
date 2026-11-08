using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BadBuilder.Configuration;
using BadBuilder.Services;

namespace BadBuilder.Tests;

public sealed class GitHubAndDownloadTests
{
    [Fact]
    public void ReleaseSelection_UsesLatestStableAndCanIncludePrerelease()
    {
        GitHubReleaseInfo stable = Release("stable", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        GitHubReleaseInfo prerelease = Release("preview", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), prerelease: true);
        GitHubReleaseInfo draft = Release("draft", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), draft: true);

        GitHubReleaseInfo selectedStable = GitHubReleaseClient.SelectRelease(
            [stable, prerelease, draft],
            new GitHubReleaseSource("o", "r", "*.zip"));
        GitHubReleaseInfo selectedPreview = GitHubReleaseClient.SelectRelease(
            [stable, prerelease, draft],
            new GitHubReleaseSource("o", "r", "*.zip", ReleaseSelectionPolicy.LatestIncludingPrerelease));

        Assert.Equal("stable", selectedStable.TagName);
        Assert.Equal("preview", selectedPreview.TagName);
    }


    [Fact]
    public void Catalog_ABadAvatar13Beta_IsPinnedToExpectedReleaseAndDigest()
    {
        ArtifactDefinition artifact = ArtifactCatalog.Exploits[ExploitOption.ABadAvatar13Beta];
        GitHubReleaseSource source = Assert.IsType<GitHubReleaseSource>(artifact.Source);

        Assert.Equal("ABadAvatar 1.3 Beta", artifact.DisplayName);
        Assert.Equal("bibarub", source.Owner);
        Assert.Equal("Xbox360BadUpdate", source.Repo);
        Assert.Equal("ABadAvatar_v1.3-beta.zip", source.AssetPattern);
        Assert.Equal(ReleaseSelectionPolicy.ExactTag, source.ReleasePolicy);
        Assert.Equal("avatar-v1.3-beta", source.ReleaseTag);
        Assert.Equal(
            "A94E0948A5F10AA288BA3A5909B55F90AE56C3CFA628B9D429E178737A0F1E35",
            source.TrustedSHA256);
        Assert.Contains("BadUpdatePayload", artifact.Layout?.RequiredPaths ?? []);
        Assert.Contains("Content", artifact.Layout?.RequiredPaths ?? []);
    }

    [Fact]
    public void Catalog_XeUnshackleMax_PinsReleaseAndOmitsRemoteAccessPlugins()
    {
        ArtifactDefinition artifact = ArtifactCatalog.Bootstraps[BootstrapOption.XeUnshackleMax];
        GitHubReleaseSource source = Assert.IsType<GitHubReleaseSource>(artifact.Source);

        Assert.Equal("XeUnshackle Max", artifact.DisplayName);
        Assert.Equal("klofi", source.Owner);
        Assert.Equal("XeUnshackle-Max", source.Repo);
        Assert.Equal("XeUnshackle-Max-v1.0.0.zip", source.AssetPattern);
        Assert.Equal(ReleaseSelectionPolicy.ExactTag, source.ReleasePolicy);
        Assert.Equal("v1.0.0", source.ReleaseTag);
        Assert.Equal("838CF16450790601158265C22C84D90C1F9FFFF7BA07854B9781722DBA2328E4", source.TrustedSHA256);
        Assert.True(artifact.Layout?.RequireSingleTopLevelDirectory);
        Assert.Equal(2, artifact.Operations?.Count);
        Assert.Equal("BadUpdatePayload", artifact.Operations![0].DestinationPath);
        Assert.Equal("<SUBFOLDER>/BadUpdatePayload", artifact.Operations[0].SourcePath);
        Assert.Contains("BadUpdatePayload/default.xex", artifact.Operations[0].OptionalOverwritePaths ?? []);
        Assert.Empty(artifact.Operations[0].AllowedOverwritePaths ?? []);
        Assert.Equal("launch.ini", artifact.Operations[1].DestinationPath);
        Assert.Equal("<SUBFOLDER>/launch.ini", artifact.Operations[1].SourcePath);
    }

    [Fact]
    public void XeUnshackleVariantsSupportDefaultHomebrewLaunch()
    {
        Assert.True(BootstrapCapabilities.SupportsDefaultHomebrewLaunch(BootstrapOption.XeUnshackle));
        Assert.True(BootstrapCapabilities.SupportsDefaultHomebrewLaunch(BootstrapOption.XeUnshackleMax));
        Assert.False(BootstrapCapabilities.SupportsDefaultHomebrewLaunch(BootstrapOption.FreeMyXe));
    }

    [Fact]
    public void AssetSelection_RequiresExactlyOneGlobMatch()
    {
        GitHubAssetInfo zip = new("release.zip", "https://example/release.zip", 10, null);
        GitHubAssetInfo text = new("notes.txt", "https://example/notes.txt", 2, null);

        Assert.Same(zip, GitHubReleaseClient.SelectAsset([zip, text], "release*.zip", "Artifact"));
        Assert.Throws<InvalidOperationException>(() => GitHubReleaseClient.SelectAsset([zip, text], "*.7z", "Artifact"));
        Assert.Throws<InvalidOperationException>(() => GitHubReleaseClient.SelectAsset(
            [zip, new GitHubAssetInfo("other.zip", "https://example/other.zip", 4, null)], "*.zip", "Artifact"));
    }

    [Theory]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("SHA256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void DigestParsing_AcceptsGitHubSha256Metadata(string digest)
    {
        string? hash = GitHubReleaseClient.ParseDigest(digest);

        Assert.Equal("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", hash);
    }

    [Fact]
    public void ChecksumPolicy_StrictlyRejectsMismatchAndMalformedPins()
    {
        Assert.Throws<InvalidDataException>(() => DownloadService.EnsureHashMatches(
            "Artifact", new string('A', 64), new string('B', 64)));
        Assert.Throws<InvalidOperationException>(() => GitHubReleaseClient.ValidatePinnedHash("not-a-hash", "Artifact"));
    }

    [Fact]
    public void MissingCommand_IsReportedWithoutShellFallback()
    {
        IOException error = Assert.Throws<IOException>(() =>
            ProcessRunner.RequireExecutable($"badbuilder-command-{Guid.NewGuid():N}"));

        Assert.Contains("not installed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptManifest_RecoversSingleCachedArchive()
    {
        using TemporaryDirectory temporary = new();
        ArtifactDefinition artifact = new("artifact", "Artifact", "Test", string.Empty, null);
        string root = Path.Combine(temporary.Path, artifact.ID);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "download-manifest.json"), "{not-json");
        string archive = Path.Combine(root, "artifact.zip");
        await File.WriteAllTextAsync(archive, "cached");

        string? recovered = await DownloadService.GetCachedArchivePathAsync(artifact, temporary.Path, CancellationToken.None);

        Assert.Equal(archive, recovered);
    }

    [Fact]
    public async Task StructurallyCorruptManifest_RecoversSingleCachedArchive()
    {
        using TemporaryDirectory temporary = new();
        ArtifactDefinition artifact = new("artifact", "Artifact", "Test", string.Empty, null);
        string root = Path.Combine(temporary.Path, artifact.ID);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "download-manifest.json"),
            "{\"versionTag\":\"v1\",\"assetName\":\"artifact.zip\",\"archivePath\":\"artifact.zip\",\"approvedSHA256\":\"invalid\"}");
        string archive = Path.Combine(root, "artifact.zip");
        await File.WriteAllTextAsync(archive, "cached");

        string? recovered = await DownloadService.GetCachedArchivePathAsync(artifact, temporary.Path, CancellationToken.None);

        Assert.Equal(archive, recovered);
    }

    [Fact]
    public async Task ChecksumlessCache_RequiresApprovalAndWarnsWhenBytesChange()
    {
        using TemporaryDirectory temporary = new();
        string source = Path.Combine(temporary.Path, "local.zip");
        string cache = Path.Combine(temporary.Path, "cache");
        await File.WriteAllTextAsync(source, "first");
        ArtifactDefinition artifact = new(
            "artifact", "Artifact", "Test", string.Empty, null, LocalArchivePath: source);
        List<UntrustedArtifact> warnings = [];

        await DownloadService.DownloadAsync(
            [artifact], cache, CancellationToken.None, warning => { warnings.Add(warning); return true; });
        await DownloadService.DownloadAsync(
            [artifact], cache, CancellationToken.None, warning => { warnings.Add(warning); return true; });
        await File.WriteAllTextAsync(source, "changed");
        await DownloadService.DownloadAsync(
            [artifact], cache, CancellationToken.None, warning => { warnings.Add(warning); return true; });

        Assert.Equal(2, warnings.Count);
        Assert.False(warnings[0].BytesChanged);
        Assert.True(warnings[1].BytesChanged);
        Assert.NotEqual(warnings[0].SHA256, warnings[1].SHA256);
    }

    [Fact]
    public async Task StreamingDownload_HashesContentAndChecksLength()
    {
        byte[] payload = Encoding.UTF8.GetBytes("verified payload");
        await using LocalHttpServer server = await LocalHttpServer.StartAsync(payload, payload.Length);
        using TemporaryDirectory temporary = new();
        using HttpClient client = new();
        string target = Path.Combine(temporary.Path, "asset.zip");
        ArtifactReference reference = new("v1", "asset.zip", server.Url, null, "local test", payload.Length);

        string hash = await DownloadService.DownloadToPartialAsync(client, reference, target, CancellationToken.None);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), hash);
        Assert.False(File.Exists(target));
        Assert.Equal(payload, await File.ReadAllBytesAsync(target + ".partial"));
    }

    [Fact]
    public async Task FailedDownload_DoesNotOverwriteValidCache()
    {
        byte[] payload = Encoding.UTF8.GetBytes("short");
        await using LocalHttpServer server = await LocalHttpServer.StartAsync(payload, payload.Length + 50);
        using TemporaryDirectory temporary = new();
        using HttpClient client = new();
        string target = Path.Combine(temporary.Path, "asset.zip");
        await File.WriteAllTextAsync(target, "known-good");
        ArtifactReference reference = new("v1", "asset.zip", server.Url, null, "local test", payload.Length + 50);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            DownloadService.DownloadToPartialAsync(client, reference, target, CancellationToken.None));

        Assert.Equal("known-good", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task UnpinnedArtifact_DoesNotTreatTransportDigestAsTrust()
    {
        GitHubReleaseSource source = new("owner", "repo", "artifact.zip", ReleaseSelectionPolicy.ExactTag, "v1");
        ArtifactDefinition artifact = new("artifact", "Artifact", "Test", string.Empty, source);
        using HttpClient client = GitHubStub(ReleaseJson(TransportDigest));
        GitHubReleaseClient github = new(client);

        ArtifactReference reference = await github.ResolveAsync(artifact, source, CancellationToken.None);

        // The API digest is transport integrity, never a trust anchor.
        Assert.Null(reference.ExpectedSHA256);
        Assert.Equal(TransportDigest, reference.TransportSHA256);
    }

    [Fact]
    public async Task PinnedArtifact_KeepsCatalogPinAndRecordsTransportDigestSeparately()
    {
        string pin = new('A', 64);
        GitHubReleaseSource source = new(
            "owner", "repo", "artifact.zip", ReleaseSelectionPolicy.ExactTag, "v1", pin, 123);
        ArtifactDefinition artifact = new("artifact", "Artifact", "Test", string.Empty, source);
        using HttpClient client = GitHubStub(ReleaseJson(TransportDigest));
        GitHubReleaseClient github = new(client);

        ArtifactReference reference = await github.ResolveAsync(artifact, source, CancellationToken.None);

        Assert.Equal(pin, reference.ExpectedSHA256);
        Assert.Equal(TransportDigest, reference.TransportSHA256);
        Assert.Equal(123, reference.ExpectedSize);
    }

    [Theory]
    [InlineData("../../escape.zip")]
    [InlineData("/etc/passwd")]
    public async Task ManifestArchivePathOutsideArtifactRoot_IsRejected(string archivePath)
    {
        using TemporaryDirectory temporary = new();
        ArtifactDefinition artifact = new("artifact", "Artifact", "Test", string.Empty, null);
        string root = Path.Combine(temporary.Path, artifact.ID);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "download-manifest.json"),
            $$"""
            {"versionTag":"v1","assetName":"a.zip","archivePath":"{{archivePath}}","approvedSHA256":"{{new string('A', 64)}}"}
            """);

        string? recovered = await DownloadService.GetCachedArchivePathAsync(artifact, temporary.Path, CancellationToken.None);

        Assert.Null(recovered);
    }

    [Fact]
    public async Task PinnedLocalArchiveWithWrongBytes_IsRejected()
    {
        using TemporaryDirectory temporary = new();
        string source = Path.Combine(temporary.Path, "local.zip");
        await File.WriteAllTextAsync(source, "wrong bytes");
        ArtifactDefinition artifact = new(
            "artifact",
            "Artifact",
            "Test",
            string.Empty,
            new DirectSource("https://example.test/a.zip", new string('A', 64)),
            LocalArchivePath: source);

        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadService.DownloadAsync(
            [artifact],
            Path.Combine(temporary.Path, "cache"),
            CancellationToken.None,
            approveUntrusted: _ => true));
    }

    [Fact]
    public async Task PinnedSizeMismatch_IsRejectedAndPartialIsDeleted()
    {
        byte[] payload = Encoding.UTF8.GetBytes("payload");
        await using LocalHttpServer server = await LocalHttpServer.StartAsync(payload, payload.Length);
        using TemporaryDirectory temporary = new();
        using HttpClient client = new();
        string target = Path.Combine(temporary.Path, "asset.zip");
        ArtifactReference reference = new(
            "v1", "asset.zip", server.Url, null, "local test", payload.Length, null, payload.Length + 1);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DownloadService.DownloadToPartialAsync(client, reference, target, CancellationToken.None));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public void EveryBuiltInArtifactHasAnExactPinAndSize()
    {
        foreach (ArtifactDefinition artifact in ArtifactCatalog.GetAllBuiltInArtifacts())
        {
            Assert.NotNull(artifact.Source);
            Assert.True(
                artifact.Source!.TrustedSHA256 is { Length: 64 },
                $"{artifact.DisplayName} has no pinned SHA-256.");
            Assert.True(
                artifact.Source.ExpectedSize is > 0,
                $"{artifact.DisplayName} has no pinned size.");

            if (artifact.Source is GitHubReleaseSource github)
            {
                Assert.Equal(ReleaseSelectionPolicy.ExactTag, github.ReleasePolicy);
                Assert.False(string.IsNullOrWhiteSpace(github.ReleaseTag));
            }
        }
    }

    private const string TransportDigest =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private static string ReleaseJson(string digest) => $$"""
        {
          "tag_name": "v1",
          "draft": false,
          "prerelease": false,
          "created_at": "2026-01-01T00:00:00Z",
          "published_at": "2026-01-01T00:00:00Z",
          "assets": [
            {
              "name": "artifact.zip",
              "browser_download_url": "https://example.test/artifact.zip",
              "size": 123,
              "digest": "sha256:{{digest.ToLowerInvariant()}}"
            }
          ]
        }
        """;

    private static HttpClient GitHubStub(string json) =>
        new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private static GitHubReleaseInfo Release(string tag, DateTimeOffset date, bool prerelease = false, bool draft = false) =>
        new(tag, draft, prerelease, date, date, []);
}

internal sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Task _serverTask;

    private LocalHttpServer(TcpListener listener, Task serverTask, string url)
    {
        _listener = listener;
        _serverTask = serverTask;
        Url = url;
    }

    internal string Url { get; }

    internal static Task<LocalHttpServer> StartAsync(byte[] payload, long advertisedLength)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task task = ServeOnceAsync(listener, payload, advertisedLength);
        return Task.FromResult(new LocalHttpServer(listener, task, $"http://127.0.0.1:{port}/asset"));
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try
        {
            await _serverTask;
        }
        catch (SocketException)
        {
        }
    }

    private static async Task ServeOnceAsync(TcpListener listener, byte[] payload, long advertisedLength)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync();
        await using NetworkStream stream = connection.GetStream();
        byte[] requestBuffer = new byte[4096];
        int used = 0;
        while (used < requestBuffer.Length)
        {
            int read = await stream.ReadAsync(requestBuffer.AsMemory(used));
            if (read == 0)
                break;
            used += read;
            if (Encoding.ASCII.GetString(requestBuffer, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {advertisedLength}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }
}
