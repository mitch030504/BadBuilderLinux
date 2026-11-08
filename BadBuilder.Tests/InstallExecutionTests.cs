using BadBuilder.Services;

namespace BadBuilder.Tests;

public sealed class InstallExecutionTests
{
    [Fact]
    public async Task Execute_RejectsTargetThatIsNotAMountedFilesystem()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(target);
        InstallPlan plan = new([], 0);

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            InstallService.ExecuteAsync(plan, target, _ => false, CancellationToken.None));

        Assert.Contains("no longer a mounted filesystem", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_RejectsSourceWhoseSizeChangedAfterPreflight()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(target);
        string source = Path.Combine(temporary.Path, "source.bin");
        await File.WriteAllTextAsync(source, "payload");

        InstallPlan plan = new(
            [new PlannedInstallAction(PlannedActionKind.CopyFile, "dest.bin", source, Size: 1)],
            1);

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            InstallService.ExecuteAsync(plan, target, _ => true, CancellationToken.None));

        Assert.Contains("changed after preflight", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_WritesExactBytesAndReportsSuccess()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(target);
        string source = Path.Combine(temporary.Path, "source.bin");
        byte[] payload = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(source, payload);

        InstallPlan plan = new(
            [
                new PlannedInstallAction(PlannedActionKind.CreateDirectory, "Apps"),
                new PlannedInstallAction(PlannedActionKind.CopyFile, "Apps/source.bin", source, Size: payload.Length),
                new PlannedInstallAction(PlannedActionKind.WriteFile, "info.txt", Contents: "hello"u8.ToArray(), Size: 5),
            ],
            payload.Length + 5);

        await InstallService.ExecuteAsync(plan, target, _ => true, CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(target, "Apps", "source.bin")));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(target, "info.txt")));
    }

    [Fact]
    public void IsMountPoint_RecognizesRootAndRejectsOrdinaryDirectories()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using TemporaryDirectory temporary = new();

        Assert.True(FileSystemSpace.IsMountPoint("/"));
        Assert.False(FileSystemSpace.IsMountPoint(temporary.Path));
    }

    [Fact]
    public void GetAvailableBytes_ReturnsPositiveForAnExistingDirectory()
    {
        using TemporaryDirectory temporary = new();

        Assert.True(FileSystemSpace.GetAvailableBytes(temporary.Path) > 0);
    }
}
