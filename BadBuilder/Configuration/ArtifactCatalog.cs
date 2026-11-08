namespace BadBuilder.Configuration;

internal static class ArtifactCatalog
{
    private static readonly Dictionary<ExploitOption, ArtifactDefinition> ExploitMap = new()
    {
        [ExploitOption.PeerPressure] = new
        (
            "exploit-peerpressure",
            "Peer Pressure",
            "Persistent softmod installer; requires an HDD and a supported console (not Corona/Winchester)",
            "exploit",
            new GitHubReleaseSource(
                "grimdoomer",
                "Xbox360PeerPressure",
                "SoftmodInstaller.zip",
                ReleaseSelectionPolicy.ExactTag,
                "v1.0.477",
                "7C7D42C301E72145B9D1F0A0E23FF164FDEFBBE780C2019A3CE7465E963B1827",
                4_978_547),
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")],
            ArtifactPriority.Exploit,
            Layout: new ArchiveLayout([
                "BadUpdatePayload/default.xex",
                "BadUpdatePayload/BadUpdateExploit-2ndStage.bin",
                "BadUpdatePayload/BadUpdateExploit-3rdStage.bin",
                "BadUpdatePayload/BadUpdateExploit-4thStage.bin",
                "BadUpdatePayload/BadUpdateExploit-Data.bin",
                "BadUpdatePayload/SoftmodData.zip",
                "BadUpdatePayload/bootanim.xex",
                "BadUpdatePayload/update_data.bin",
                "BadUpdatePayload/xke_update.bin",
                "BadUpdatePayload/SoftmodExtras/SoftmodExtras.ini",
                "BadUpdatePayload/SoftmodExtras/Xell/boot.ini",
                "BadUpdatePayload/SoftmodExtras/Xell/xell-2f.bin",
                "Content/0000000000000000/5841122D/00000001/DaTArrest_Xbox360BadUpdate"
            ])
        ),
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
                ReleaseSelectionPolicy.ExactTag,
                "vPB1.0",
                "036718C53E76C8845E9459FC414B5E74B9A6F45A31E7ECF7DE561D84BAB51D66",
                204_279),
            [ new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".") ],
            ArtifactPriority.Exploit,
            Layout: new ArchiveLayout(["BadUpdatePayload"])
        ),
        [ExploitOption.ABadAvatar13Beta] = new
        (
            "exploit-abadavatar-1-3-beta",
            "ABadAvatar 1.3 Beta",
            "ABadAvatar rebased on BadUpdate 1.3 for much faster and more reliable execution; still beta",
            "exploit",
            new GitHubReleaseSource(
                "bibarub",
                "Xbox360BadUpdate",
                "ABadAvatar_v1.3-beta.zip",
                ReleaseSelectionPolicy.ExactTag,
                "avatar-v1.3-beta",
                "A94E0948A5F10AA288BA3A5909B55F90AE56C3CFA628B9D429E178737A0F1E35",
                287_282),
            [ new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".") ],
            ArtifactPriority.Exploit,
            Layout: new ArchiveLayout(["BadUpdatePayload", "Content"])
        ),
        [ExploitOption.BadUpdate] = new
        (
            "exploit-badupdate",
            "BadUpdate",
            "The original exploit utilizing savedata exploits in games like Rock Band Blitz",
            "exploit",
            new GitHubReleaseSource(
                "grimdoomer",
                "Xbox360BadUpdate",
                "Xbox360BadUpdate-Retail-USB-*.zip",
                ReleaseSelectionPolicy.ExactTag,
                "BadUpdate-v1.3",
                "CFD56BCA11EC94BBB132A29633EEEA82C0F02C016A83F9A8E6C9DF2714DFE003",
                27_996_626),
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
            new GitHubReleaseSource(
                "Byrom90",
                "XeUnshackle",
                "XeUnshackle-BETA-*.zip",
                ReleaseSelectionPolicy.ExactTag,
                "v1.03",
                "DFC8CBC8ED2DDB6D2F453A52ADE2079D5F0D804BBD9A4635DE00BCBD236F792C",
                4_062_601),
            [
                new InstallOperation(
                    InstallOperationKind.CopyDirectory,
                    ".",
                    "<SUBFOLDER>/.",
                    OptionalOverwritePaths: ["BadUpdatePayload/default.xex"])
            ],
            ArtifactPriority.Bootstrap,
            Layout: new ArchiveLayout(RequireSingleTopLevelDirectory: true)
        ),
        [BootstrapOption.XeUnshackleMax] = new
        (
            "bootstrap-xeunshackle-max",
            "XeUnshackle Max",
            "XeUnshackle fork with auto-start, key masking and configurable boot video (beta)",
            "bootstrap",
            new GitHubReleaseSource(
                "klofi",
                "XeUnshackle-Max",
                "XeUnshackle-Max-v1.0.0.zip",
                ReleaseSelectionPolicy.ExactTag,
                "v1.0.0",
                "838CF16450790601158265C22C84D90C1F9FFFF7BA07854B9781722DBA2328E4",
                4_012_667),
            [
                // Network-access plugins included in the ZIP are not installed automatically.
                new InstallOperation(
                    InstallOperationKind.CopyDirectory,
                    "BadUpdatePayload",
                    "<SUBFOLDER>/BadUpdatePayload",
                    OptionalOverwritePaths: ["BadUpdatePayload/default.xex"]),
                new InstallOperation(
                    InstallOperationKind.CopyFile,
                    "launch.ini",
                    "<SUBFOLDER>/launch.ini")
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
            new GitHubReleaseSource(
                "FreeMyXe",
                "FreeMyXe",
                "FreeMyXe-*.zip",
                ReleaseSelectionPolicy.ExactTag,
                "1.2.1",
                "AB6688AD7BFB286A2E202C060561A6202038E936BACEF2DB8BEF7B99BC4A2A6C",
                263_909),
            Operations:
            [
                new InstallOperation(InstallOperationKind.CopyDirectory, "BadUpdatePayload", "."),
                new InstallOperation(
                    InstallOperationKind.RenameFile,
                    "BadUpdatePayload/default.xex",
                    "BadUpdatePayload/FreeMyXe.xex",
                    OptionalOverwritePaths: ["BadUpdatePayload/default.xex"])
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
                new GitHubReleaseSource(
                    "Pdawg-bytes",
                    "BadBuilder",
                    "Aurora.rar",
                    ReleaseSelectionPolicy.ExactTag,
                    "v0.10a",
                    "0C7C765C3A5B938CFC64C71BEDA276A17D109B88DF2A88E3F91EB1E74B7E46A0",
                    22_647_806),
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
                new GitHubReleaseSource(
                    "Pdawg-bytes",
                    "BadBuilder",
                    "MenuData.7z",
                    ReleaseSelectionPolicy.ExactTag,
                    "v0.10a",
                    "2684EEA2CF2B8BA7A37DBF039440482AB36093CB950B4A99A9D7B942EE8FC6D4",
                    109_169_994),
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
                new GitHubReleaseSource(
                    "Pdawg-bytes",
                    "BadBuilder",
                    "Flasher.7z",
                    ReleaseSelectionPolicy.ExactTag,
                    "v0.10a",
                    "0241219BF2D06871842B23F3AF6DB150658361656DFC53342B631BAFAB06FE1D",
                    1_069_864),
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
        new GitHubReleaseSource(
            "Pdawg-bytes",
            "BadBuilder",
            "GameData.zip",
            ReleaseSelectionPolicy.ExactTag,
            "v0.10a",
            "2FBB57F09D73298FF9DF03A47367BE0CFB007431F1B8A627FC04F0EBC17CD559",
            286_678_579),
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
        new DirectSource(
            "https://download.microsoft.com/download/8/f/4/8f456817-e264-4207-9b95-6efc990fee98/SystemUpdate_17559_USB.zip",
            "38885A3AB6B9854FB4FA31D5DDF21A4FDDFD4CFB25E219060CFB0C2FB31F208F",
            115_992_188),
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
        else if (config.IsPeerPressureInstall)
        {
            selected.Add(BadUpdateGameData);
            selected.Add(ExploitMap[ExploitOption.PeerPressure]);
            selected.AddRange(config.Homebrew.Select(entry => entry.Artifact));
        }
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
