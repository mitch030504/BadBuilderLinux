using System.Net;
using System.Security.Cryptography;
using System.Text;
using BadBuilder.Configuration;
using BadBuilder.Services;
using BadBuilder.UI;

namespace BadBuilder.Tests;

public sealed class DownloadHardeningTests
{
    [Fact]
    public async Task DirectSourceDownloadFailure_PromptsForLocalArchiveAndUsesIt()
    {
        using TemporaryDirectory temporary = new();
        string downloadRoot = Path.Combine(temporary.Path, "downloads");
        string localArchive = Path.Combine(temporary.Path, "local.zip");
        byte[] localBytes = Encoding.UTF8.GetBytes("local archive");
        await File.WriteAllBytesAsync(localArchive, localBytes);
        string pinnedHash = Convert.ToHexString(SHA256.HashData(localBytes));

        ArtifactDefinition artifact = new(
            "dashboard",
            "Dashboard",
            "Test",
            string.Empty,
            new DirectSource("https://example.invalid/dashboard.zip", pinnedHash));

        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using HttpClient client = new(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        List<ArchivePathEntry> prompts = [];

        DownloadResult result = await DownloadService.DownloadAsync(
            [artifact],
            downloadRoot,
            CancellationToken.None,
            approveUntrusted: _ => true,
            promptLocalArchives: entries =>
            {
                prompts.AddRange(entries);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [artifact.ID] = localArchive,
                };
            },
            httpClient: client);

        Assert.Single(prompts);
        Assert.Equal(0, result.DownloadedCount);
        (ArtifactDefinition returned, string archivePath) = Assert.Single(result.Artifacts);
        Assert.Same(artifact, returned);
        // The supplied archive is imported into the cache so the approved bytes
        // cannot be swapped between approval and extraction.
        Assert.True(PathSafety.IsWithinRoot(downloadRoot, archivePath));
        Assert.NotEqual(Path.GetFullPath(localArchive), archivePath);
        Assert.Equal(localBytes, await File.ReadAllBytesAsync(archivePath));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task InconsistentManifest_IsIgnoredAndCachedArchiveRequiresApproval()
    {
        using TemporaryDirectory temporary = new();
        string downloadRoot = Path.Combine(temporary.Path, "downloads");
        string artifactRoot = Path.Combine(downloadRoot, "offline");
        Directory.CreateDirectory(artifactRoot);

        string cachedArchive = Path.Combine(artifactRoot, "cached.zip");
        byte[] cachedBytes = Encoding.UTF8.GetBytes("stale cache");
        await File.WriteAllBytesAsync(cachedArchive, cachedBytes);

        string manifest = $$"""
            {
              "VersionTag": "local",
              "AssetName": "cached.zip",
              "ArchivePath": "cached.zip",
              "TrustedSHA256": "{{new string('0', 64)}}",
              "ApprovedSHA256": "{{Convert.ToHexString(SHA256.HashData(cachedBytes))}}",
              "UpdatedAtUtc": "2026-01-01T00:00:00+00:00"
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(artifactRoot, "download-manifest.json"), manifest);

        ArtifactDefinition artifact = new("offline", "Offline", "Test", string.Empty, null);
        List<ArchivePathEntry> prompts = [];
        List<UntrustedArtifact> approvals = [];

        DownloadResult result = await DownloadService.DownloadAsync(
            [artifact],
            downloadRoot,
            CancellationToken.None,
            approveUntrusted: warning =>
            {
                approvals.Add(warning);
                return true;
            },
            promptLocalArchives: entries =>
            {
                prompts.AddRange(entries);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            });

        Assert.Empty(prompts);
        UntrustedArtifact approval = Assert.Single(approvals);
        Assert.False(approval.BytesChanged);
        (_, string archivePath) = Assert.Single(result.Artifacts);
        Assert.Equal(Path.GetFullPath(cachedArchive), archivePath);
        Assert.Equal(cachedBytes, await File.ReadAllBytesAsync(archivePath));
    }

    [Fact]
    public async Task StreamingDownloadBodyStall_TimesOutAndDeletesPartialFile()
    {
        using TemporaryDirectory temporary = new();
        using StallingStream stalling = new();
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stalling),
        });
        using HttpClient client = new(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        string target = Path.Combine(temporary.Path, "asset.zip");
        ArtifactReference reference = new(
            "v1",
            "asset.zip",
            "https://example.invalid/asset.zip",
            null,
            "test",
            null);

        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() =>
            DownloadService.DownloadToPartialAsync(
                client,
                reference,
                target,
                CancellationToken.None,
                TimeSpan.FromMilliseconds(100)));

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task GitHubMetadataBodyStall_TimesOut()
    {
        using StallingStream stalling = new();
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stalling),
        });
        using HttpClient httpClient = new(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        GitHubReleaseClient client = new(httpClient, TimeSpan.FromMilliseconds(100));
        GitHubReleaseSource source = new("owner", "repo", "*.zip");
        ArtifactDefinition artifact = new("artifact", "Artifact", "Test", string.Empty, source);

        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.ResolveAsync(artifact, source, CancellationToken.None));

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellation_RemainsOperationCanceled()
    {
        using TemporaryDirectory temporary = new();
        using StallingStream stalling = new();
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stalling),
        });
        using HttpClient client = new(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        string target = Path.Combine(temporary.Path, "asset.zip");
        ArtifactReference reference = new(
            "v1",
            "asset.zip",
            "https://example.invalid/asset.zip",
            null,
            "test",
            null);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DownloadService.DownloadToPartialAsync(
                client,
                reference,
                target,
                cancellation.Token,
                TimeSpan.FromSeconds(10)));

        Assert.False(File.Exists(target + ".partial"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        private int _requestCount;

        internal StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_response(request));
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
