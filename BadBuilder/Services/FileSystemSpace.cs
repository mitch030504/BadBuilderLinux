namespace BadBuilder.Services;

internal static class FileSystemSpace
{
    internal static long GetAvailableBytes(string path)
    {
        string fullPath = Path.GetFullPath(path);
        DriveInfo? drive = DriveInfo.GetDrives()
            .Where(candidate => candidate.IsReady && PathSafety.IsWithinRoot(candidate.Name, fullPath))
            .OrderByDescending(candidate => candidate.Name.Length)
            .FirstOrDefault();

        if (drive is not null)
            return drive.AvailableFreeSpace;

        string root = Path.GetPathRoot(fullPath)
            ?? throw new IOException($"Could not determine the filesystem containing {fullPath}.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
