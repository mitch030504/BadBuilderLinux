using System.Management;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace BadBuilder.Services.Disks;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDiskBackend : IDiskBackend
{
    public Task<IReadOnlyList<DiskInfo>> EnumerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<int> systemDisks = GetSystemDiskIndices();
        Dictionary<int, ModernDiskFlags> modernFlags = GetModernDiskFlags();
        systemDisks.UnionWith(modernFlags.Where(pair => pair.Value.IsSystem || pair.Value.IsBoot).Select(pair => pair.Key));
        List<DiskInfo> disks = [];

        using ManagementObjectSearcher searcher = new("SELECT * FROM Win32_DiskDrive");
        using ManagementObjectCollection results = searcher.Get();

        foreach (ManagementObject drive in results)
        {
            using (drive)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (drive["Index"] is null)
                {
                    // Convert.ToInt32(null) returns 0, which would silently map an
                    // unknown disk onto PhysicalDrive0.
                    continue;
                }

                int index = Convert.ToInt32(drive["Index"], CultureInfo.InvariantCulture);
                if (systemDisks.Contains(index))
                    continue;

                string deviceId = Convert.ToString(drive["DeviceID"], CultureInfo.InvariantCulture) ?? $@"\\.\PhysicalDrive{index}";
                string model = Convert.ToString(drive["Model"], CultureInfo.InvariantCulture)?.Trim() ?? $"Disk {index}";
                string? serial = Convert.ToString(drive["SerialNumber"], CultureInfo.InvariantCulture)?.Trim();
                ModernDiskFlags? flags = modernFlags.GetValueOrDefault(index);
                string? wwn = null;
                string? stableId = flags?.UniqueId;
                if (string.IsNullOrWhiteSpace(stableId))
                    stableId = Convert.ToString(drive["PNPDeviceID"], CultureInfo.InvariantCulture)?.Trim();
                long size = drive["Size"] is null ? 0 : Convert.ToInt64(drive["Size"], CultureInfo.InvariantCulture);
                string transport = Convert.ToString(drive["InterfaceType"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                string mediaType = Convert.ToString(drive["MediaType"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

                bool removable =
                    transport.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase);

                // Fixed SATA/NVMe disks are intentionally never offered, even if manually selected elsewhere.
                bool readOnly = flags?.IsReadOnly ?? false;
                bool offline = flags?.IsOffline ?? false;
                if (!removable || readOnly || offline || !DiskFormatter.IsSupportedDiskSize(size))
                    continue;

                if (string.IsNullOrWhiteSpace(serial) &&
                    string.IsNullOrWhiteSpace(wwn) &&
                    string.IsNullOrWhiteSpace(stableId))
                {
                    continue;
                }

                DiskIdentity identity = DiskIdentity.Create(deviceId, model, serial, wwn, size, transport, stableId);
                disks.Add(new DiskInfo(identity, IsRemovable: true, IsHotPlug: true, IsReadOnly: readOnly, GetVolumes(index)));
            }
        }

        return Task.FromResult<IReadOnlyList<DiskInfo>>(disks);
    }

    public async Task<DiskInfo> RevalidateAsync(DiskIdentity selected, CancellationToken cancellationToken)
    {
        IReadOnlyList<DiskInfo> current = await EnumerateAsync(cancellationToken);
        DiskInfo? match = current.FirstOrDefault(disk =>
            string.Equals(disk.DevicePath, selected.DevicePath, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new DiskSafetyException($"The selected USB disk {selected.DevicePath} is no longer connected or eligible.");
        if (!selected.IsExactMatch(match.Identity))
            throw new DiskSafetyException($"The identity of {selected.DevicePath} changed. Select the disk again before formatting.");

        return match;
    }

    public async Task<PreparedTarget> PrepareAsync(DiskIdentity selected, CancellationToken cancellationToken)
    {
        DiskInfo current = await RevalidateAsync(selected, cancellationToken);
        int diskIndex = ParseDiskIndex(current.DevicePath);
        List<VolumeLock> locks = [];

        try
        {
            List<string> accessPaths = GetVolumeAccessPaths(diskIndex);
            int partitionCount = GetVolumes(diskIndex).Count;
            if (partitionCount > 0 && accessPaths.Count == 0)
            {
                throw new DiskSafetyException(
                    $"Windows did not report any volume access paths for {current.DevicePath}, so its volumes cannot be locked.");
            }

            foreach (string accessPath in accessPaths)
                locks.Add(VolumeLock.Create(accessPath));

            // Revalidate after all associated volumes are locked to narrow disconnect/replug races.
            await RevalidateAsync(selected, cancellationToken);

            using SafeFileHandle handle = CreateFile(
                current.DevicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
                throw Win32IOException($"Could not open {current.DevicePath} for writing");

            await using FileStream stream = new(handle, FileAccess.ReadWrite);
            DiskFormatter.FormatFat32(stream, current.Size);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            foreach (VolumeLock volumeLock in locks)
                volumeLock.Dispose();
        }

        string mountRoot = await RefreshAndFindDriveLetterAsync(current.Identity, cancellationToken);
        string cleanupToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return new PreparedTarget(mountRoot, current.Identity, "windows", cleanupToken, RequiresFinalize: true);
    }

    public Task<FinalizeResult> FinalizeAsync(PreparedTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using SafeFileHandle handle = CreateFile(
                ToDevicePath(target.MountRoot),
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return Task.FromResult(new FinalizeResult(
                    false,
                    Error: $"Could not open {target.MountRoot} to flush it. Win32 error {Marshal.GetLastWin32Error()}."));
            }

            if (!FlushFileBuffers(handle))
            {
                return Task.FromResult(new FinalizeResult(
                    false,
                    Error: $"Could not flush {target.MountRoot}. Win32 error {Marshal.GetLastWin32Error()}."));
            }

            return Task.FromResult(new FinalizeResult(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new FinalizeResult(false, Error: ex.Message));
        }
    }

    private static List<string> GetVolumeAccessPaths(int diskIndex)
    {
        List<string> paths = [];
        ManagementScope scope = new(@"\\.\root\Microsoft\Windows\Storage");
        scope.Connect();
        using ManagementObjectSearcher searcher = new(
            scope,
            new ObjectQuery($"SELECT AccessPaths FROM MSFT_Partition WHERE DiskNumber = {diskIndex}"));
        using ManagementObjectCollection partitions = searcher.Get();

        foreach (ManagementObject partition in partitions)
        {
            using (partition)
            {
                if (partition["AccessPaths"] is string[] accessPaths)
                    paths.AddRange(accessPaths.Where(path => !string.IsNullOrWhiteSpace(path)));
            }
        }

        return paths;
    }

    private async Task<string> RefreshAndFindDriveLetterAsync(DiskIdentity expected, CancellationToken cancellationToken)
    {
        DiskInfo revalidated = await RevalidateAsync(expected, cancellationToken);
        int diskIndex = ParseDiskIndex(revalidated.DevicePath);

        using (SafeFileHandle handle = CreateFile(
            revalidated.DevicePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero))
        {
            if (handle.IsInvalid)
                throw Win32IOException($"Could not refresh {revalidated.DevicePath}");
            if (!DeviceIoControl(handle, IOCTL_DISK_UPDATE_PROPERTIES, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw Win32IOException($"Could not refresh the partition table for {revalidated.DevicePath}");
        }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            // Revalidate on every attempt so a replacement device which reuses the
            // formatted disk's index can never become the copy destination.
            DiskInfo current = await RevalidateAsync(expected, cancellationToken);
            int currentIndex = ParseDiskIndex(current.DevicePath);
            string? driveRoot = SelectPreparedDriveLetter(GetVolumes(currentIndex));
            if (driveRoot is not null)
                return driveRoot;

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new IOException(
            $"PhysicalDrive{diskIndex} was formatted, but Windows did not expose exactly one FAT32 BADUPDATE volume with a drive letter.");
    }

    internal static string? SelectPreparedDriveLetter(IReadOnlyList<VolumeInfo> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        if (volumes.Count == 0)
            return null;
        if (volumes.Count > 1)
            throw new DiskSafetyException(
                $"The formatted disk exposes {volumes.Count} partitions; exactly one FAT32 BADUPDATE volume is required.");

        VolumeInfo volume = volumes[0];

        // WMI can lag behind the formatter, so only reject filesystem and label
        // values which Windows actually reports.
        string? fileSystem = volume.FileSystem?.Trim();
        if (!string.IsNullOrEmpty(fileSystem) &&
            !fileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
        {
            throw new DiskSafetyException(
                $"The formatted volume reports filesystem '{fileSystem}' instead of FAT32.");
        }

        string? label = volume.Label?.Trim();
        if (!string.IsNullOrEmpty(label) &&
            !label.Equals("BADUPDATE", StringComparison.OrdinalIgnoreCase))
        {
            throw new DiskSafetyException(
                $"The formatted volume reports label '{label}' instead of BADUPDATE.");
        }

        List<string> driveRoots = [];
        foreach (string mountPoint in volume.MountPoints)
        {
            string trimmed = mountPoint.TrimEnd('\\', '/');
            if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
                driveRoots.Add($"{char.ToUpperInvariant(trimmed[0])}:\\");
        }

        if (volume.MountPoints.Count > 1)
            throw new DiskSafetyException(
                $"The formatted volume exposes {volume.MountPoints.Count} mount points; exactly one drive letter is required.");

        return driveRoots.Count == 1 ? driveRoots[0] : null;
    }

    private static HashSet<int> GetSystemDiskIndices()
    {
        HashSet<int> indices = [];
        string? systemDrive = null;

        using (ManagementObjectSearcher searcher = new("SELECT SystemDrive FROM Win32_OperatingSystem"))
        using (ManagementObjectCollection results = searcher.Get())
        {
            foreach (ManagementObject os in results)
            {
                using (os)
                    systemDrive = Convert.ToString(os["SystemDrive"], CultureInfo.InvariantCulture);
            }
        }

        if (string.IsNullOrWhiteSpace(systemDrive))
            throw new DiskSafetyException("Windows did not report its system drive, so no target disks will be offered.");

        string escaped = EscapeWmiObjectPath(systemDrive);
        using ManagementObjectSearcher partitionSearcher = new(
            $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{escaped}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
        using ManagementObjectCollection partitions = partitionSearcher.Get();

        foreach (ManagementObject partition in partitions)
        {
            using (partition)
            {
                if (partition["DiskIndex"] is not null)
                    indices.Add(Convert.ToInt32(partition["DiskIndex"], CultureInfo.InvariantCulture));
            }
        }

        if (indices.Count == 0)
            throw new DiskSafetyException("Windows system-disk ancestry could not be resolved, so no target disks will be offered.");
        return indices;
    }

    private static Dictionary<int, ModernDiskFlags> GetModernDiskFlags()
    {
        Dictionary<int, ModernDiskFlags> result = [];
        try
        {
            ManagementScope scope = new(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using ManagementObjectSearcher searcher = new(
                scope,
                new ObjectQuery("SELECT Number, IsReadOnly, IsOffline, IsSystem, IsBoot, UniqueId FROM MSFT_Disk"));
            using ManagementObjectCollection disks = searcher.Get();
            foreach (ManagementObject disk in disks)
            {
                using (disk)
                {
                    if (disk["Number"] is null)
                        continue;
                    int number = Convert.ToInt32(disk["Number"], CultureInfo.InvariantCulture);
                    result[number] = new ModernDiskFlags(
                        Convert.ToBoolean(disk["IsReadOnly"], CultureInfo.InvariantCulture),
                        Convert.ToBoolean(disk["IsOffline"], CultureInfo.InvariantCulture),
                        Convert.ToBoolean(disk["IsSystem"], CultureInfo.InvariantCulture),
                        Convert.ToBoolean(disk["IsBoot"], CultureInfo.InvariantCulture),
                        Convert.ToString(disk["UniqueId"], CultureInfo.InvariantCulture)?.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            // Without MSFT_Disk the system-disk and read-only flags cannot be
            // trusted, so refuse to offer any disk rather than guess.
            throw new DiskSafetyException(
                "Windows storage metadata (MSFT_Disk) is unavailable, so no target disks will be offered.",
                ex);
        }
        return result;
    }

    private static List<VolumeInfo> GetVolumes(int diskIndex)
    {
        List<VolumeInfo> volumes = [];
        using ManagementObjectSearcher partitionSearcher = new(
            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\.\\PHYSICALDRIVE{diskIndex}'}} " +
            "WHERE AssocClass=Win32_DiskDriveToDiskPartition");
        using ManagementObjectCollection partitions = partitionSearcher.Get();

        foreach (ManagementObject partition in partitions)
        {
            using (partition)
            {
                string partitionId = Convert.ToString(partition["DeviceID"], CultureInfo.InvariantCulture) ?? $"Disk #{diskIndex} partition";
                using ManagementObjectSearcher logicalSearcher = new(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{EscapeWmiObjectPath(partitionId)}'}} " +
                    "WHERE AssocClass=Win32_LogicalDiskToPartition");
                using ManagementObjectCollection logicalDisks = logicalSearcher.Get();

                List<string> mountPoints = [];
                string? fileSystem = null;
                string? label = null;
                foreach (ManagementObject logical in logicalDisks)
                {
                    using (logical)
                    {
                        string? letter = Convert.ToString(logical["DeviceID"], CultureInfo.InvariantCulture);
                        if (!string.IsNullOrWhiteSpace(letter))
                            mountPoints.Add(letter + Path.DirectorySeparatorChar);
                        fileSystem ??= Convert.ToString(logical["FileSystem"], CultureInfo.InvariantCulture);
                        label ??= Convert.ToString(logical["VolumeName"], CultureInfo.InvariantCulture);
                    }
                }

                volumes.Add(new VolumeInfo(partitionId, "part", fileSystem, label, mountPoints));
            }
        }

        return volumes;
    }

    internal static string ToDevicePath(string accessPath)
    {
        string trimmed = accessPath.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
            return $@"\\.\{trimmed}";
        if (trimmed.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase))
            return @"\\.\" + trimmed[4..];
        return $@"\\.\{trimmed}";
    }

    internal static int ParseDiskIndex(string devicePath)
    {
        const string marker = "PhysicalDrive";
        int markerIndex = devicePath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0 || !int.TryParse(devicePath[(markerIndex + marker.Length)..], out int index))
            throw new DiskSafetyException($"Unexpected Windows disk path: {devicePath}");
        return index;
    }

    private static string EscapeWmiObjectPath(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static IOException Win32IOException(string message) =>
        new($"{message}. Win32 error {Marshal.GetLastWin32Error()}.");

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FSCTL_LOCK_VOLUME = 0x00090018;
    private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    private const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle hFile);

    private sealed class VolumeLock : IDisposable
    {
        private readonly SafeFileHandle _handle;

        private VolumeLock(SafeFileHandle handle) => _handle = handle;

        internal static VolumeLock Create(string accessPath)
        {
            string devicePath = ToDevicePath(accessPath);
            SafeFileHandle handle = CreateFile(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException($"Could not open volume {accessPath} for locking. Win32 error {error}.");
            }

            if (!DeviceIoControl(handle, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException($"Could not lock volume {accessPath}. Close applications using it and retry. Win32 error {error}.");
            }

            if (!DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException($"Could not dismount volume {accessPath}. Win32 error {error}.");
            }

            return new VolumeLock(handle);
        }

        public void Dispose() => _handle.Dispose();
    }

    private sealed record ModernDiskFlags(
        bool IsReadOnly,
        bool IsOffline,
        bool IsSystem,
        bool IsBoot,
        string? UniqueId);
}
