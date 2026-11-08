namespace BadBuilder.Services;

internal static class FileSystemSpace
{
    internal static long GetAvailableBytes(string path)
    {
        string fullPath = Path.GetFullPath(path);

        if (OperatingSystem.IsLinux())
            return Disks.LinuxNative.GetAvailableBytes(fullPath);

        DriveInfo drive = DriveInfo.GetDrives()
            .Where(candidate => candidate.IsReady && PathSafety.IsWithinRoot(candidate.Name, fullPath))
            .OrderByDescending(candidate => candidate.Name.Length)
            .FirstOrDefault()
            ?? throw new IOException(
                $"Could not determine the filesystem containing {fullPath}. Refusing to continue without a verified free-space measurement.");

        return drive.AvailableFreeSpace;
    }

    internal static bool IsMountPoint(string path)
    {
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

        if (OperatingSystem.IsLinux())
            return Disks.LinuxNative.IsMountPoint(fullPath);

        return DriveInfo.GetDrives().Any(drive =>
            drive.IsReady &&
            string.Equals(
                Path.GetFullPath(drive.Name).TrimEnd(Path.DirectorySeparatorChar),
                fullPath,
                StringComparison.OrdinalIgnoreCase));
    }
}
