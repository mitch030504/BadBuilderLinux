namespace BadBuilder.Configuration;

internal static class ArtifactCatalog
{
    private static readonly Dictionary<ExploitOption, ArtifactDefinition> ExploitMap = new()
    {
        [ExploitOption.ABadAvatar] = new
        (
            "exploit-abadavatar",
            "ABadAvatar",
            "A variant of the exploit targeting the Xbox 360 profile avatar system",
            "exploit",
            new GitHubReleaseSource(
                "shutterbug2000",
                "ABadAvatar",
                "ABadAvatar-publicbeta*.zip",
                ReleaseSelectionPolicy.LatestIncludingPrerelease),
            [ new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".") ],
            ArtifactPriority.Exploit,
            Layout: new ArchiveLayout(["BadUpdatePayload"])
        ),
        [ExploitOption.BadUpdate] = new
        (
            "exploit-badupdate",
            "BadUpdate",
            "The original exploit utilizing savedata exploits in games like Rock Band Blitz",
            "exploit",
            new GitHubReleaseSource("grimdoomer", "Xbox360BadUpdate", "Xbox360BadUpdate-Retail-USB-*.zip"),
            [ new InstallOperation(InstallOperationKind.CopyDirectory, ".", "Rock Band Blitz") ],
            ArtifactPriority.Exploit,
            Layout: new ArchiveLayout(["Rock Band Blitz"])
        ),
    };

    private static readonly Dictionary<BootstrapOption, ArtifactDefinition> BootstrapMap = new()
    {
        [BootstrapOption.XeUnshackle] = new
        (
            "bootstrap-xeunshackle",
            "XeUnshackle",
            "Full payload with Dashlaunch, plugin support, and higher game compatibility",
            "bootstrap",
            new GitHubReleaseSource("Byrom90", "XeUnshackle", "XeUnshackle-BETA-*.zip"),
            [
                new InstallOperation(
                    InstallOperationKind.CopyDirectory,
                    ".",
                    "<SUBFOLDER>/.",
                    AllowedOverwritePaths: ["BadUpdatePayload/default.xex"])
            ],
            ArtifactPriority.Bootstrap,
            Layout: new ArchiveLayout(RequireSingleTopLevelDirectory: true)
        ),
        [BootstrapOption.FreeMyXe] = new
        (
            "bootstrap-freemyxe",
            "FreeMyXe",
            "Lightweight payload designed to apply essential patches for launching homebrew, XeLL, and LibXenon",
            "bootstrap",
            new GitHubReleaseSource("FreeMyXe", "FreeMyXe", "FreeMyXe-*.zip"),
            Operations:
            [
                new InstallOperation(InstallOperationKind.CopyDirectory, "BadUpdatePayload", "."),
                new InstallOperation(
                    InstallOperationKind.RenameFile,
                    "BadUpdatePayload/default.xex",
                    "BadUpdatePayload/FreeMyXe.xex",
                    AllowedOverwritePaths: ["BadUpdatePayload/default.xex"])
            ],
            ArtifactPriority.Bootstrap,
            Layout: new ArchiveLayout(["FreeMyXe.xex"])
        ),
    };

    private static readonly List<HomebrewEntry> HomebrewEntries =
    [
        new
        (
            new ArtifactDefinition
            (
                "homebrew-aurora",
                "Aurora",
                "Featured dashboard replacement with plugin support.",
                "homebrew/aurora",
                new GitHubReleaseSource("Pdawg-bytes", "BadBuilder", "Aurora.rar", ReleaseSelectionPolicy.ExactTag, "v0.10a"),
                [ new InstallOperation(InstallOperationKind.CopyDirectory, "Apps/Aurora", ".") ],
                ArtifactPriority.Homebrew,
                Layout: new ArchiveLayout(["."])
            ),
            EntryPointRelativePath: "Aurora.xex"
        ),
        new
        (
            new ArtifactDefinition
            (
                "homebrew-xexmenu",
                "XeXMenu",
                "Simple launcher and file manager.",
                "homebrew/xexmenu",
                new GitHubReleaseSource("Pdawg-bytes", "BadBuilder", "MenuData.7z", ReleaseSelectionPolicy.ExactTag, "v0.10a"),
                [ new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".") ],
                ArtifactPriority.Homebrew,
                Layout: new ArchiveLayout(["."])
            ),
            EntryPointRelativePath: null
        ),
        new
        (
            new ArtifactDefinition
            (
                "homebrew-simple360nandflasher",
                "Simple 360 NAND Flasher",
                "NAND flashing utility kept available for maintenance scenarios.",
                "homebrew/simple360nandflasher",
                new GitHubReleaseSource("Pdawg-bytes", "BadBuilder", "Flasher.7z", ReleaseSelectionPolicy.ExactTag, "v0.10a"),
                [ new InstallOperation(InstallOperationKind.CopyDirectory, "Apps/Simple 360 NAND Flasher", "Simple 360 NAND Flasher") ],
                ArtifactPriority.Homebrew,
                Layout: new ArchiveLayout(["Simple 360 NAND Flasher"])
            ),
            EntryPointRelativePath: "Default.xex"
        ),
    ];

    private static readonly ArtifactDefinition BadUpdateGameData = new
    (
        "badupdate-gamedata",
        "Rock Band Blitz",
        "Game data required by the BadUpdate package.",
        string.Empty,
        new GitHubReleaseSource("Pdawg-bytes", "BadBuilder", "GameData.zip", ReleaseSelectionPolicy.ExactTag, "v0.10a"),
        [ new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".") ],
        ArtifactPriority.RockBandBlitz,
        Layout: new ArchiveLayout(["."])
    );

    private static readonly ArtifactDefinition DashboardUpdate = new
    (
        "dashboard-update",
        "Dashboard Update",
        "The official Xbox 360 dashboard update package.",
        string.Empty,
        new DirectSource("https://download.microsoft.com/download/8/f/4/8f456817-e264-4207-9b95-6efc990fee98/SystemUpdate_17559_USB.zip"),
        [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")],
        ArtifactPriority.DashboardUpdate,
        Layout: new ArchiveLayout(["$SystemUpdate"])
    );


    internal static IReadOnlyDictionary<ExploitOption,   ArtifactDefinition> Exploits   => ExploitMap;
    internal static IReadOnlyDictionary<BootstrapOption, ArtifactDefinition> Bootstraps => BootstrapMap;
    internal static IReadOnlyList<HomebrewEntry> Homebrew                               => HomebrewEntries;

    internal static IReadOnlyList<ArtifactDefinition> GetAllBuiltInArtifacts() =>
    [
        ..ExploitMap.Values,
        ..BootstrapMap.Values,
        ..HomebrewEntries.Select(entry => entry.Artifact),
        BadUpdateGameData,
        DashboardUpdate,
    ];

    internal static IReadOnlyList<ArtifactDefinition> GetSelectedArtifacts(BuilderConfig config)
    {
        List<ArtifactDefinition> selected = [];

        if (config.FirmwareUpdateEnabled)
            selected.Add(DashboardUpdate);
        else
        {
            selected.Add(ExploitMap[config.SelectedExploit]);
            selected.Add(BootstrapMap[config.SelectedBootstrap]);

            if (config.SelectedExploit == ExploitOption.BadUpdate)
                selected.Add(BadUpdateGameData);

            selected.AddRange(config.Homebrew
                .Select(homebrew => homebrew.Artifact)
                .OfType<ArtifactDefinition>());
        }

        return [.. selected.OrderBy(artifact => artifact.Priority)];
    }
}
