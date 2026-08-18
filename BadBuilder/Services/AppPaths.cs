namespace BadBuilder.Services;

internal static class AppPaths
{
    internal static string CacheRoot { get; } = GetCacheRoot();
    internal static string DownloadRoot => Path.Combine(CacheRoot, "downloads");
    internal static string LogRoot => Path.Combine(CacheRoot, "logs");
    internal static string WorkspaceRoot => Path.GetFullPath(Directory.GetCurrentDirectory());

    internal static string CreateRunRoot() =>
        Directory.CreateTempSubdirectory("BadBuilder-run-").FullName;

    internal static void DeleteRunRoot(string runRoot)
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRunRoot = Path.GetFullPath(runRoot);
        string? parent = Path.GetDirectoryName(fullRunRoot)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(parent, temporaryRoot, comparison) ||
            !Path.GetFileName(fullRunRoot).StartsWith("BadBuilder-run-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to remove an unexpected staging path.");
        }
        if (Directory.Exists(fullRunRoot))
            Directory.Delete(fullRunRoot, recursive: true);
    }

    private static string GetCacheRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.GetFullPath(Path.Combine(local, "BadBuilder", "Cache"));
        }

        string? xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgCache) && Path.IsPathFullyQualified(xdgCache))
            return Path.GetFullPath(Path.Combine(xdgCache, "badbuilder"));

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.GetFullPath(Path.Combine(home, ".cache", "badbuilder"));
    }
}
