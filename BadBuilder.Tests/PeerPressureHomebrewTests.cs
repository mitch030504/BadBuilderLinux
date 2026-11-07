using BadBuilder.Configuration;
using BadBuilder.Services;

namespace BadBuilder.Tests;

public sealed class PeerPressureHomebrewTests
{
    [Fact]
    public async Task Extras_InstallAppsAndContentWithOneDefaultDashboardAndPreserveXell()
    {
        using TemporaryDirectory temporary = new();
        BuilderConfig config = new(ArtifactCatalog.Homebrew) { SelectedExploit = ExploitOption.PeerPressure };
        config.LaunchHomebrew = config.Homebrew[0];
        var staged = CreateStagedArtifacts(temporary.Path, config);

        var prepared = PeerPressureHomebrewService.PrepareArtifacts(config, staged);
        InstallPlan plan = InstallService.BuildPlan(prepared, [], 128L * 1024 * 1024);
        string target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(target);
        await InstallService.ExecuteAsync(plan, target, CancellationToken.None);

        string extras = Path.Combine(target, "BadUpdatePayload", "SoftmodExtras");
        Assert.Equal("aurora", await File.ReadAllTextAsync(Path.Combine(extras, "homebrew-aurora", "Aurora.xex")));
        Assert.True(File.Exists(Path.Combine(extras, "homebrew-xexmenu", "Content", "menu")));
        Assert.True(File.Exists(Path.Combine(extras, "homebrew-simple360nandflasher", "Default.xex")));
        Assert.True(File.Exists(Path.Combine(extras, "Xell", "xell-2f.bin")));
        Assert.Equal("installer", await File.ReadAllTextAsync(Path.Combine(target, "BadUpdatePayload", "default.xex")));
        string ini = await File.ReadAllTextAsync(Path.Combine(extras, "SoftmodExtras.ini"));
        Assert.StartsWith("; upstream comments\r\n[Xell]\r\nSource = \"Xell\"\r\n", ini, StringComparison.Ordinal);
        Assert.Contains("Source = \"homebrew-aurora\"\r\nDestination = \"Hdd1:\\Apps\\Aurora\"", ini, StringComparison.Ordinal);
        Assert.Contains("Source = \"homebrew-xexmenu\"\r\nDestination = \"Hdd1:\\\"", ini, StringComparison.Ordinal);
        Assert.Single(ini.Split('\n'), line => line.StartsWith("DashXex", StringComparison.Ordinal));
        Assert.Contains("DashXex = \"Aurora.xex\"", ini, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(target, "Apps")));
        Assert.False(File.Exists(Path.Combine(target, "launch.ini")));
        Assert.All(config.Homebrew, entry => Assert.DoesNotContain("SoftmodExtras", entry.Artifact.Operations![0].DestinationPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Extras_WithoutADefaultDoNotSetDashXex()
    {
        using TemporaryDirectory temporary = new();
        BuilderConfig config = new(ArtifactCatalog.Homebrew) { SelectedExploit = ExploitOption.PeerPressure };
        var prepared = PeerPressureHomebrewService.PrepareArtifacts(config, CreateStagedArtifacts(temporary.Path, config));
        InstallPlan plan = InstallService.BuildPlan(prepared, [], 128L * 1024 * 1024);
        var ini = Assert.Single(plan.Actions, action => action.Kind == PlannedActionKind.WriteFile);
        Assert.DoesNotContain("DashXex", System.Text.Encoding.UTF8.GetString(ini.Contents!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing.xex", typeof(FileNotFoundException))]
    [InlineData("../outside.xex", typeof(InvalidDataException))]
    public void Extras_RejectInvalidDefaultEntryPointBeforeFormatting(string entryPoint, Type expectedError)
    {
        using TemporaryDirectory temporary = new();
        HomebrewEntry aurora = ArtifactCatalog.Homebrew[0] with { EntryPointRelativePath = entryPoint };
        BuilderConfig config = new([aurora]) { SelectedExploit = ExploitOption.PeerPressure, LaunchHomebrew = aurora };
        var staged = CreateStagedArtifacts(temporary.Path, config);
        Assert.Throws(expectedError, () => PeerPressureHomebrewService.PrepareArtifacts(config, staged));
    }

    [Fact]
    public void Extras_RejectIniInjectionInAnAppName()
    {
        using TemporaryDirectory temporary = new();
        HomebrewEntry aurora = ArtifactCatalog.Homebrew[0] with
        {
            Artifact = ArtifactCatalog.Homebrew[0].Artifact with { DisplayName = "Aurora\"\r\nDestination = HddSys:" }
        };
        BuilderConfig config = new([aurora]) { SelectedExploit = ExploitOption.PeerPressure };
        Assert.Throws<InvalidDataException>(() => PeerPressureHomebrewService.PrepareArtifacts(config,
            CreateStagedArtifacts(temporary.Path, config)));
    }

    [Fact]
    public async Task Extras_SupportNestedCustomEntryPointsAndArchiveSubdirectories()
    {
        using TemporaryDirectory temporary = new();
        HomebrewEntry app = ArtifactCatalog.Homebrew[0] with
        {
            EntryPointRelativePath = "bin/start.xex",
            Artifact = ArtifactCatalog.Homebrew[0].Artifact with
            {
                DisplayName = "My App",
                Operations = [new InstallOperation(InstallOperationKind.CopyDirectory, "Apps/My App", "package")]
            }
        };
        BuilderConfig config = new([app]) { SelectedExploit = ExploitOption.PeerPressure, LaunchHomebrew = app };
        var staged = CreateStagedArtifacts(temporary.Path, config);
        WriteFile(Path.Combine(temporary.Path, app.Artifact.ID), "package/bin/start.xex", "custom");

        var prepared = PeerPressureHomebrewService.PrepareArtifacts(config, staged);
        InstallPlan plan = InstallService.BuildPlan(prepared, [], 128L * 1024 * 1024);
        string target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(target);
        await InstallService.ExecuteAsync(plan, target, CancellationToken.None);

        string extras = Path.Combine(target, "BadUpdatePayload", "SoftmodExtras");
        Assert.Equal("custom", await File.ReadAllTextAsync(Path.Combine(extras, app.Artifact.ID, "bin", "start.xex")));
        string ini = await File.ReadAllTextAsync(Path.Combine(extras, "SoftmodExtras.ini"));
        Assert.Contains("Destination = \"Hdd1:\\Apps\\My App\"", ini, StringComparison.Ordinal);
        Assert.Contains("DashXex = \"bin\\start.xex\"", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void Extras_WithNoAppsPreserveOriginalInstallerOperations()
    {
        using TemporaryDirectory temporary = new();
        BuilderConfig config = new([]) { SelectedExploit = ExploitOption.PeerPressure };
        var staged = CreateStagedArtifacts(temporary.Path, config);
        var prepared = PeerPressureHomebrewService.PrepareArtifacts(config, staged);
        Assert.Equal(staged, prepared);
    }

    [Fact]
    public void Extras_RejectAStaleDefaultSelection()
    {
        BuilderConfig config = new([])
        {
            SelectedExploit = ExploitOption.PeerPressure,
            LaunchHomebrew = ArtifactCatalog.Homebrew[0]
        };
        Assert.Throws<InvalidOperationException>(() => PeerPressureHomebrewService.PrepareArtifacts(config, []));
    }

    private static List<(ArtifactDefinition Artifact, string StagingPath)> CreateStagedArtifacts(string root, BuilderConfig config)
    {
        ArtifactDefinition installer = ArtifactCatalog.Exploits[ExploitOption.PeerPressure];
        string installerRoot = Path.Combine(root, installer.ID);
        foreach (string path in installer.Layout!.RequiredPaths!)
            WriteFile(installerRoot, path, "installer");
        WriteFile(installerRoot, "BadUpdatePayload/SoftmodExtras/SoftmodExtras.ini", "; upstream comments\r\n[Xell]\r\nSource = \"Xell\"\r\n");
        List<(ArtifactDefinition, string)> staged = [(installer, installerRoot)];
        foreach (HomebrewEntry entry in config.Homebrew)
        {
            string appRoot = Path.Combine(root, entry.Artifact.ID);
            string file = entry.Artifact.ID switch
            {
                "homebrew-aurora" => "Aurora.xex",
                "homebrew-xexmenu" => "Content/menu",
                _ => "Simple 360 NAND Flasher/Default.xex"
            };
            WriteFile(appRoot, file, "aurora");
            staged.Add((entry.Artifact, appRoot));
        }
        return staged;
    }

    private static void WriteFile(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
