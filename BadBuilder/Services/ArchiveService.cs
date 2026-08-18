using SharpCompress.Archives;
using SharpCompress.Common;
using BadBuilder.Configuration;

namespace BadBuilder.Services;

internal sealed record ArchiveExtractionLimits(
    int MaxEntries,
    long MaxExpandedBytes,
    long TargetCapacityBytes,
    long StagingAvailableBytes)
{
    internal static ArchiveExtractionLimits CreateDefault(string stagingRoot, long targetCapacityBytes)
    {
        Directory.CreateDirectory(stagingRoot);
        long available = FileSystemSpace.GetAvailableBytes(stagingRoot);
        long reserved = Math.Min(64L * 1024 * 1024, available);
        return new ArchiveExtractionLimits(
            MaxEntries: 100_000,
            MaxExpandedBytes: 16L * 1024 * 1024 * 1024,
            TargetCapacityBytes: targetCapacityBytes,
            StagingAvailableBytes: available - reserved);
    }

    internal long EffectiveByteLimit => new[] { MaxExpandedBytes, TargetCapacityBytes, StagingAvailableBytes }.Min();
}

internal static class ArchiveService
{
    internal static async Task<string> ExtractAsync(
        string artifactId,
        string archivePath,
        string stagingRoot,
        ArchiveExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        FileServices.EnsureFile(archivePath);
        string extractionRoot = PathSafety.ResolveInside(stagingRoot, FileServices.ValidateIdentifier(artifactId));
        if (Directory.Exists(extractionRoot))
            throw new IOException($"The run-specific staging directory already exists for {artifactId}.");
        Directory.CreateDirectory(extractionRoot);

        try
        {
            using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
            ArchiveEntryPlan[] entries = InspectEntries(archive, limits);
            long actualExpanded = 0;

            foreach (ArchiveEntryPlan planned in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destinationPath = PathSafety.ResolveInside(extractionRoot, planned.RelativePath);
                if (planned.IsDirectory)
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                string? destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                await using FileStream destination = new(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous);
                using BoundedWriteStream boundedDestination = new(
                    destination,
                    checked(limits.EffectiveByteLimit - actualExpanded),
                    cancellationToken);
                planned.Entry.WriteTo(boundedDestination, new ExtractionOptions { CheckCrc = true });
                if (boundedDestination.BytesWritten != planned.Entry.Size)
                    throw new InvalidDataException($"The expanded size of '{planned.RelativePath}' does not match its archive metadata.");
                actualExpanded = checked(actualExpanded + boundedDestination.BytesWritten);
                await destination.FlushAsync(cancellationToken);
            }

            return extractionRoot;
        }
        catch
        {
            if (Directory.Exists(extractionRoot))
                Directory.Delete(extractionRoot, recursive: true);
            throw;
        }
    }

    internal static void ValidateLayout(ArtifactDefinition artifact, string stagingPath)
    {
        ArchiveLayout? layout = artifact.Layout;
        if (layout is null)
            return;

        foreach (string required in layout.RequiredPaths ?? [])
        {
            string path = PathSafety.ResolveInside(stagingPath, required);
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new InvalidDataException($"{artifact.DisplayName} is missing required archive path '{required}'.");
        }

        if (layout.RequireSingleTopLevelDirectory)
        {
            string[] directories = Directory.GetDirectories(stagingPath);
            if (directories.Length != 1)
                throw new InvalidDataException($"{artifact.DisplayName} must contain exactly one top-level directory.");
        }
    }

    internal static IReadOnlyList<string> FindEntryPoints(string archivePath, bool rootOnly = false)
    {
        FileServices.EnsureFile(archivePath);
        using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
        ArchiveExtractionLimits scanLimits = new(100_000, long.MaxValue, long.MaxValue, long.MaxValue);

        return [..InspectEntries(archive, scanLimits)
            .Where(entry => !entry.IsDirectory)
            .Select(entry => entry.RelativePath)
            .Where(path => path.EndsWith(".xex", StringComparison.OrdinalIgnoreCase) &&
                           (!rootOnly || !path.Contains('/', StringComparison.Ordinal)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static ArchiveEntryPlan[] InspectEntries(IArchive archive, ArchiveExtractionLimits limits)
    {
        List<ArchiveEntryPlan> result = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> filePaths = new(StringComparer.OrdinalIgnoreCase);
        long declaredBytes = 0;
        int entryCount = 0;

        foreach (IArchiveEntry entry in archive.Entries)
        {
            if (++entryCount > limits.MaxEntries)
                throw new InvalidDataException($"The archive contains more than {limits.MaxEntries} entries.");

            string key = entry.Key ?? throw new InvalidDataException("The archive contains an entry without a path.");
            string relative = PathSafety.NormalizeRelativePath(key);
            if (relative == ".")
                continue;

            FileServices.ValidateFatRelativePath(relative);
            if (!paths.Add(relative))
                throw new InvalidDataException($"The archive contains duplicate or case-colliding paths: {relative}");
            if (!string.IsNullOrWhiteSpace(entry.LinkTarget))
                throw new InvalidDataException($"Archive links are not allowed: {relative}");

            foreach (string parent in GetParents(relative))
            {
                if (filePaths.Contains(parent))
                    throw new InvalidDataException($"An archive file collides with a parent directory: {parent}");
            }

            if (!entry.IsDirectory)
            {
                filePaths.Add(relative);
                if (entry.Size < 0)
                    throw new InvalidDataException($"The archive reports an invalid size for {relative}.");
                declaredBytes = checked(declaredBytes + entry.Size);
                if (declaredBytes > limits.EffectiveByteLimit)
                    throw new InvalidDataException("The archive declares more expanded data than the configured or available capacity limit.");
            }

            result.Add(new ArchiveEntryPlan(entry, relative, entry.IsDirectory));
        }

        // Repeat the parent check after the complete inventory so entry ordering cannot hide a
        // child-before-parent file/directory collision.
        foreach (string path in paths)
        {
            foreach (string parent in GetParents(path))
            {
                if (filePaths.Contains(parent))
                    throw new InvalidDataException($"An archive file collides with a parent directory: {parent}");
            }
        }

        return [..result];
    }

    private static IEnumerable<string> GetParents(string path)
    {
        int index = path.LastIndexOf('/');
        while (index > 0)
        {
            yield return path[..index];
            index = path.LastIndexOf('/', index - 1);
        }
    }

    private sealed record ArchiveEntryPlan(IArchiveEntry Entry, string RelativePath, bool IsDirectory);

    private sealed class BoundedWriteStream(Stream inner, long maximumBytes, CancellationToken cancellationToken) : Stream
    {
        internal long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWithinLimit(count);
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWithinLimit(buffer.Length);
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken writeCancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writeCancellationToken.ThrowIfCancellationRequested();
            EnsureWithinLimit(buffer.Length);
            return WriteAndCountAsync(buffer, writeCancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private async ValueTask WriteAndCountAsync(ReadOnlyMemory<byte> buffer, CancellationToken writeCancellationToken)
        {
            await inner.WriteAsync(buffer, writeCancellationToken);
            BytesWritten += buffer.Length;
        }

        private void EnsureWithinLimit(int count)
        {
            if (count < 0 || checked(BytesWritten + count) > maximumBytes)
                throw new InvalidDataException("The archive expanded beyond the configured or available capacity limit.");
        }
    }
}
