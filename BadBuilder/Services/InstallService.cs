using BadBuilder.Configuration;

namespace BadBuilder.Services;

internal static class InstallService
{
    internal static async Task ExecuteAsync(
        IReadOnlyList<(ArtifactDefinition Artifact, string StagingPath)> artifacts,
        IReadOnlyList<InstallOperation> extraOperations,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        foreach (var (artifact, stagingPath) in artifacts)
        {
            if (artifact.Operations is null) continue;

            foreach (var operation in artifact.Operations)
                await ExecuteOperationAsync(operation, stagingPath, targetRoot, cancellationToken);
        }

        foreach (var operation in extraOperations)
            await ExecuteOperationAsync(operation, string.Empty, targetRoot, cancellationToken);
    }

    private static async Task ExecuteOperationAsync(InstallOperation operation, string stagingPath, string targetRoot, CancellationToken cancellationToken)
    {
        string destination = ResolveInside(targetRoot, operation.DestinationPath);

        switch (operation.Kind)
        {
            case InstallOperationKind.CopyDirectory:
                await CopyDirectoryAsync(ResolveSource(stagingPath, operation), destination, cancellationToken);
                break;
            case InstallOperationKind.CopyFile:
                {
                    string source = ResolveSource(stagingPath, operation);
                    FileServices.EnsureFile(source);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: true);
                    break;
                }
            case InstallOperationKind.WriteFile:
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllTextAsync(destination, operation.Contents ?? string.Empty, cancellationToken);
                break;
            case InstallOperationKind.RenameFile:
                {
                    string source = ResolveInside(targetRoot, operation.SourcePath
                        ?? throw new InvalidOperationException($"{operation.Kind} requires a source path."));

                    FileServices.EnsureFile(source);
                    File.Move(source, destination, overwrite: true);
                    break;
                }
            default:
                throw new InvalidOperationException($"Unsupported install operation: {operation.Kind}.");
        }
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Staged directory not found: {source}");

        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destinationFile = ResolveInside(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: true);
        }

        await Task.CompletedTask;
    }


    private static string ResolveSource(string stagingPath, InstallOperation operation) => operation.SourcePath is null
        ? throw new InvalidOperationException($"{operation.Kind} requires a source path.")
        : ResolveInside(stagingPath, operation.SourcePath);

    private static string ResolveInside(string root, string relativePath)
    {
        string fullRoot = Path.GetFullPath(root);
        string current  = fullRoot;

        foreach (string component in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
                continue;

            if (component.Equals("<SUBFOLDER>", StringComparison.OrdinalIgnoreCase))
            {
                current = Directory
                    .GetDirectories(current)
                    .FirstOrDefault() ?? throw new InvalidOperationException($"Could not resolve <SUBFOLDER> inside: {current}");
            }
            else
                current = Path.Combine(current, component);
        }

        string fullPath = Path.GetFullPath(current);

        bool isInside =
            fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(
                fullRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                )
                + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            );

        if (!isInside)
        {
            throw new InvalidOperationException(
                $"Install path escapes its root: {relativePath}.");
        }

        return fullPath;
    }
}