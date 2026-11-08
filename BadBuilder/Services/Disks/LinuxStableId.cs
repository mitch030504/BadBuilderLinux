namespace BadBuilder.Services.Disks;

internal static class LinuxStableId
{
    private const string ByIdRoot = "/dev/disk/by-id";

    /// <summary>
    /// Returns a stable name for the device from /dev/disk/by-id. The link name
    /// encodes the serial, or the port for devices that report no serial, so it
    /// survives /dev node renumbering.
    /// </summary>
    internal static string? TryGet(string devicePath)
    {
        try
        {
            if (!Directory.Exists(ByIdRoot))
                return null;

            string deviceName = Path.GetFileName(devicePath);
            return Directory.EnumerateFiles(ByIdRoot)
                .Select(path => (Name: Path.GetFileName(path), Target: TryReadLink(path)))
                .Where(entry => entry.Target is not null &&
                                string.Equals(Path.GetFileName(entry.Target), deviceName, StringComparison.Ordinal))
                .Select(entry => entry.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryReadLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
