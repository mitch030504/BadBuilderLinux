using System.Security.Cryptography;

namespace BadBuilder.Services;

internal static class FileServices
{
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
}