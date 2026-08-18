using System.Globalization;
using System.Text.Json;
using BadBuilder.Services;

namespace BadBuilder.Services.Disks;

internal static class LinuxDiskHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunEntryPointAsync(string[] arguments)
    {
        LinuxHelperResponse response;
        try
        {
            HelperRequest request = ParseRequest(arguments);
            ValidateExecutionContext(request);
            response = request.Operation switch
            {
                "prepare" => await PrepareAsync(request),
                "finalize" => await FinalizeAsync(request),
                _ => throw new InvalidOperationException("The disk helper supports only prepare and finalize."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            response = new LinuxHelperResponse(Success: false, Error: ex.Message);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        return response.Success ? 0 : 4;
    }

    internal static HelperRequest ParseRequest(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || arguments[0] is not ("prepare" or "finalize"))
            throw new ArgumentException("The disk helper requires exactly one operation: prepare or finalize.");

        HashSet<string> allowed = ["--device", "--fingerprint", "--uid", "--gid", "--token", "--mount", "--workspace", "--cache"];
        Dictionary<string, string> values = new(StringComparer.Ordinal);

        for (int index = 1; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count || !allowed.Contains(arguments[index]))
                throw new ArgumentException($"Unknown or incomplete disk-helper argument: {arguments[index]}");
            if (!values.TryAdd(arguments[index], arguments[index + 1]))
                throw new ArgumentException($"Duplicate disk-helper argument: {arguments[index]}");
        }

        string GetRequired(string name) => values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"The disk helper requires {name}.");

        string device = GetRequired("--device");
        string fingerprint = GetRequired("--fingerprint");
        string token = GetRequired("--token");
        string workspace = GetRequired("--workspace");
        string cache = GetRequired("--cache");

        if (!device.StartsWith("/dev/", StringComparison.Ordinal) || !Path.IsPathFullyQualified(device))
            throw new ArgumentException("The disk-helper device must be an absolute /dev path.");
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("The disk-helper fingerprint is invalid.");
        if (token.Length != 32 || !token.All(Uri.IsHexDigit))
            throw new ArgumentException("The disk-helper token is invalid.");
        if (!uint.TryParse(GetRequired("--uid"), NumberStyles.None, CultureInfo.InvariantCulture, out uint uid) || uid == 0)
            throw new ArgumentException("The disk-helper caller UID is invalid.");
        if (!uint.TryParse(GetRequired("--gid"), NumberStyles.None, CultureInfo.InvariantCulture, out uint gid))
            throw new ArgumentException("The disk-helper caller GID is invalid.");
        if (!Path.IsPathFullyQualified(workspace) || !Path.IsPathFullyQualified(cache))
            throw new ArgumentException("The disk-helper protected paths must be absolute.");

        string? mount = values.GetValueOrDefault("--mount");
        if (arguments[0] == "finalize" && string.IsNullOrWhiteSpace(mount))
            throw new ArgumentException("The finalize operation requires --mount.");
        if (arguments[0] == "prepare" && mount is not null)
            throw new ArgumentException("The prepare operation does not accept --mount.");

        string expectedMount = GetExpectedMount(uid, token);
        if (mount is not null && !string.Equals(Path.GetFullPath(mount), expectedMount, StringComparison.Ordinal))
            throw new ArgumentException("The finalize mount path does not match the caller and cleanup token.");

        return new HelperRequest(
            arguments[0],
            device,
            fingerprint.ToUpperInvariant(),
            uid,
            gid,
            token.ToLowerInvariant(),
            mount is null ? null : expectedMount,
            Path.GetFullPath(workspace),
            Path.GetFullPath(cache));
    }

    private static void ValidateExecutionContext(HelperRequest request)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The privileged disk helper is available only on Linux.");
        if (LinuxNative.GetEffectiveUserId() != 0)
            throw new UnauthorizedAccessException("The disk helper must be invoked through sudo.");

        string? sudoUidText = Environment.GetEnvironmentVariable("SUDO_UID");
        if (!uint.TryParse(sudoUidText, NumberStyles.None, CultureInfo.InvariantCulture, out uint sudoUid) || sudoUid != request.Uid)
            throw new UnauthorizedAccessException("The disk helper caller does not match SUDO_UID.");
        string? sudoGidText = Environment.GetEnvironmentVariable("SUDO_GID");
        if (!uint.TryParse(sudoGidText, NumberStyles.None, CultureInfo.InvariantCulture, out uint sudoGid) || sudoGid != request.Gid)
            throw new UnauthorizedAccessException("The disk helper caller does not match SUDO_GID.");
    }

    private static async Task<LinuxHelperResponse> PrepareAsync(HelperRequest request)
    {
        EnsureCommands("lsblk", "findmnt", "umount", "wipefs", "blockdev", "mount");
        DiskInfo disk = await RevalidateAsync(request, CancellationToken.None);
        string leasePath = GetLeasePath(request.Fingerprint);
        string lockPath = GetLockPath(request.Fingerprint);

        await using FileStream operationLock = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(leasePath))
            throw new DiskSafetyException($"Another BadBuilder operation already owns {request.Device}. Finalize it before retrying.");

        LeaseRecord lease = new(request.Device, request.Fingerprint, request.Uid, request.Gid, request.Token, GetExpectedMount(request.Uid, request.Token));
        await CreateLeaseAsync(leasePath, lease);

        bool mounted = false;
        try
        {
            disk = await UnmountChildVolumesAsync(request, disk);

            ProcessResult wipe = await ProcessRunner.RunAsync(
                ProcessRunner.RequireExecutable("wipefs"), ["--all", "--", request.Device], CancellationToken.None);
            wipe.EnsureSuccess($"Wiping old filesystem signatures on {request.Device}");

            await using (FileStream stream = new(
                request.Device,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                DiskFormatter.FormatFat32(stream, disk.Size);
                stream.Flush(flushToDisk: true);
            }

            ProcessResult reread = await ProcessRunner.RunAsync(
                ProcessRunner.RequireExecutable("blockdev"), BuildBlockdevRereadArguments(request.Device), CancellationToken.None);
            reread.EnsureSuccess($"Refreshing the partition table on {request.Device}");

            await SettleDevicesAsync();
            string partition = await WaitForSinglePartitionAsync(request, CancellationToken.None);
            disk = await RevalidateAsync(request, CancellationToken.None);
            disk = await UnmountChildVolumesAsync(request, disk);
            string mountRoot = lease.MountRoot;
            CreateMountDirectory(mountRoot, request.Uid, request.Gid);

            string options = $"nodev,nosuid,noexec,uid={request.Uid.ToString(CultureInfo.InvariantCulture)},gid={request.Gid.ToString(CultureInfo.InvariantCulture)},umask=0077";
            ProcessResult mount = await ProcessRunner.RunAsync(
                ProcessRunner.RequireExecutable("mount"), ["--options", options, "--", partition, mountRoot], CancellationToken.None);
            if (mount.ExitCode != 0)
            {
                // A desktop automounter can claim the new volume between udev
                // settling and our mount. Release that normal mount and retry once.
                disk = await RevalidateAsync(request, CancellationToken.None);
                if (disk.Volumes.Any(volume => volume.MountPoints.Count > 0))
                {
                    await UnmountChildVolumesAsync(request, disk);
                    mount = await ProcessRunner.RunAsync(
                        ProcessRunner.RequireExecutable("mount"), ["--options", options, "--", partition, mountRoot], CancellationToken.None);
                }
            }
            mount.EnsureSuccess($"Mounting {partition}");
            mounted = true;

            await VerifyMountAsync(partition, mountRoot, request.Uid, request.Gid, CancellationToken.None);
            return new LinuxHelperResponse(Success: true, MountRoot: mountRoot);
        }
        catch (Exception ex)
        {
            if (mounted)
            {
                try
                {
                    ProcessResult cleanup = await ProcessRunner.RunAsync(
                        ProcessRunner.RequireExecutable("umount"), ["--", lease.MountRoot], CancellationToken.None);
                    mounted = cleanup.ExitCode != 0;
                }
                catch (IOException)
                {
                    mounted = true;
                }
            }

            if (!mounted)
            {
                DeleteEmptyMountDirectories(lease.MountRoot, request.Uid);
                File.Delete(leasePath);
            }
            else
            {
                throw new DiskSafetyException(
                    $"USB preparation failed, and the target remains mounted at {lease.MountRoot}: {ex.Message}");
            }
            throw;
        }
    }

    private static async Task<LinuxHelperResponse> FinalizeAsync(HelperRequest request)
    {
        EnsureCommands("findmnt", "umount", "sync");
        string leasePath = GetLeasePath(request.Fingerprint);
        string lockPath = GetLockPath(request.Fingerprint);

        await using FileStream operationLock = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        LeaseRecord lease = await ReadLeaseAsync(leasePath);
        if (!lease.Matches(request))
            throw new DiskSafetyException("The finalize request does not match the active disk lease.");

        await RevalidateAsync(request, CancellationToken.None);

        bool mounted = await IsMountedAsync(lease.MountRoot, CancellationToken.None);
        if (mounted)
        {
            ProcessResult flush = await ProcessRunner.RunAsync(
                ProcessRunner.RequireExecutable("sync"), ["--file-system", lease.MountRoot], CancellationToken.None);
            if (flush.ExitCode != 0)
                return new LinuxHelperResponse(false, lease.MountRoot, StillMounted: true, Error: "Could not flush all USB writes before unmounting.");

            ProcessResult unmount = await ProcessRunner.RunAsync(
                ProcessRunner.RequireExecutable("umount"), ["--", lease.MountRoot], CancellationToken.None);
            if (unmount.ExitCode != 0)
            {
                string error = string.IsNullOrWhiteSpace(unmount.StandardError)
                    ? "The USB remains mounted because normal unmounting failed. Close programs using it and retry."
                    : $"The USB remains mounted: {unmount.StandardError.Trim()}";
                return new LinuxHelperResponse(false, lease.MountRoot, StillMounted: true, Error: error);
            }
        }

        DeleteEmptyMountDirectories(lease.MountRoot, request.Uid);
        File.Delete(leasePath);
        return new LinuxHelperResponse(Success: true);
    }

    private static async Task<DiskInfo> RevalidateAsync(HelperRequest request, CancellationToken cancellationToken)
    {
        LinuxInventory inventory = await LinuxDiskEnumerator.ReadAsync(
            [request.Workspace, request.Cache, AppContext.BaseDirectory], cancellationToken);
        DiskInfo? disk = inventory.EligibleDisks.FirstOrDefault(candidate =>
            string.Equals(candidate.DevicePath, request.Device, StringComparison.Ordinal));

        if (disk is null)
            throw new DiskSafetyException($"{request.Device} is not an eligible writable USB/removable disk or is a protected system device.");
        if (!string.Equals(disk.Identity.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
            throw new DiskSafetyException($"The hardware identity of {request.Device} changed. Nothing was written.");
        if (disk.IsReadOnly)
            throw new DiskSafetyException($"{request.Device} is read-only.");
        return disk;
    }

    private static async Task<DiskInfo> UnmountChildVolumesAsync(HelperRequest request, DiskInfo disk)
    {
        foreach (string mountPoint in disk.Volumes
            .SelectMany(volume => volume.MountPoints)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(path => path.Length))
        {
            ProcessResult unmount = await ProcessRunner.RunAsync(
                ProcessRunner.RequireExecutable("umount"), ["--", mountPoint], CancellationToken.None);
            unmount.EnsureSuccess($"Unmounting {mountPoint}");
        }

        DiskInfo refreshed = await RevalidateAsync(request, CancellationToken.None);
        if (refreshed.Volumes.Any(volume => volume.MountPoints.Count > 0))
            throw new DiskSafetyException($"One or more volumes on {request.Device} remain mounted.");
        return refreshed;
    }

    private static async Task<string> WaitForSinglePartitionAsync(HelperRequest request, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            LinuxInventory inventory = await LinuxDiskEnumerator.ReadAsync(
                [request.Workspace, request.Cache, AppContext.BaseDirectory], cancellationToken);
            DiskInfo? verifiedDisk = inventory.EligibleDisks.FirstOrDefault(candidate =>
                string.Equals(candidate.DevicePath, request.Device, StringComparison.Ordinal));
            if (verifiedDisk is null ||
                !string.Equals(verifiedDisk.Identity.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
            {
                throw new DiskSafetyException($"The identity or safety status of {request.Device} changed after formatting.");
            }

            LinuxBlockNode? diskNode = inventory.AllNodes.FirstOrDefault(node =>
                string.Equals(node.Type, "disk", StringComparison.Ordinal) &&
                string.Equals(node.Path, request.Device, StringComparison.Ordinal));

            string[] partitions = diskNode is null
                ? []
                : [..diskNode.Children.Where(node => string.Equals(node.Type, "part", StringComparison.Ordinal)).Select(node => node.Path)];
            if (partitions.Length == 1)
                return partitions[0];
            if (partitions.Length > 1)
                throw new DiskSafetyException($"Unexpected partition layout appeared on {request.Device}.");

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new IOException($"The new partition on {request.Device} did not appear.");
    }

    private static async Task SettleDevicesAsync()
    {
        try
        {
            string udevadm = ProcessRunner.RequireExecutable("udevadm");
            await ProcessRunner.RunAsync(udevadm, ["settle", "--timeout=10"], CancellationToken.None);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"udevadm is unavailable; polling for the new partition instead: {ex.Message}");
        }
    }

    private static void CreateMountDirectory(string mountRoot, uint uid, uint gid)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Unix mount permissions are available only on Linux.");

        const string runtimeRoot = "/run/badbuilder";
        Directory.CreateDirectory(runtimeRoot);
        EnsureRealDirectory(runtimeRoot);
        LinuxNative.ChangeOwner(runtimeRoot, 0, 0);
        File.SetUnixFileMode(
            runtimeRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        string uidRoot = Path.Combine(runtimeRoot, uid.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(uidRoot);
        EnsureRealDirectory(uidRoot);
        LinuxNative.ChangeOwner(uidRoot, 0, 0);
        File.SetUnixFileMode(
            uidRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        if (Path.Exists(mountRoot))
            throw new DiskSafetyException("The randomized USB mount directory already exists.");
        Directory.CreateDirectory(mountRoot);
        EnsureRealDirectory(mountRoot);
        LinuxNative.ChangeOwner(mountRoot, uid, gid);
        File.SetUnixFileMode(mountRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void EnsureRealDirectory(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new DiskSafetyException($"Refusing unsafe runtime path: {path}");
    }

    private static async Task VerifyMountAsync(
        string partition,
        string mountRoot,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        ProcessResult find = await ProcessRunner.RunAsync(
            ProcessRunner.RequireExecutable("findmnt"),
            ["--json", "--mountpoint", mountRoot, "--output", "SOURCE,TARGET,OPTIONS"],
            cancellationToken);
        find.EnsureSuccess($"Verifying the mount at {mountRoot}");

        using JsonDocument document = JsonDocument.Parse(find.StandardOutput);
        JsonElement mount = document.RootElement.GetProperty("filesystems").EnumerateArray().Single();
        string source = mount.GetProperty("source").GetString() ?? string.Empty;
        string target = mount.GetProperty("target").GetString() ?? string.Empty;
        string options = mount.GetProperty("options").GetString() ?? string.Empty;

        HashSet<string> mountOptions = [..options.Split(',', StringSplitOptions.RemoveEmptyEntries)];
        string expectedUid = $"uid={uid.ToString(CultureInfo.InvariantCulture)}";
        string expectedGid = $"gid={gid.ToString(CultureInfo.InvariantCulture)}";
        bool privateMask = mountOptions.Contains("umask=0077") ||
            mountOptions.Contains("fmask=0077") && mountOptions.Contains("dmask=0077");
        if (!string.Equals(source, partition, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(target), mountRoot, StringComparison.Ordinal) ||
            !mountOptions.Contains("nodev") ||
            !mountOptions.Contains("nosuid") ||
            !mountOptions.Contains("noexec") ||
            !mountOptions.Contains(expectedUid) ||
            !mountOptions.Contains(expectedGid) ||
            !privateMask)
        {
            throw new DiskSafetyException("The prepared USB mount did not have the expected source, ownership, and safety options.");
        }
    }

    private static async Task<bool> IsMountedAsync(string mountRoot, CancellationToken cancellationToken)
    {
        ProcessResult find = await ProcessRunner.RunAsync(
            ProcessRunner.RequireExecutable("findmnt"), ["--mountpoint", mountRoot], cancellationToken);
        return find.ExitCode == 0;
    }

    private static async Task CreateLeaseAsync(string path, LeaseRecord lease)
    {
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await JsonSerializer.SerializeAsync(stream, lease, JsonOptions);
        await stream.FlushAsync();
    }

    private static async Task<LeaseRecord> ReadLeaseAsync(string path)
    {
        if (!File.Exists(path))
            throw new DiskSafetyException("No active disk lease exists for this finalize request.");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<LeaseRecord>(stream, JsonOptions)
            ?? throw new InvalidDataException("The active disk lease is corrupt.");
    }

    private static void DeleteEmptyMountDirectories(string mountRoot, uint uid)
    {
        if (Directory.Exists(mountRoot))
            Directory.Delete(mountRoot, recursive: false);

        string uidRoot = Path.Combine("/run/badbuilder", uid.ToString(CultureInfo.InvariantCulture));
        if (Directory.Exists(uidRoot) && !Directory.EnumerateFileSystemEntries(uidRoot).Any())
            Directory.Delete(uidRoot, recursive: false);
        if (Directory.Exists("/run/badbuilder") && !Directory.EnumerateFileSystemEntries("/run/badbuilder").Any())
            Directory.Delete("/run/badbuilder", recursive: false);
    }

    private static string GetExpectedMount(uint uid, string token) =>
        Path.GetFullPath(Path.Combine("/run/badbuilder", uid.ToString(CultureInfo.InvariantCulture), token));

    // Unlike most util-linux commands, blockdev parses every leading --token as a
    // command and does not support the conventional standalone -- separator.
    internal static IReadOnlyList<string> BuildBlockdevRereadArguments(string device) =>
        ["--rereadpt", device];

    private static string GetLeasePath(string fingerprint) =>
        Path.Combine("/run/lock", $"badbuilder-{fingerprint[..24].ToLowerInvariant()}.lease.json");

    private static string GetLockPath(string fingerprint) =>
        Path.Combine("/run/lock", $"badbuilder-{fingerprint[..24].ToLowerInvariant()}.lock");

    private static void EnsureCommands(params string[] commands)
    {
        foreach (string command in commands)
            ProcessRunner.RequireExecutable(command);
    }
}

internal sealed record HelperRequest(
    string Operation,
    string Device,
    string Fingerprint,
    uint Uid,
    uint Gid,
    string Token,
    string? Mount,
    string Workspace,
    string Cache);

internal sealed record LeaseRecord(
    string Device,
    string Fingerprint,
    uint Uid,
    uint Gid,
    string Token,
    string MountRoot)
{
    internal bool Matches(HelperRequest request) =>
        string.Equals(Device, request.Device, StringComparison.Ordinal) &&
        string.Equals(Fingerprint, request.Fingerprint, StringComparison.Ordinal) &&
        Uid == request.Uid &&
        Gid == request.Gid &&
        string.Equals(Token, request.Token, StringComparison.Ordinal) &&
        string.Equals(MountRoot, request.Mount, StringComparison.Ordinal);
}
