using System.Security.Cryptography;
using System.Text;

namespace BadBuilder.Services.Disks;

internal sealed record VolumeInfo(
    string DevicePath,
    string Type,
    string? FileSystem,
    string? Label,
    IReadOnlyList<string> MountPoints);

internal sealed record DiskIdentity(
    string Fingerprint,
    string DevicePath,
    string Model,
    string? Serial,
    string? Wwn,
    long SizeBytes,
    string? Transport)
{
    internal static DiskIdentity Create(
        string devicePath,
        string? model,
        string? serial,
        string? wwn,
        long sizeBytes,
        string? transport,
        string? stableId = null)
    {
        string normalizedModel = Normalize(model) ?? "Unknown disk";
        string? normalizedSerial = Normalize(serial);
        string? normalizedWwn = Normalize(wwn);
        string? normalizedTransport = Normalize(transport)?.ToLowerInvariant();

        // Prefer hardware identifiers so the fingerprint survives /dev node or drive-index changes.
        // The path is a last-resort discriminator for devices which expose no serial or WWN.
        string discriminator = normalizedWwn ?? normalizedSerial ?? Normalize(stableId) ?? Path.GetFullPath(devicePath);
        string material = string.Join('\n', normalizedModel, normalizedSerial, normalizedWwn, sizeBytes, normalizedTransport, discriminator);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

        return new DiskIdentity(
            fingerprint,
            devicePath,
            normalizedModel,
            normalizedSerial,
            normalizedWwn,
            sizeBytes,
            normalizedTransport);
    }

    internal bool IsExactMatch(DiskIdentity other) =>
        string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal) &&
        string.Equals(DevicePath, other.DevicePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        string.Equals(Model, other.Model, StringComparison.Ordinal) &&
        string.Equals(Serial, other.Serial, StringComparison.Ordinal) &&
        string.Equals(Wwn, other.Wwn, StringComparison.Ordinal) &&
        SizeBytes == other.SizeBytes &&
        string.Equals(Transport, other.Transport, StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record DiskInfo(
    DiskIdentity Identity,
    bool IsRemovable,
    bool IsHotPlug,
    bool IsReadOnly,
    IReadOnlyList<VolumeInfo> Volumes)
{
    internal string ID => Identity.Fingerprint;
    internal string Name => Identity.Model;
    internal long Size => Identity.SizeBytes;
    internal DriveType Type => IsRemovable || IsHotPlug ? DriveType.Removable : DriveType.Fixed;
    internal string DevicePath => Identity.DevicePath;
    internal string? Serial => Identity.Serial;
    internal string? Wwn => Identity.Wwn;
    internal string? Transport => Identity.Transport;

    public override string ToString() => $"{Name} ({DevicePath}, {Size} bytes)";
}

internal sealed record PreparedTarget(
    string MountRoot,
    DiskIdentity Identity,
    string Backend,
    string CleanupToken,
    bool RequiresFinalize);

internal sealed record FinalizeResult(bool Success, bool StillMounted = false, string? Error = null);

internal interface IDiskBackend
{
    Task<IReadOnlyList<DiskInfo>> EnumerateAsync(CancellationToken cancellationToken);
    Task<DiskInfo> RevalidateAsync(DiskIdentity selected, CancellationToken cancellationToken);
    Task<PreparedTarget> PrepareAsync(DiskIdentity selected, CancellationToken cancellationToken);
    Task<FinalizeResult> FinalizeAsync(PreparedTarget target, CancellationToken cancellationToken);
}

internal sealed class DiskSafetyException(string message) : IOException(message);

internal static class DiskService
{
    private static readonly Lazy<IDiskBackend> PlatformBackend = new(CreatePlatformBackend);

    internal static Task<IReadOnlyList<DiskInfo>> EnumerateDisksAsync(CancellationToken cancellationToken) =>
        PlatformBackend.Value.EnumerateAsync(cancellationToken);

    internal static Task<DiskInfo> RevalidateAsync(DiskIdentity selected, CancellationToken cancellationToken) =>
        PlatformBackend.Value.RevalidateAsync(selected, cancellationToken);

    internal static Task<PreparedTarget> PrepareAsync(DiskIdentity selected, CancellationToken cancellationToken) =>
        PlatformBackend.Value.PrepareAsync(selected, cancellationToken);

    internal static Task<FinalizeResult> FinalizeAsync(PreparedTarget target, CancellationToken cancellationToken) =>
        PlatformBackend.Value.FinalizeAsync(target, cancellationToken);

    private static IDiskBackend CreatePlatformBackend()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsDiskBackend();
        if (OperatingSystem.IsLinux())
            return new LinuxDiskBackend();

        throw new PlatformNotSupportedException(
            $"BadBuilder supports Windows and Linux only. This platform is {Environment.OSVersion.Platform}.");
    }
}
