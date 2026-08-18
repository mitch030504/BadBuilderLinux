using BadBuilder.Services.Disks;

namespace BadBuilder.Configuration;

internal sealed class BuilderConfig
{
    public BuilderConfig(IEnumerable<HomebrewEntry> builtInHomebrew)
    {
        Homebrew.AddRange(builtInHomebrew);
    }

    public string? MountPoint { get; set; }
    public DiskInfo? TargetDisk { get; set; }

    public ExploitOption SelectedExploit     { get; set; } = ExploitOption.BadUpdate;
    public BootstrapOption SelectedBootstrap { get; set; } = BootstrapOption.XeUnshackle;

    public List<HomebrewEntry> Homebrew {  get; } = [];
    public HomebrewEntry? LaunchHomebrew { get; set; }

    public bool FirmwareUpdateEnabled { get; set; } = false;

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