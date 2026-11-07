using BadBuilder.Services.Disks;

namespace BadBuilder.Configuration;

internal sealed class BuilderConfig
{
    internal BuilderConfig(IEnumerable<HomebrewEntry> builtInHomebrew) =>Homebrew.AddRange(builtInHomebrew);

    internal DiskInfo? TargetDisk { get; set; }

    internal ExploitOption SelectedExploit     { get; set; } = ExploitOption.BadUpdate;
    internal BootstrapOption SelectedBootstrap { get; set; } = BootstrapOption.XeUnshackle;

    internal List<HomebrewEntry> Homebrew  { get; } = [];
    internal HomebrewEntry? LaunchHomebrew { get; set; }

    internal bool FirmwareUpdateEnabled { get; set; }

    internal bool IsPeerPressureInstall => !FirmwareUpdateEnabled && SelectedExploit == ExploitOption.PeerPressure;

    public override string ToString()
    {
        if (IsPeerPressureInstall)
            return $"""
                Target drive: {TargetDisk}

                Selected exploit: Peer Pressure
                USB contents: Official softmod installer and Rock Band Blitz demo (includes XeLL).
                Installer extras: {string.Join(", ", Homebrew.Select(item => item.Artifact.DisplayName))}
                Default dashboard: {LaunchHomebrew?.Artifact.DisplayName ?? "N/A"}
                """;

        return 
        $"""
            Target drive: {TargetDisk}

            Selected exploit: {SelectedExploit}
            Selected bootstrap: {SelectedBootstrap}

            Homebrew: {string.Join(", ", Homebrew.Select(item => item.Artifact.DisplayName))}
            Default launch homebrew: {LaunchHomebrew?.Artifact.DisplayName ?? "N/A"}
        """;
    }
}
