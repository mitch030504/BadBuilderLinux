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


internal enum ReleaseSelectionPolicy
{
    LatestStable,
    LatestIncludingPrerelease,
    ExactTag,
}

internal abstract record Source(string? TrustedSHA256 = null);

internal sealed record GitHubReleaseSource(
    string Owner,
    string Repo,
    string AssetPattern,
    ReleaseSelectionPolicy ReleasePolicy = ReleaseSelectionPolicy.LatestStable,
    string? ReleaseTag = null,
    string? PinnedSHA256 = null) : Source(PinnedSHA256);

internal sealed record DirectSource(string URL, string? PinnedSHA256 = null) : Source(PinnedSHA256);

internal sealed record ArchiveLayout(
    IReadOnlyList<string>? RequiredPaths = null,
    bool RequireSingleTopLevelDirectory = false);

internal sealed record HomebrewEntry(
    ArtifactDefinition Artifact,
    string? SourcePath = null,
    string? EntryPointRelativePath = null);

internal sealed record ArtifactDefinition(
    string ID,
    string DisplayName,
    string Description,
    string DestinationRelativePath,
    Source? Source,
    IReadOnlyList<InstallOperation>? Operations = null,
    ArtifactPriority Priority = ArtifactPriority.Homebrew,
    string? LocalArchivePath = null,
    ArchiveLayout? Layout = null);

internal enum ArtifactPriority
{
    DashboardUpdate,
    RockBandBlitz,
    Exploit,
    Bootstrap,
    Homebrew,
}


internal sealed record InstallOperation(
    InstallOperationKind Kind,
    string DestinationPath,
    string? SourcePath = null,
    string? Contents = null,
    IReadOnlyList<string>? AllowedOverwritePaths = null);

internal enum InstallOperationKind
{
    CopyDirectory,
    CopyFile,
    WriteFile,
    RenameFile,
}
