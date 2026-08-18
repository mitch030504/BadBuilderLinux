namespace BadBuilder.Services;

internal static class PathSafety
{
    internal static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("A relative path is empty.");

        string normalized = path.Replace('\\', '/');
        if (normalized[0] == '/' ||
            (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':'))
        {
            throw new InvalidDataException($"A rooted path is not allowed: {path}");
        }

        List<string> components = [];
        foreach (string component in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
                continue;
            if (component == "..")
                throw new InvalidDataException($"A path attempts to leave its root: {path}");
            components.Add(component);
        }

        return components.Count == 0 ? "." : string.Join('/', components);
    }

    internal static string ResolveInside(string root, string relativePath, bool? caseSensitive = null)
    {
        string fullRoot = Path.GetFullPath(root);
        string normalized = NormalizeRelativePath(relativePath);
        string candidate = normalized == "."
            ? fullRoot
            : Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsWithinRoot(fullRoot, candidate, caseSensitive))
            throw new InvalidDataException($"A path escapes its assigned root: {relativePath}");
        return candidate;
    }

    internal static bool IsWithinRoot(string root, string candidate, bool? caseSensitive = null)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullCandidate = Path.GetFullPath(candidate);
        StringComparison comparison = (caseSensitive ?? !OperatingSystem.IsWindows())
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        return string.Equals(fullRoot, fullCandidate, comparison) ||
            fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }
}
