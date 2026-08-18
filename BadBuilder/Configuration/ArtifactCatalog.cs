namespace BadBuilder.Configuration;

internal sealed class ArtifactCatalog
{
    private readonly Dictionary<ExploitOption,   ArtifactDefinition> _exploitMap;
    private readonly Dictionary<BootstrapOption, ArtifactDefinition> _bootstrapMap;
    private readonly List<HomebrewEntry> _homebrew;
    private readonly ArtifactDefinition  _badUpdateGameData;
    private readonly ArtifactDefinition  _dashboardUpdate;

    private ArtifactCatalog(
        Dictionary<ExploitOption,   ArtifactDefinition> exploitMap,
        Dictionary<BootstrapOption, ArtifactDefinition> bootstrapMap,
        List<HomebrewEntry> homebrew,
        ArtifactDefinition badUpdateGameData,
        ArtifactDefinition dashboardUpdate)
    {
        _exploitMap        = exploitMap;
        _bootstrapMap      = bootstrapMap;
        _homebrew          = homebrew;
        _badUpdateGameData = badUpdateGameData;
        _dashboardUpdate   = dashboardUpdate;
    }

    public IReadOnlyDictionary<ExploitOption,   ArtifactDefinition> Exploits   => _exploitMap;
    public IReadOnlyDictionary<BootstrapOption, ArtifactDefinition> Bootstraps => _bootstrapMap;
    public IReadOnlyList<HomebrewEntry> Homebrew                               => _homebrew;

    public static ArtifactCatalog CreateDefault()
    {
        Dictionary<ExploitOption, ArtifactDefinition> exploitMap = new()
        {
            [ExploitOption.ABadAvatar] = new
            (
                "exploit-abadavatar",
                "ABadAvatar",
                "A variant of the exploit targeting the Xbox 360 profile avatar system",
                "exploit",
                new GitHubReleaseSource("shutterbug2000", "ABadAvatar"),
                [ new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".") ],
                ArtifactPriority.Exploit
            ),

            [ExploitOption.BadUpdate] = new
            (
                "exploit-badupdate",
                "BadUpdate",
                "The original exploit utilizing savedata exploits in games like Rock Band Blitz",
                "exploit",
                new GitHubReleaseSource("grimdoomer", "Xbox360BadUpdate"),
                [ new InstallOperation(InstallOperationKind.CopyDirectory, ".", "Rock Band Blitz") ],
                ArtifactPriority.Exploit
            ),
        };

        Dictionary<BootstrapOption, ArtifactDefinition> bootstrapMap = new()
        {
            [BootstrapOption.XeUnshackle] = new
            (
                "bootstrap-xeunshackle",
                "XeUnshackle",
                "Full payload with Dashlaunch, plugin support, and higher game compatibility",
                "bootstrap",
                new GitHubReleaseSource("Byrom90", "XeUnshackle"),
                [ new InstallOperation(InstallOperationKind.CopyDirectory, ".", "<SUBFOLDER>\\.") ],
                ArtifactPriority.Bootstrap
            ),

            [BootstrapOption.FreeMyXe] = new
            (
                "bootstrap-freemyxe",
                "FreeMyXe",
                "Lightweight payload designed to apply essential patches for launching homebrew, XeLL, and LibXenon",
                "bootstrap",
                new GitHubReleaseSource("FreeMyXe", "FreeMyXe"),
                Operations:
                [
                    new InstallOperation(InstallOperationKind.CopyDirectory, "BadUpdatePayload", "."),
                    new InstallOperation(InstallOperationKind.RenameFile, "BadUpdatePayload/default.xex", "BadUpdatePayload/FreeMyXe.xex")
                ],
                ArtifactPriority.Bootstrap
            ),
        };

        List<HomebrewEntry> homebrew =
        [
            new
            (
                new ArtifactDefinition
                (
                    "homebrew-aurora",
                    "Aurora",
                    "Featured dashboard replacement with plugin support.",
                    "homebrew/aurora",
                    new GitHubReleaseSource("Pdawg-bytes", "BadBuilder", "v0.10a", "Aurora.rar"),
                    [ new InstallOperation(InstallOperationKind.CopyDirectory, "Apps/Aurora", ".") ],
                    ArtifactPriority.Homebrew
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
                    new GitHubReleaseSource("Pdawg-bytes", "BadBuilder", "v0.10a", "MenuData.7z"),
                    [ new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".") ],
                    ArtifactPriority.Homebrew
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
                    new GitHubReleaseSource("Pdawg-bytes", "BadBuilder", "v0.10a", "Flasher.7z"),
                    [ new InstallOperation(InstallOperationKind.CopyDirectory, "Apps/Simple 360 NAND Flasher", "Simple 360 NAND Flasher") ],
                    ArtifactPriority.Homebrew
                ),
                EntryPointRelativePath: "Simple 360 NAND Flasher/Default.xex"
            ),
        ];

        ArtifactDefinition badUpdateGameData = new
        (
            "badupdate-gamedata",
            "Rock Band Blitz",
            "Game data required by the BadUpdate package.",
            string.Empty,
            new GitHubReleaseSource("Pdawg-bytes", "BadBuilder", "v0.10a", "GameData.zip"),
            [ new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".") ],
            ArtifactPriority.RockBandBlitz
        );

        ArtifactDefinition dashboardUpdate = new
        (
            "dashboard-update",
            "Dashboard Update",
            "The official Xbox 360 dashboard update package.",
            string.Empty,
            new DirectSource("https://download.microsoft.com/download/8/f/4/8f456817-e264-4207-9b95-6efc990fee98/SystemUpdate_17559_USB.zip"),
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")],
            ArtifactPriority.DashboardUpdate
        );

        return new ArtifactCatalog(exploitMap, bootstrapMap, homebrew, badUpdateGameData, dashboardUpdate);
    }

    public IReadOnlyList<ArtifactDefinition> GetSelectedArtifacts(BuilderConfig config)
    {
        List<ArtifactDefinition> selected = [];

        if (config.FirmwareUpdateEnabled)
            selected.Add(_dashboardUpdate);
        else
        {
            selected.Add(_exploitMap[config.SelectedExploit]);
            selected.Add(_bootstrapMap[config.SelectedBootstrap]);

            if (config.SelectedExploit == ExploitOption.BadUpdate)
                selected.Add(_badUpdateGameData);

            selected.AddRange(config.Homebrew
                .Select(homebrew => homebrew.Artifact)
                .OfType<ArtifactDefinition>());
        }

        return [..selected.OrderBy(artifact => artifact.Priority)];
    }
}