namespace BadBuilder.Services;

internal static class XboxPath
{
    internal static string Combine(string device, params string[] relativeParts)
    {
        if (string.IsNullOrWhiteSpace(device) || device.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("The Xbox device name is invalid.", nameof(device));

        string combined = string.Join('/', relativeParts.Where(part => !string.IsNullOrWhiteSpace(part)));
        string normalized = PathSafety.NormalizeRelativePath(combined);
        FileServices.ValidateFatRelativePath(normalized);
        return normalized == "."
            ? device + @":\"
            : device + @":\" + normalized.Replace('/', '\\');
    }
}
