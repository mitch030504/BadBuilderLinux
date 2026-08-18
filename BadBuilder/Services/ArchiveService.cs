using SharpCompress.Archives;

namespace BadBuilder.Services;

internal static class ArchiveService
{
    internal static async Task<string> ExtractAsync(string archivePath, string stagingRoot, CancellationToken cancellationToken)
    {
        string extractionRoot     = Path.Combine(stagingRoot, Path.GetFileName(archivePath));
        string extractionFullPath = Path.GetFullPath(extractionRoot);

        if (Directory.Exists(extractionFullPath))
            Directory.Delete(extractionFullPath, recursive: true);

        Directory.CreateDirectory(extractionFullPath);

        using IArchive archive = ArchiveFactory.OpenArchive(archivePath);

        foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relativePath = (entry.Key ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new InvalidOperationException("Archive contains an entry without a path.");

            string destinationPath = Path.GetFullPath(Path.Combine(extractionFullPath, relativePath));
            if (!destinationPath.StartsWith(extractionFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Archive contains an unsafe path: {entry.Key}");

            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            await using Stream source          = entry.OpenEntryStream();
            await using FileStream destination = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            await source.CopyToAsync(destination, cancellationToken);
        }

        return extractionFullPath;
    }

    internal static IReadOnlyList<string> FindEntryPoints(string archivePath, bool rootOnly = false)
    {
        using IArchive archive = ArchiveFactory.OpenArchive(archivePath);

        return [..archive.Entries
            .Where(entry  => !entry.IsDirectory && !string.IsNullOrWhiteSpace(entry.Key))
            .Select(entry => entry.Key!.Replace('\\', '/').TrimStart('/'))
            .Where(path   => path.EndsWith(".xex", StringComparison.OrdinalIgnoreCase) && (!rootOnly || !path.Contains('/')))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }
}