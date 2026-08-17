using System.Security.Cryptography;

namespace BadBuilder.Services;

internal static class FileServices
{
    public static string ComputeSHA256(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    public static string NormalizeUserPath(string path) => path.Trim().Trim('"', '\'');

    public static void FormatDisk()
    {

    }
}