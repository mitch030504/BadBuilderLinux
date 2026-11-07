using System.Globalization;
using System.Text;
using BadBuilder.Configuration;

namespace BadBuilder.Services;

internal static class PeerPressureHomebrewService
{
    private const string ExtrasRoot = "BadUpdatePayload/SoftmodExtras";
    private const string ExtrasIni = ExtrasRoot + "/SoftmodExtras.ini";

    internal static List<(ArtifactDefinition Artifact, string StagingPath)> PrepareArtifacts(
        BuilderConfig config,
        IReadOnlyList<(ArtifactDefinition Artifact, string StagingPath)> staged)
    {
        if (config.LaunchHomebrew is not null && !config.Homebrew.Any(entry => entry.Artifact.ID == config.LaunchHomebrew.Artifact.ID))
            throw new InvalidOperationException("The default dashboard must be included in the homebrew selection.");

        List<(ArtifactDefinition Artifact, string StagingPath)> prepared = [..staged];
        if (config.Homebrew.Count == 0)
            return prepared;

        string installerId = ArtifactCatalog.Exploits[ExploitOption.PeerPressure].ID;
        int installerIndex = prepared.FindIndex(item => item.Artifact.ID == installerId);
        if (installerIndex < 0)
            throw new InvalidDataException("The Peer Pressure installer archive was not staged.");
        (ArtifactDefinition installer, string installerRoot) = prepared[installerIndex];
        StringBuilder ini = new(File.ReadAllText(PathSafety.ResolveInside(installerRoot, ExtrasIni)));

        foreach (HomebrewEntry entry in config.Homebrew)
        {
            string id = FileServices.ValidateIdentifier(entry.Artifact.ID);
            string name = entry.Artifact.DisplayName;
            if (string.IsNullOrWhiteSpace(name) || name.Any(character => char.IsControl(character) || character == '"'))
                throw new InvalidDataException("A Peer Pressure homebrew name cannot contain quotes or control characters.");

            int index = prepared.FindIndex(item => item.Artifact.ID == id);
            if (index < 0)
                throw new InvalidDataException($"The homebrew archive for {name} was not staged.");
            (ArtifactDefinition artifact, string root) = prepared[index];
            // shortcut: extras use one directory copy per app; extend if the homebrew catalog adds other layouts.
            if (artifact.Operations is not [InstallOperation { Kind: InstallOperationKind.CopyDirectory } operation])
                throw new InvalidDataException($"{name} must install from one directory to be packaged as a Peer Pressure extra.");

            string destination = XboxPath.Combine("Hdd1", operation.DestinationPath);
            ini.Append(CultureInfo.InvariantCulture, $"\r\n[BadBuilder-{id}]\r\nCategory = \"Homebrew\"\r\nName = \"{name}\"\r\nDescription = \"Installs {name}.\"\r\nSource = \"{id}\"\r\nDestination = \"{destination}\"\r\n");
            if (config.LaunchHomebrew?.Artifact.ID == id)
            {
                if (string.IsNullOrWhiteSpace(entry.EntryPointRelativePath))
                    throw new InvalidOperationException("The selected default dashboard has no entry point.");
                string entryPoint = PathSafety.NormalizeRelativePath(entry.EntryPointRelativePath);
                FileServices.ValidateFatRelativePath(entryPoint);
                string source = PathSafety.ResolveInside(root, operation.SourcePath ?? ".");
                FileServices.EnsureFile(PathSafety.ResolveInside(source, entryPoint));
                ini.Append(CultureInfo.InvariantCulture, $"DashXex = \"{entryPoint.Replace('/', '\\')}\"\r\n");
            }

            prepared[index] = (artifact with
            {
                Operations = [operation with { DestinationPath = ExtrasRoot + "/" + id }]
            }, root);
        }

        prepared[installerIndex] = (installer with
        {
            Operations = [..installer.Operations!, new InstallOperation(
                InstallOperationKind.WriteFile, ExtrasIni, Contents: ini.ToString(), AllowedOverwritePaths: [ExtrasIni])]
        }, installerRoot);
        return prepared;
    }
}
