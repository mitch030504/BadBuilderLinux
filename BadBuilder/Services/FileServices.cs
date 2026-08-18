using System.Security.Cryptography;

namespace BadBuilder.Services;

internal static class FileServices
{
    private static readonly HashSet<string> ReservedFatNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    internal static string ComputeSHA256(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        byte[] hash             = SHA256.HashData(stream);

        return Convert.ToHexString(hash);
    }

    internal static string NormalizeUserPath(string path) => path.Trim().Trim('"', '\'');

    internal static void EnsureFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Staged file not found: {path}", path);
    }

    internal static string ValidateIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character =>
            !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new InvalidDataException($"Invalid internal identifier: {value}");
        }
        return value;
    }

    internal static string SanitizeFatName(string value)
    {
        string sanitized = new(value.Trim().Select(character =>
            character < 32 || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '_'
                : character).ToArray());
        sanitized = sanitized.TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new InvalidDataException("The name contains no FAT-compatible characters.");
        if (ReservedFatNames.Contains(Path.GetFileNameWithoutExtension(sanitized)))
            sanitized = "_" + sanitized;
        return sanitized.Length <= 120 ? sanitized : sanitized[..120].TrimEnd(' ', '.');
    }

    internal static void ValidateFatRelativePath(string relativePath)
    {
        foreach (string component in PathSafety.NormalizeRelativePath(relativePath).Split('/'))
        {
            if (component == ".")
                continue;
            if (component.Length > 255 || component.EndsWith(' ') || component.EndsWith('.') ||
                component.Any(character => character < 32 || character is '<' or '>' or ':' or '"' or '|' or '?' or '*') ||
                ReservedFatNames.Contains(Path.GetFileNameWithoutExtension(component)))
            {
                throw new InvalidDataException($"Path is not valid on FAT32: {relativePath}");
            }
        }
    }
}
