using System.Globalization;

namespace BadBuilder.Services.Disks;

internal static class LinuxProtectedPaths
{
    // Any disk backing a mount at (or above) one of these paths must never be
    // offered as a target. "/" alone protects the root disk; the rest protect
    // separate system volumes such as /srv, /var or /efi.
    private static readonly string[] SystemRoots =
    [
        "/", "/boot", "/boot/efi", "/efi", "/home", "/root", "/srv", "/var",
        "/usr", "/opt", "/etc", "/tmp", "/run", "/snap", "/nix", "/lib", "/lib64",
    ];

    internal static IReadOnlyList<string> ForApplication() =>
        Resolve([
            AppPaths.WorkspaceRoot,
            AppPaths.CacheRoot,
            AppContext.BaseDirectory,
            AppPaths.HomeRoot,
            ..SystemRoots,
            ..AllHomeDirectories(),
        ]);

    internal static IReadOnlyList<string> ForHelper(
        uint uid,
        string? workspaceRoot = null,
        string? cacheRoot = null) =>
        Resolve([
            workspaceRoot ?? AppPaths.WorkspaceRoot,
            cacheRoot ?? AppPaths.CacheRoot,
            AppContext.BaseDirectory,
            LinuxUserHome.Resolve(uid),
            ..SystemRoots,
            ..AllHomeDirectories(),
        ]);

    internal static IReadOnlyList<string> AllHomeDirectories()
    {
        try
        {
            return File.Exists("/etc/passwd")
                ? LinuxUserHome.ParsePasswdHomes(File.ReadAllText("/etc/passwd"))
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<string> Resolve(IEnumerable<string> paths)
    {
        List<string> resolved = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string? realPath = TryResolveRealPath(path);
            if (realPath is not null && seen.Add(realPath))
                resolved.Add(realPath);
        }
        return resolved;
    }

    internal static string? TryResolveRealPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        try
        {
            string root = Path.GetPathRoot(fullPath) ?? Path.DirectorySeparatorChar.ToString();
            string current = root;
            foreach (string component in fullPath[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = ResolveComponent(Path.Combine(current, component));
            }
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return fullPath;
        }
    }

    private static string ResolveComponent(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? path : Path.GetFullPath(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }
}

internal static class LinuxUserHome
{
    internal static string Resolve(uint uid)
    {
        string? home = TryResolve(uid);
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
        {
            throw new DiskSafetyException(
                $"The home directory for UID {uid.ToString(CultureInfo.InvariantCulture)} could not be resolved.");
        }
        return Path.GetFullPath(home);
    }

    internal static string? TryResolve(uint uid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                string? home = LinuxNative.TryGetHomeDirectory(uid);
                if (!string.IsNullOrWhiteSpace(home))
                    return home;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Fall through to the passwd-file lookup below.
            }
        }

        return TryReadPasswdHome(uid);
    }

    internal static string? TryReadPasswdHome(uint uid)
    {
        try
        {
            return File.Exists("/etc/passwd") ? ParsePasswdHome(File.ReadAllText("/etc/passwd"), uid) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? ParsePasswdHome(string passwd, uint uid)
    {
        foreach (string line in passwd.Split('\n'))
        {
            string entry = line.TrimEnd('\r');
            if (entry.Length == 0 || entry[0] == '#')
                continue;

            string[] fields = entry.Split(':');
            if (fields.Length < 7)
                continue;
            if (uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint entryUid) &&
                entryUid == uid)
            {
                return string.IsNullOrWhiteSpace(fields[5]) ? null : fields[5];
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> ParsePasswdHomes(string passwd)
    {
        List<string> homes = [];
        foreach (string line in passwd.Split('\n'))
        {
            string entry = line.TrimEnd('\r');
            if (entry.Length == 0 || entry[0] == '#')
                continue;

            string[] fields = entry.Split(':');
            if (fields.Length >= 7 &&
                !string.IsNullOrWhiteSpace(fields[5]) &&
                fields[5].StartsWith('/'))
            {
                homes.Add(fields[5]);
            }
        }

        return homes;
    }
}
