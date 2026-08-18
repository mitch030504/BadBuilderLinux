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

    public override string ToString()
    {
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
