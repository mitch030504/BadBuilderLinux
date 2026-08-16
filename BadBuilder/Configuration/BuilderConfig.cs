namespace BadBuilder.Configuration;

internal sealed class BuilderConfig
{
    public BuilderConfig(IEnumerable<HomebrewEntry> builtInHomebrew)
    {
        Homebrew.AddRange(builtInHomebrew);
    }

    public string? TargetDrive { get; set; }

    public ExploitOption SelectedExploit     { get; set; } = ExploitOption.BadUpdate;
    public BootstrapOption SelectedBootstrap { get; set; } = BootstrapOption.XeUnshackle;

    public List<HomebrewEntry> Homebrew { get; } = [];
    public string? LaunchHomebrewId     { get; set; }

    public override string ToString()
    {
        return 
        $"""
            Target drive: {TargetDrive}

            Selected exploit: {SelectedExploit}
            Selected bootstrap: {SelectedBootstrap}

            Homebrew: {string.Join(", ", Homebrew.Select(item => item.DisplayName))}
            Default launch homebrew: {LaunchHomebrewId ?? "N/A"}
        """;
    }
}

internal sealed record HomebrewEntry(string ID, string DisplayName, string Description, ArtifactDefinition? Artifact = null, string? SourcePath = null, string? EntryPointRelativePath = null);


internal enum ExploitOption
{
    ABadAvatar,
    BadUpdate,
}

internal enum BootstrapOption
{
    XeUnshackle,
    FreeMyXe,
}