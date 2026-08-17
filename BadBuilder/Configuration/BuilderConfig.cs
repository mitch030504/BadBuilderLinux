namespace BadBuilder.Configuration;

internal sealed class BuilderConfig
{
    public BuilderConfig(IEnumerable<HomebrewEntry> builtInHomebrew)
    {
        Homebrew.AddRange(builtInHomebrew);
    }

    public string? MountPoint { get; set; }

    public ExploitOption SelectedExploit     { get; set; } = ExploitOption.BadUpdate;
    public BootstrapOption SelectedBootstrap { get; set; } = BootstrapOption.XeUnshackle;

    public List<HomebrewEntry> Homebrew { get; } = [];
    public string? LaunchHomebrewId     { get; set; }

    public override string ToString()
    {
        return 
        $"""
            Target drive: {MountPoint}

            Selected exploit: {SelectedExploit}
            Selected bootstrap: {SelectedBootstrap}

            Homebrew: {string.Join(", ", Homebrew.Select(item => item.DisplayName))}
            Default launch homebrew: {LaunchHomebrewId ?? "N/A"}
        """;
    }
}