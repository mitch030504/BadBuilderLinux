namespace BadBuilder.Configuration;

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


internal sealed record GitHubReleaseSource(string Owner, string Repo, string? ReleaseTag = null, string? AssetName = null);

internal sealed record HomebrewEntry(
    string ID,
    string DisplayName,
    string Description,
    ArtifactDefinition? Artifact = null,
    string? SourcePath = null,
    string? EntryPointRelativePath = null);

internal sealed record ArtifactDefinition(
    string ID,
    string DisplayName,
    string Description,
    string DestinationRelativePath,
    GitHubReleaseSource? Source,
    IReadOnlyList<InstallOperation>? Operations = null,
    ArtifactPriority Priority = ArtifactPriority.Homebrew,
    string? LocalArchivePath = null);

internal enum ArtifactPriority
{
    RockBandBlitz,
    Exploit,
    Bootstrap,
    Homebrew,
}


internal sealed record InstallOperation(
    InstallOperationKind Kind,
    string DestinationPath,
    string? SourcePath = null,
    string? Contents = null);

internal enum InstallOperationKind
{
    CopyDirectory,
    CopyFile,
    WriteFile,
    RenameFile,
}