using BadBuilder.Configuration;
using BadBuilder.Services;

namespace BadBuilder.Tests;

public sealed class FreeMyXeTests
{
    private const string DefaultXexPath = "BadUpdatePayload/default.xex";
    private const string FreeMyXeSourcePath = "BadUpdatePayload/FreeMyXe.xex";

    [Fact]
    public void Catalog_FreeMyXeRenameTreatsDefaultXexAsOptionalOverwrite()
    {
        ArtifactDefinition artifact = ArtifactCatalog.Bootstraps[BootstrapOption.FreeMyXe];
        InstallOperation rename = Assert.Single(
            artifact.Operations!,
            operation => operation.Kind == InstallOperationKind.RenameFile);

        Assert.Equal(DefaultXexPath, rename.DestinationPath);
        Assert.Equal(FreeMyXeSourcePath, rename.SourcePath);
        Assert.Contains(DefaultXexPath, rename.OptionalOverwritePaths ?? []);
        Assert.Empty(rename.AllowedOverwritePaths ?? []);
    }

    [Fact]
    public async Task FreeMyXe_InstallsWhenExploitHasNoDefaultXex()
    {
        using TemporaryDirectory temporary = new();
        string exploitRoot = Path.Combine(temporary.Path, "exploit");
        string freeMyXeRoot = Path.Combine(temporary.Path, "freemyxe");
        string target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(Path.Combine(exploitRoot, "BadUpdatePayload"));
        Directory.CreateDirectory(freeMyXeRoot);
        Directory.CreateDirectory(target);

        await File.WriteAllTextAsync(Path.Combine(exploitRoot, "BadUpdatePayload", "stage2.bin"), "exploit");
        byte[] freeMyXeBytes = "freemyxe payload"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(freeMyXeRoot, "FreeMyXe.xex"), freeMyXeBytes);

        ArtifactDefinition exploit = new(
            "exploit", "Exploit", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")]);
        InstallPlan plan = InstallService.BuildPlan(
            [(exploit, exploitRoot), (ArtifactCatalog.Bootstraps[BootstrapOption.FreeMyXe], freeMyXeRoot)],
            [], 128L * 1024 * 1024);
        await InstallService.ExecuteAsync(plan, target, CancellationToken.None);

        Assert.Equal(
            freeMyXeBytes,
            await File.ReadAllBytesAsync(Path.Combine(target, "BadUpdatePayload", "default.xex")));
        Assert.False(File.Exists(Path.Combine(target, FreeMyXeSourcePath)));
    }

    [Fact]
    public async Task FreeMyXe_OverwritesExistingExploitDefaultXex()
    {
        using TemporaryDirectory temporary = new();
        string exploitRoot = Path.Combine(temporary.Path, "exploit");
        string freeMyXeRoot = Path.Combine(temporary.Path, "freemyxe");
        string target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(Path.Combine(exploitRoot, "BadUpdatePayload"));
        Directory.CreateDirectory(freeMyXeRoot);
        Directory.CreateDirectory(target);

        await File.WriteAllTextAsync(Path.Combine(exploitRoot, DefaultXexPath), "exploit payload");
        byte[] freeMyXeBytes = "freemyxe payload"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(freeMyXeRoot, "FreeMyXe.xex"), freeMyXeBytes);

        ArtifactDefinition exploit = new(
            "exploit", "Exploit", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")]);
        InstallPlan plan = InstallService.BuildPlan(
            [(exploit, exploitRoot), (ArtifactCatalog.Bootstraps[BootstrapOption.FreeMyXe], freeMyXeRoot)],
            [], 128L * 1024 * 1024);
        await InstallService.ExecuteAsync(plan, target, CancellationToken.None);

        Assert.Equal(
            freeMyXeBytes,
            await File.ReadAllBytesAsync(Path.Combine(target, DefaultXexPath)));
        Assert.False(File.Exists(Path.Combine(target, FreeMyXeSourcePath)));
    }
}
