using System.Security.Cryptography;
using BadBuilder.Configuration;
using BadBuilder.Services;

namespace BadBuilder.Tests;

public sealed class PeerPressureTests
{
    [Fact]
    public void Catalog_PinsOfficialInstallerReleaseAndChecksum()
    {
        GitHubReleaseSource source = Assert.IsType<GitHubReleaseSource>(
            ArtifactCatalog.Exploits[ExploitOption.PeerPressure].Source);

        Assert.Equal("grimdoomer", source.Owner);
        Assert.Equal("Xbox360PeerPressure", source.Repo);
        Assert.Equal("SoftmodInstaller.zip", source.AssetPattern);
        Assert.Equal(ReleaseSelectionPolicy.ExactTag, source.ReleasePolicy);
        Assert.Equal("v1.0.477", source.ReleaseTag);
        Assert.Equal("7C7D42C301E72145B9D1F0A0E23FF164FDEFBBE780C2019A3CE7465E963B1827", source.TrustedSHA256);
    }

    [Theory]
    [InlineData("XeUnshackle")]
    [InlineData("XeUnshackleMax")]
    [InlineData("FreeMyXe")]
    public void Selection_IncludesHomebrewWithoutASeparateBootstrap(string bootstrap)
    {
        BuilderConfig config = new(ArtifactCatalog.Homebrew)
        {
            SelectedExploit = ExploitOption.PeerPressure,
            SelectedBootstrap = Enum.Parse<BootstrapOption>(bootstrap),
            LaunchHomebrew = ArtifactCatalog.Homebrew[0],
        };

        IReadOnlyList<ArtifactDefinition> selected = ArtifactCatalog.GetSelectedArtifacts(config);

        Assert.True(config.IsPeerPressureInstall);
        Assert.Equal(["badupdate-gamedata", "exploit-peerpressure", "homebrew-aurora", "homebrew-xexmenu", "homebrew-simple360nandflasher"],
            selected.Select(artifact => artifact.ID));
        Assert.DoesNotContain("XeUnshackle", config.ToString(), StringComparison.Ordinal);
        Assert.Contains("Aurora", config.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardUpdate_StillTakesPrecedenceOverPeerPressure()
    {
        BuilderConfig config = new(ArtifactCatalog.Homebrew)
        {
            SelectedExploit = ExploitOption.PeerPressure,
            FirmwareUpdateEnabled = true,
        };

        Assert.Equal("dashboard-update", Assert.Single(ArtifactCatalog.GetSelectedArtifacts(config)).ID);
        Assert.False(config.IsPeerPressureInstall);
    }

    [Fact]
    public async Task Install_MergesGameAndInstallerContentWithoutReplacingInstallerPayload()
    {
        using TemporaryDirectory temporary = new();
        BuilderConfig config = new([]) { SelectedExploit = ExploitOption.PeerPressure };
        List<(ArtifactDefinition Artifact, string StagingPath)> staged = [];
        foreach (ArtifactDefinition artifact in ArtifactCatalog.GetSelectedArtifacts(config))
        {
            string root = Path.Combine(temporary.Path, artifact.ID);
            Directory.CreateDirectory(root);
            foreach (string required in artifact.Layout!.RequiredPaths!)
            {
                string path = Path.Combine(root, required);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (required == ".")
                    continue;
                await File.WriteAllTextAsync(path, required);
            }

            string content = Path.Combine(root, "Content", "0000000000000000", "5841122D",
                artifact.ID == "badupdate-gamedata" ? "000D0000/demo" : "00000001/DaTArrest_Xbox360BadUpdate");
            Directory.CreateDirectory(Path.GetDirectoryName(content)!);
            await File.WriteAllTextAsync(content, artifact.ID);
            staged.Add((artifact, root));
        }

        InstallPlan plan = InstallService.BuildPlan(staged, [], 128L * 1024 * 1024);
        string target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(target);
        await InstallService.ExecuteAsync(plan, target, CancellationToken.None);

        Assert.Equal("BadUpdatePayload/default.xex",
            await File.ReadAllTextAsync(Path.Combine(target, "BadUpdatePayload", "default.xex")));
        Assert.True(File.Exists(Path.Combine(target, "BadUpdatePayload", "SoftmodData.zip")));
        Assert.Equal("badupdate-gamedata", await File.ReadAllTextAsync(Path.Combine(target,
            "Content", "0000000000000000", "5841122D", "000D0000", "demo")));
        Assert.Equal("exploit-peerpressure", await File.ReadAllTextAsync(Path.Combine(target,
            "Content", "0000000000000000", "5841122D", "00000001", "DaTArrest_Xbox360BadUpdate")));
        Assert.False(File.Exists(Path.Combine(target, "launch.ini")));
    }

    [Theory]
    [InlineData("BadUpdatePayload/default.xex")]
    [InlineData("BadUpdatePayload/SoftmodData.zip")]
    [InlineData("Content/0000000000000000/5841122D/00000001/DaTArrest_Xbox360BadUpdate")]
    public void Install_RejectsAnIncompleteInstaller(string missingPath)
    {
        using TemporaryDirectory temporary = new();
        ArtifactDefinition artifact = ArtifactCatalog.Exploits[ExploitOption.PeerPressure];
        foreach (string required in artifact.Layout!.RequiredPaths!.Where(path => path != missingPath))
        {
            string path = Path.Combine(temporary.Path, required);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }

        Assert.Throws<InvalidDataException>(() => InstallService.BuildPlan(
            [(artifact, temporary.Path)], [], 128L * 1024 * 1024));
    }

    [Fact]
    public async Task OptIn_RealArchivesPrepareInstallerUsbInTemporaryDirectory()
    {
        string? archives = Environment.GetEnvironmentVariable("BADBUILDER_PEER_PRESSURE_ARCHIVES");
        if (string.IsNullOrWhiteSpace(archives))
            return;

        using TemporaryDirectory temporary = new();
        BuilderConfig config = new(ArtifactCatalog.Homebrew) { SelectedExploit = ExploitOption.PeerPressure };
        config.LaunchHomebrew = config.Homebrew[0];
        List<(ArtifactDefinition Artifact, string StagingPath)> staged = [];
        foreach (ArtifactDefinition artifact in ArtifactCatalog.GetSelectedArtifacts(config))
        {
            GitHubReleaseSource source = Assert.IsType<GitHubReleaseSource>(artifact.Source);
            string archive = Path.Combine(archives, source.AssetPattern);
            await using (FileStream stream = File.OpenRead(archive))
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                DownloadService.EnsureHashMatches(artifact.DisplayName, hash, source.TrustedSHA256);
            }
            string root = await ArchiveService.ExtractAsync(artifact.ID, archive, temporary.Path,
                ArchiveExtractionLimits.CreateDefault(temporary.Path, 1024L * 1024 * 1024), CancellationToken.None);
            staged.Add((artifact, root));
        }

        var prepared = PeerPressureHomebrewService.PrepareArtifacts(config, staged);
        InstallPlan plan = InstallService.BuildPlan(prepared, [], 1024L * 1024 * 1024);
        string target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(target);
        await InstallService.ExecuteAsync(plan, target, CancellationToken.None);

        foreach (PlannedInstallAction action in plan.Actions.Where(action => action.Kind == PlannedActionKind.CopyFile &&
            action.DestinationRelativePath != "BadUpdatePayload/SoftmodExtras/SoftmodExtras.ini"))
        {
            await using FileStream source = File.OpenRead(action.SourcePath!);
            await using FileStream installed = File.OpenRead(Path.Combine(target, action.DestinationRelativePath));
            Assert.Equal(await SHA256.HashDataAsync(source), await SHA256.HashDataAsync(installed));
        }
        string ini = await File.ReadAllTextAsync(Path.Combine(target, "BadUpdatePayload", "SoftmodExtras", "SoftmodExtras.ini"));
        Assert.Contains("[Xell]", ini, StringComparison.Ordinal);
        Assert.Contains("DashXex = \"Aurora.xex\"", ini, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(target, "BadUpdatePayload", "SoftmodExtras", "homebrew-aurora", "Aurora.xex")));
        Assert.False(Directory.Exists(Path.Combine(target, "Apps")));
        Assert.False(File.Exists(Path.Combine(target, "launch.ini")));
    }
}
