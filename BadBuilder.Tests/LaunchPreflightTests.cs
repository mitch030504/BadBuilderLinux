using System.Text;
using BadBuilder.Configuration;
using BadBuilder.Services;

namespace BadBuilder.Tests;

public sealed class LaunchPreflightTests
{
    private const string DefaultPath = @"Usb:\Apps\Aurora\Aurora.xex";

    [Fact]
    public async Task ConfigureStagedLaunch_UpdatesRootCopyDirectoryIniBeforePlanExecution()
    {
        using TemporaryDirectory temporary = new();
        string bootstrapStaging = CreateDirectory(temporary.Path, "bootstrap");
        string homebrewStaging = CreateDirectory(temporary.Path, "homebrew");
        await File.WriteAllTextAsync(
            Path.Combine(bootstrapStaging, "launch.ini"),
            "[Paths]\r\nDefault = Usb:\\old.xex\r\n");
        await File.WriteAllTextAsync(Path.Combine(homebrewStaging, "Aurora.xex"), "xex");

        ArtifactDefinition bootstrap = CreateBootstrap(
            "bootstrap-xeunshackle",
            new InstallOperation(InstallOperationKind.CopyDirectory, ".", "."));
        HomebrewEntry launchHomebrew = CreateHomebrew(
            "homebrew-aurora",
            "Aurora",
            new InstallOperation(InstallOperationKind.CopyDirectory, "Apps/Aurora", "."),
            "Aurora.xex");
        List<(ArtifactDefinition Artifact, string StagingPath)> staged =
        [
            (bootstrap, bootstrapStaging),
            (launchHomebrew.Artifact, homebrewStaging),
        ];

        bool updated = await LaunchIniService.ConfigureStagedLaunchAsync(
            staged, bootstrap.ID, launchHomebrew, DefaultPath, CancellationToken.None);

        Assert.True(updated);
        Assert.Contains(
            $"Default = {DefaultPath}",
            await File.ReadAllTextAsync(Path.Combine(bootstrapStaging, "launch.ini")),
            StringComparison.Ordinal);

        InstallPlan plan = InstallService.BuildPlan(staged, [], 128L * 1024 * 1024);
        string target = CreateDirectory(temporary.Path, "target");
        await InstallService.ExecuteAsync(plan, target, _ => true, CancellationToken.None);

        Assert.Contains(
            $"Default = {DefaultPath}",
            await File.ReadAllTextAsync(Path.Combine(target, "launch.ini")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureStagedLaunch_UpdatesExplicitCopyFileIni()
    {
        using TemporaryDirectory temporary = new();
        string bootstrapStaging = CreateDirectory(temporary.Path, "bootstrap");
        string maxFolder = CreateDirectory(bootstrapStaging, "XeUnshackle-Max");
        await File.WriteAllTextAsync(Path.Combine(maxFolder, "launch.ini"), "[Paths]\r\n; comment\r\n");
        string homebrewStaging = CreateDirectory(temporary.Path, "homebrew");
        await File.WriteAllTextAsync(Path.Combine(homebrewStaging, "Default.xex"), "xex");

        ArtifactDefinition bootstrap = CreateBootstrap(
            "bootstrap-xeunshackle-max",
            new InstallOperation(
                InstallOperationKind.CopyFile,
                "launch.ini",
                "XeUnshackle-Max/launch.ini"));
        HomebrewEntry launchHomebrew = CreateHomebrew(
            "homebrew-aurora",
            "Aurora",
            new InstallOperation(InstallOperationKind.CopyDirectory, "Apps/Aurora", "."),
            "Default.xex");
        List<(ArtifactDefinition Artifact, string StagingPath)> staged =
        [
            (bootstrap, bootstrapStaging),
            (launchHomebrew.Artifact, homebrewStaging),
        ];

        bool updated = await LaunchIniService.ConfigureStagedLaunchAsync(
            staged, bootstrap.ID, launchHomebrew, DefaultPath, CancellationToken.None);

        Assert.True(updated);
        Assert.Contains(
            $"Default = {DefaultPath}",
            await File.ReadAllTextAsync(Path.Combine(maxFolder, "launch.ini")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureStagedLaunch_MalformedUtf16IniThrowsBeforeWriting()
    {
        using TemporaryDirectory temporary = new();
        string bootstrapStaging = CreateDirectory(temporary.Path, "bootstrap");
        string homebrewStaging = CreateDirectory(temporary.Path, "homebrew");
        byte[] malformed =
        [
            .. Encoding.Unicode.GetPreamble(),
            .. Encoding.Unicode.GetBytes("[Paths]\r\n"),
            0x00, 0xD8,
        ];
        string iniPath = Path.Combine(bootstrapStaging, "launch.ini");
        await File.WriteAllBytesAsync(iniPath, malformed);
        await File.WriteAllTextAsync(Path.Combine(homebrewStaging, "Aurora.xex"), "xex");

        ArtifactDefinition bootstrap = CreateBootstrap(
            "bootstrap-xeunshackle",
            new InstallOperation(InstallOperationKind.CopyDirectory, ".", "."));
        HomebrewEntry launchHomebrew = CreateHomebrew(
            "homebrew-aurora",
            "Aurora",
            new InstallOperation(InstallOperationKind.CopyDirectory, "Apps/Aurora", "."),
            "Aurora.xex");
        List<(ArtifactDefinition Artifact, string StagingPath)> staged =
        [
            (bootstrap, bootstrapStaging),
            (launchHomebrew.Artifact, homebrewStaging),
        ];

        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            LaunchIniService.ConfigureStagedLaunchAsync(
                staged, bootstrap.ID, launchHomebrew, DefaultPath, CancellationToken.None));

        Assert.Equal(malformed, await File.ReadAllBytesAsync(iniPath));
    }

    [Fact]
    public async Task ConfigureStagedLaunch_MissingSelectedEntryPointThrowsFileNotFoundException()
    {
        using TemporaryDirectory temporary = new();
        string bootstrapStaging = CreateDirectory(temporary.Path, "bootstrap");
        string homebrewStaging = CreateDirectory(temporary.Path, "homebrew");
        await File.WriteAllTextAsync(Path.Combine(homebrewStaging, "Other.xex"), "xex");

        ArtifactDefinition bootstrap = CreateBootstrap(
            "bootstrap-xeunshackle",
            new InstallOperation(InstallOperationKind.CopyDirectory, ".", "."));
        HomebrewEntry launchHomebrew = CreateHomebrew(
            "homebrew-aurora",
            "Aurora",
            new InstallOperation(InstallOperationKind.CopyDirectory, "Apps/Aurora", "."),
            "Missing.xex");
        List<(ArtifactDefinition Artifact, string StagingPath)> staged =
        [
            (bootstrap, bootstrapStaging),
            (launchHomebrew.Artifact, homebrewStaging),
        ];

        // The entry point is validated before the missing launch.ini is considered.
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            LaunchIniService.ConfigureStagedLaunchAsync(
                staged, bootstrap.ID, launchHomebrew, DefaultPath, CancellationToken.None));
    }

    [Fact]
    public async Task ConfigureStagedLaunch_MissingLaunchIniReturnsFalse()
    {
        using TemporaryDirectory temporary = new();
        string bootstrapStaging = CreateDirectory(temporary.Path, "bootstrap");
        string homebrewStaging = CreateDirectory(temporary.Path, "homebrew");
        await File.WriteAllTextAsync(Path.Combine(bootstrapStaging, "default.xex"), "payload");
        await File.WriteAllTextAsync(Path.Combine(homebrewStaging, "Aurora.xex"), "xex");

        ArtifactDefinition bootstrap = CreateBootstrap(
            "bootstrap-xeunshackle",
            new InstallOperation(InstallOperationKind.CopyDirectory, ".", "."));
        HomebrewEntry launchHomebrew = CreateHomebrew(
            "homebrew-aurora",
            "Aurora",
            new InstallOperation(InstallOperationKind.CopyDirectory, "Apps/Aurora", "."),
            "Aurora.xex");
        List<(ArtifactDefinition Artifact, string StagingPath)> staged =
        [
            (bootstrap, bootstrapStaging),
            (launchHomebrew.Artifact, homebrewStaging),
        ];

        bool updated = await LaunchIniService.ConfigureStagedLaunchAsync(
            staged, bootstrap.ID, launchHomebrew, DefaultPath, CancellationToken.None);

        Assert.False(updated);
    }

    [Fact]
    public void FindStagedLaunchIni_MultipleDistinctCandidatesThrowInvalidDataException()
    {
        using TemporaryDirectory temporary = new();
        string bootstrapStaging = CreateDirectory(temporary.Path, "bootstrap");
        string first = CreateDirectory(bootstrapStaging, "first");
        string second = CreateDirectory(bootstrapStaging, "second");
        File.WriteAllText(Path.Combine(first, "launch.ini"), "[Paths]\r\n");
        File.WriteAllText(Path.Combine(second, "launch.ini"), "[Paths]\r\n");

        ArtifactDefinition bootstrap = CreateBootstrap(
            "bootstrap-xeunshackle-max",
            new InstallOperation(InstallOperationKind.CopyFile, "launch.ini", "first/launch.ini"),
            new InstallOperation(InstallOperationKind.CopyFile, "launch.ini", "second/launch.ini"));
        List<(ArtifactDefinition Artifact, string StagingPath)> staged = [(bootstrap, bootstrapStaging)];

        Assert.Throws<InvalidDataException>(() =>
            LaunchIniService.FindStagedLaunchIni(staged, bootstrap.ID));
    }

    private static string CreateDirectory(string root, string relativePath)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ArtifactDefinition CreateBootstrap(string id, params InstallOperation[] operations) =>
        new(
            id,
            id,
            "Test bootstrap",
            "bootstrap",
            null,
            operations,
            ArtifactPriority.Bootstrap);

    private static HomebrewEntry CreateHomebrew(
        string id,
        string displayName,
        InstallOperation operation,
        string? entryPoint) =>
        new(
            new ArtifactDefinition(
                id,
                displayName,
                "Test homebrew",
                "homebrew/" + id,
                null,
                [operation],
                ArtifactPriority.Homebrew),
            EntryPointRelativePath: entryPoint);
}
