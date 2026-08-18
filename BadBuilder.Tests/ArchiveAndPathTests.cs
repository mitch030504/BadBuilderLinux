using System.IO.Compression;
using BadBuilder.Configuration;
using BadBuilder.Services;

namespace BadBuilder.Tests;

public sealed class ArchiveAndPathTests
{
    [Fact]
    public void Containment_UsesRequestedOperatingSystemCaseRules()
    {
        Assert.True(PathSafety.IsWithinRoot("/tmp/Root", "/tmp/Root/file", caseSensitive: true));
        Assert.False(PathSafety.IsWithinRoot("/tmp/Root", "/tmp/root/file", caseSensitive: true));
        Assert.True(PathSafety.IsWithinRoot("/tmp/Root", "/tmp/root/file", caseSensitive: false));
        Assert.False(PathSafety.IsWithinRoot("/tmp/Root", "/tmp/RootSibling/file", caseSensitive: false));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/rooted")]
    [InlineData("C:/windows")]
    public void NormalizeRelativePath_RejectsEscapesAndRoots(string path)
    {
        Assert.Throws<InvalidDataException>(() => PathSafety.NormalizeRelativePath(path));
    }

    [Theory]
    [InlineData("CON/file.txt")]
    [InlineData("folder/bad:name.txt")]
    [InlineData("folder/trailing.")]
    public void FatValidation_RejectsUnsupportedNames(string path)
    {
        Assert.Throws<InvalidDataException>(() => FileServices.ValidateFatRelativePath(path));
    }

    [Fact]
    public async Task Extraction_RejectsZipSlipAndCleansPartialDirectory()
    {
        using TemporaryDirectory temporary = new();
        string archive = CreateZip(temporary.Path, ("../outside.txt", "no"));
        string staging = System.IO.Path.Combine(temporary.Path, "staging");
        Directory.CreateDirectory(staging);

        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveService.ExtractAsync(
            "artifact", archive, staging, Limits(), CancellationToken.None));

        Assert.False(Directory.Exists(System.IO.Path.Combine(staging, "artifact")));
        Assert.False(File.Exists(System.IO.Path.Combine(temporary.Path, "outside.txt")));
    }

    [Fact]
    public async Task Extraction_RejectsCaseCollisions()
    {
        using TemporaryDirectory temporary = new();
        string archive = CreateZip(temporary.Path, ("Apps/Test.xex", "a"), ("apps/test.xex", "b"));
        string staging = System.IO.Path.Combine(temporary.Path, "staging");
        Directory.CreateDirectory(staging);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveService.ExtractAsync(
            "artifact", archive, staging, Limits(), CancellationToken.None));

        Assert.Contains("case-colliding", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Extraction_RejectsChildBeforeParentFileCollision()
    {
        using TemporaryDirectory temporary = new();
        string archive = CreateZip(temporary.Path, ("item/child.bin", "a"), ("item", "b"));
        string staging = System.IO.Path.Combine(temporary.Path, "staging");
        Directory.CreateDirectory(staging);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveService.ExtractAsync(
            "artifact", archive, staging, Limits(), CancellationToken.None));

        Assert.Contains("parent directory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(System.IO.Path.Combine(staging, "artifact")));
    }

    [Fact]
    public async Task Extraction_EnforcesExpandedByteLimit()
    {
        using TemporaryDirectory temporary = new();
        string archive = CreateZip(temporary.Path, ("large.bin", new string('x', 1024)));
        string staging = System.IO.Path.Combine(temporary.Path, "staging");
        Directory.CreateDirectory(staging);
        ArchiveExtractionLimits limits = new(100, 100, 100, 100);

        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveService.ExtractAsync(
            "artifact", archive, staging, limits, CancellationToken.None));
    }

    [Fact]
    public async Task Extraction_VerifiesArchiveCrcAndCleansFailure()
    {
        using TemporaryDirectory temporary = new();
        string archivePath = System.IO.Path.Combine(temporary.Path, "corrupt.zip");
        const string payloadText = "UNIQUE-BADBUILDER-CRC-PAYLOAD";
        using (FileStream stream = File.Create(archivePath))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("payload.bin", CompressionLevel.NoCompression);
            using StreamWriter writer = new(entry.Open());
            writer.Write(payloadText);
        }

        byte[] bytes = await File.ReadAllBytesAsync(archivePath);
        byte[] needle = System.Text.Encoding.UTF8.GetBytes(payloadText);
        int payloadOffset = bytes.AsSpan().IndexOf(needle);
        Assert.True(payloadOffset >= 0);
        bytes[payloadOffset] ^= 0x40;
        await File.WriteAllBytesAsync(archivePath, bytes);
        string staging = System.IO.Path.Combine(temporary.Path, "staging");
        Directory.CreateDirectory(staging);

        await Assert.ThrowsAnyAsync<Exception>(() => ArchiveService.ExtractAsync(
            "artifact", archivePath, staging, Limits(), CancellationToken.None));
        Assert.False(Directory.Exists(System.IO.Path.Combine(staging, "artifact")));
    }

    [Fact]
    public async Task Extraction_UsesArtifactIdAndValidatesLayout()
    {
        using TemporaryDirectory temporary = new();
        string archive = CreateZip(temporary.Path, ("payload/default.xex", "xex"));
        string staging = System.IO.Path.Combine(temporary.Path, "staging");
        Directory.CreateDirectory(staging);

        string extracted = await ArchiveService.ExtractAsync("artifact-one", archive, staging, Limits(), CancellationToken.None);
        ArtifactDefinition artifact = new(
            "artifact-one", "Artifact", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")],
            Layout: new ArchiveLayout(["payload/default.xex"]));

        ArchiveService.ValidateLayout(artifact, extracted);
        Assert.EndsWith("artifact-one", extracted, StringComparison.Ordinal);
    }

    private static ArchiveExtractionLimits Limits() => new(100, 1024 * 1024, 1024 * 1024, 1024 * 1024);

    private static string CreateZip(string root, params (string Path, string Contents)[] files)
    {
        string path = System.IO.Path.Combine(root, $"fixture-{Guid.NewGuid():N}.zip");
        using FileStream stream = File.Create(path);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        foreach ((string entryPath, string contents) in files)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryPath);
            using StreamWriter writer = new(entry.Open());
            writer.Write(contents);
        }
        return path;
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"BadBuilderTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
