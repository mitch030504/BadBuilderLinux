namespace BadBuilder.Services;

internal static class XboxPath
{
    private const int MaxFatxComponentLength = 42;
    private const int MaxFatxPathLength = 240;

    internal static string Combine(string device, params string[] relativeParts)
    {
        ValidateDevice(device);

        string combined = string.Join('/', relativeParts.Where(part => !string.IsNullOrWhiteSpace(part)));
        string normalized = PathSafety.NormalizeRelativePath(combined);
        FileServices.ValidateFatRelativePath(normalized);
        return ToCompletePath(device, normalized);
    }

    internal static string ValidateFatxPath(string device, string relativePath)
    {
        ValidateDevice(device);

        string normalized = PathSafety.NormalizeRelativePath(relativePath);
        FileServices.ValidateFatRelativePath(normalized);

        foreach (string component in normalized.Split('/'))
        {
            if (component != "." && component.Length > MaxFatxComponentLength)
            {
                throw new InvalidDataException(
                    $"A FATX path component cannot be longer than {MaxFatxComponentLength} characters: {relativePath}");
            }
        }

        string complete = ToCompletePath(device, normalized);
        if (complete.Length > MaxFatxPathLength)
        {
            throw new InvalidDataException(
                $"A FATX path cannot be longer than {MaxFatxPathLength} characters: {complete}");
        }

        return complete;
    }

    private static void ValidateDevice(string device)
    {
        if (string.IsNullOrWhiteSpace(device) || device.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("The Xbox device name is invalid.", nameof(device));
    }

    private static string ToCompletePath(string device, string normalizedRelativePath) =>
        normalizedRelativePath == "."
            ? device + @":\"
            : device + @":\" + normalizedRelativePath.Replace('/', '\\');
}
