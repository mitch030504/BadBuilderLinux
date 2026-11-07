using System.Text.Json;
using BadBuilder.Services.Disks;

namespace BadBuilder.Services;

internal static class PendingCleanupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string DefaultPath = Path.Combine(AppPaths.CacheRoot, "pending-cleanup.json");

    internal static void Save(PreparedTarget target, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        string targetPath = Path.GetFullPath(path ?? DefaultPath);
        string? directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("The pending cleanup record needs a directory.");

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            PendingCleanupRecord record = PendingCleanupRecord.From(target);
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                // A leftover temporary file must not mask the original save failure.
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static PreparedTarget? TryLoad(string? path = null)
    {
        string targetPath = path ?? DefaultPath;

        try
        {
            if (!File.Exists(targetPath))
                return null;

            using FileStream stream = new(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            PendingCleanupRecord? record = JsonSerializer.Deserialize<PendingCleanupRecord>(stream, JsonOptions);
            if (record is null ||
                !IsHex(record.Fingerprint, 64) ||
                !IsHex(record.CleanupToken, 32) ||
                string.IsNullOrWhiteSpace(record.DevicePath) ||
                string.IsNullOrWhiteSpace(record.MountRoot))
            {
                return null;
            }

            DiskIdentity identity = new(
                record.Fingerprint!,
                record.DevicePath!,
                string.IsNullOrWhiteSpace(record.Model) ? "Unknown disk" : record.Model,
                record.Serial,
                record.Wwn,
                record.SizeBytes,
                record.Transport);

            return new PreparedTarget(
                record.MountRoot!,
                identity,
                record.Backend ?? string.Empty,
                record.CleanupToken!,
                record.RequiresFinalize);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Recovery must treat an unreadable or corrupt record as absent rather than fail startup.
            return null;
        }
    }

    internal static void Clear(string? path = null)
    {
        string targetPath = path ?? DefaultPath;
        if (File.Exists(targetPath))
            File.Delete(targetPath);
    }

    internal static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    private static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(char.IsAsciiHexDigit);

    private sealed class PendingCleanupRecord
    {
        public string? DevicePath { get; set; }
        public string? Fingerprint { get; set; }
        public string? Model { get; set; }
        public string? Serial { get; set; }
        public string? Wwn { get; set; }
        public long SizeBytes { get; set; }
        public string? Transport { get; set; }
        public string? MountRoot { get; set; }
        public string? CleanupToken { get; set; }
        public string? Backend { get; set; }
        public bool RequiresFinalize { get; set; } = true;

        internal static PendingCleanupRecord From(PreparedTarget target) => new()
        {
            DevicePath = target.Identity.DevicePath,
            Fingerprint = target.Identity.Fingerprint,
            Model = target.Identity.Model,
            Serial = target.Identity.Serial,
            Wwn = target.Identity.Wwn,
            SizeBytes = target.Identity.SizeBytes,
            Transport = target.Identity.Transport,
            MountRoot = target.MountRoot,
            CleanupToken = target.CleanupToken,
            Backend = target.Backend,
            RequiresFinalize = target.RequiresFinalize,
        };
    }
}
