using System.Text;
using BadBuilder.Configuration;

namespace BadBuilder.Services;

internal enum PlannedActionKind
{
    CreateDirectory,
    CopyFile,
    WriteFile,
    MoveFile,
}

internal sealed record PlannedInstallAction(
    PlannedActionKind Kind,
    string DestinationRelativePath,
    string? SourcePath = null,
    byte[]? Contents = null,
    long Size = 0);

internal sealed record InstallPlan(IReadOnlyList<PlannedInstallAction> Actions, long RequiredBytes);

internal static class InstallService
{
    internal static InstallPlan BuildPlan(
        IReadOnlyList<(ArtifactDefinition Artifact, string StagingPath)> artifacts,
        IReadOnlyList<InstallOperation> extraOperations,
        long targetCapacityBytes)
    {
        List<PlannedInstallAction> actions = [];
        Dictionary<string, long> destinationFiles = new(StringComparer.OrdinalIgnoreCase);

        foreach ((ArtifactDefinition artifact, string stagingPath) in artifacts)
        {
            ArchiveService.ValidateLayout(artifact, stagingPath);
            if (artifact.Operations is null || artifact.Operations.Count == 0)
                throw new InvalidDataException($"{artifact.DisplayName} has no installation operations.");

            foreach (InstallOperation operation in artifact.Operations)
                PlanOperation(operation, stagingPath, actions, destinationFiles, allowOverwrite: false);
        }

        foreach (InstallOperation operation in extraOperations)
            PlanOperation(operation, string.Empty, actions, destinationFiles, allowOverwrite: true);

        ValidateDestinationShape(actions);

        long required = destinationFiles.Values.Aggregate(0L, checked((total, size) => total + size));
        long overhead = Math.Max(16L * 1024 * 1024, required / 20);
        if (checked(required + overhead) > targetCapacityBytes)
        {
            throw new IOException(
                $"The selected artifacts require at least {required + overhead} bytes, but the target disk has {targetCapacityBytes} bytes.");
        }

        return new InstallPlan(actions, required + overhead);
    }

    internal static async Task ExecuteAsync(
        InstallPlan plan,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(targetRoot))
            throw new DirectoryNotFoundException($"The prepared target mount does not exist: {targetRoot}");

        long available = FileSystemSpace.GetAvailableBytes(targetRoot);
        if (available < plan.RequiredBytes)
            throw new IOException($"The prepared target has {available} free bytes, but {plan.RequiredBytes} bytes are required.");

        foreach (PlannedInstallAction action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = PathSafety.ResolveInside(targetRoot, action.DestinationRelativePath);

            switch (action.Kind)
            {
                case PlannedActionKind.CreateDirectory:
                    Directory.CreateDirectory(destination);
                    break;

                case PlannedActionKind.CopyFile:
                    {
                        string source = action.SourcePath
                            ?? throw new InvalidOperationException("A planned file copy has no source.");
                        FileServices.EnsureFile(source);
                        if (new FileInfo(source).Length != action.Size)
                            throw new IOException($"A staged source changed after preflight: {source}");
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                        await input.CopyToAsync(output, cancellationToken);
                        break;
                    }

                case PlannedActionKind.WriteFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await File.WriteAllBytesAsync(destination, action.Contents ?? [], cancellationToken);
                    break;

                case PlannedActionKind.MoveFile:
                    {
                        string sourceRelative = action.SourcePath
                            ?? throw new InvalidOperationException("A planned move has no source.");
                        string source = PathSafety.ResolveInside(targetRoot, sourceRelative);
                        FileServices.EnsureFile(source);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Move(source, destination, overwrite: true);
                        break;
                    }

                default:
                    throw new InvalidOperationException($"Unsupported planned action: {action.Kind}.");
            }
        }
    }

    private static void PlanOperation(
        InstallOperation operation,
        string stagingPath,
        List<PlannedInstallAction> actions,
        Dictionary<string, long> destinationFiles,
        bool allowOverwrite)
    {
        string destination = NormalizeTargetPath(operation.DestinationPath);
        HashSet<string> allowedOverwritePaths = new(
            (operation.AllowedOverwritePaths ?? []).Select(NormalizeTargetPath),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> matchedOverwritePaths = new(StringComparer.OrdinalIgnoreCase);

        bool AddPlannedDestination(string path, long size)
        {
            bool explicitlyAllowed = allowedOverwritePaths.Contains(path);
            bool collided = AddDestination(
                destinationFiles,
                path,
                size,
                allowOverwrite || explicitlyAllowed);
            if (collided && explicitlyAllowed)
                matchedOverwritePaths.Add(path);
            return collided;
        }

        switch (operation.Kind)
        {
            case InstallOperationKind.CopyDirectory:
                {
                    string source = ResolveStagedSource(stagingPath, operation);
                    if (!Directory.Exists(source))
                        throw new DirectoryNotFoundException($"Expected staged directory was not found: {source}");

                    actions.Add(new PlannedInstallAction(PlannedActionKind.CreateDirectory, destination));
                    foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(source, directory).Replace(Path.DirectorySeparatorChar, '/');
                        actions.Add(new PlannedInstallAction(
                            PlannedActionKind.CreateDirectory,
                            CombineTarget(destination, relative)));
                    }

                    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
                        string target = CombineTarget(destination, relative);
                        long length = new FileInfo(file).Length;
                        AddPlannedDestination(target, length);
                        actions.Add(new PlannedInstallAction(PlannedActionKind.CopyFile, target, file, Size: length));
                    }
                    break;
                }

            case InstallOperationKind.CopyFile:
                {
                    string source = ResolveStagedSource(stagingPath, operation);
                    FileServices.EnsureFile(source);
                    long length = new FileInfo(source).Length;
                    AddPlannedDestination(destination, length);
                    actions.Add(new PlannedInstallAction(PlannedActionKind.CopyFile, destination, source, Size: length));
                    break;
                }

            case InstallOperationKind.WriteFile:
                {
                    byte[] contents = Encoding.UTF8.GetBytes(operation.Contents ?? string.Empty);
                    AddPlannedDestination(destination, contents.LongLength);
                    actions.Add(new PlannedInstallAction(PlannedActionKind.WriteFile, destination, Contents: contents, Size: contents.LongLength));
                    break;
                }

            case InstallOperationKind.RenameFile:
                {
                    string source = NormalizeTargetPath(operation.SourcePath
                        ?? throw new InvalidOperationException("RenameFile requires a source path."));
                    if (!destinationFiles.TryGetValue(source, out long length))
                        throw new InvalidDataException($"A rename source is not produced by the preflight plan: {source}");
                    destinationFiles.Remove(source);
                    AddPlannedDestination(destination, length);
                    actions.Add(new PlannedInstallAction(PlannedActionKind.MoveFile, destination, source, Size: length));
                    break;
                }

            default:
                throw new InvalidOperationException($"Unsupported install operation: {operation.Kind}.");
        }

        string[] unmatched = [..allowedOverwritePaths.Except(matchedOverwritePaths, StringComparer.OrdinalIgnoreCase)];
        if (unmatched.Length > 0)
        {
            throw new InvalidDataException(
                $"The expected installation overwrite did not occur: {string.Join(", ", unmatched)}");
        }
    }

    private static string ResolveStagedSource(string stagingPath, InstallOperation operation)
    {
        if (string.IsNullOrWhiteSpace(stagingPath))
            throw new InvalidOperationException($"{operation.Kind} requires a staging root.");
        string relative = operation.SourcePath
            ?? throw new InvalidOperationException($"{operation.Kind} requires a source path.");

        string current = Path.GetFullPath(stagingPath);
        foreach (string component in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
                continue;
            if (component == "..")
                throw new InvalidDataException($"A staged source escapes its root: {relative}");
            if (component.Equals("<SUBFOLDER>", StringComparison.OrdinalIgnoreCase))
            {
                string[] directories = Directory.GetDirectories(current);
                if (directories.Length != 1)
                    throw new InvalidDataException($"Expected exactly one subfolder inside {current}.");
                current = directories[0];
            }
            else
            {
                current = Path.Combine(current, component);
            }
        }

        string result = Path.GetFullPath(current);
        if (!PathSafety.IsWithinRoot(stagingPath, result))
            throw new InvalidDataException($"A staged source escapes its root: {relative}");
        return result;
    }

    private static string NormalizeTargetPath(string path)
    {
        string normalized = PathSafety.NormalizeRelativePath(path);
        FileServices.ValidateFatRelativePath(normalized);
        return normalized;
    }

    private static string CombineTarget(string root, string relative) =>
        root == "." ? NormalizeTargetPath(relative) : NormalizeTargetPath($"{root}/{relative}");

    private static bool AddDestination(
        Dictionary<string, long> destinations,
        string path,
        long size,
        bool allowOverwrite)
    {
        bool collision = destinations.ContainsKey(path);
        if (!allowOverwrite && collision)
            throw new InvalidDataException($"Multiple artifacts would write the same FAT path: {path}");
        destinations[path] = size;
        return collision;
    }

    private static void ValidateDestinationShape(IReadOnlyList<PlannedInstallAction> actions)
    {
        HashSet<string> directories = new(actions
            .Where(action => action.Kind == PlannedActionKind.CreateDirectory)
            .Select(action => action.DestinationRelativePath), StringComparer.OrdinalIgnoreCase);
        HashSet<string> files = new(actions
            .Where(action => action.Kind is PlannedActionKind.CopyFile or PlannedActionKind.WriteFile or PlannedActionKind.MoveFile)
            .Select(action => action.DestinationRelativePath), StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            if (directories.Contains(file))
                throw new InvalidDataException($"The installation plan treats the same FAT path as both a file and directory: {file}");
            if (files.Any(other =>
                !string.Equals(file, other, StringComparison.OrdinalIgnoreCase) &&
                other.StartsWith(file.TrimEnd('/') + '/', StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"The installation plan places another file beneath file path: {file}");
            }
        }
    }
}
