using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BadBuilder.Services;

namespace BadBuilder.Services.Disks;

internal static partial class LinuxNative
{
    [DllImport("libc", EntryPoint = "getuid")]
    internal static extern uint GetUserId();

    [DllImport("libc", EntryPoint = "geteuid")]
    internal static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getgid")]
    internal static extern uint GetGroupId();

    [LibraryImport("libc", EntryPoint = "chown", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ChangeOwnerNative(string path, uint owner, uint group);

    internal static void ChangeOwner(string path, uint owner, uint group)
    {
        if (ChangeOwnerNative(path, owner, group) != 0)
            throw new IOException($"Could not assign ownership of {path}. OS error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}.");
    }
}

internal static class LinuxDiskEnumerator
{
    internal static readonly string[] LsblkArguments =
    [
        "--json", "--bytes", "--paths",
        "--output", "NAME,KNAME,PATH,PKNAME,TYPE,SIZE,MODEL,SERIAL,WWN,TRAN,RM,HOTPLUG,RO,FSTYPE,LABEL,MOUNTPOINTS",
    ];

    internal static readonly string[] FindmntArguments = ["--json", "--real", "--output", "SOURCE,TARGET"];

    internal static async Task<LinuxInventory> ReadAsync(IEnumerable<string> protectedPaths, CancellationToken cancellationToken)
    {
        string lsblk = ProcessRunner.RequireExecutable("lsblk");
        string findmnt = ProcessRunner.RequireExecutable("findmnt");

        Task<ProcessResult> lsblkTask = ProcessRunner.RunAsync(lsblk, LsblkArguments, cancellationToken);
        Task<ProcessResult> findmntTask = ProcessRunner.RunAsync(findmnt, FindmntArguments, cancellationToken);
        await Task.WhenAll(lsblkTask, findmntTask);

        ProcessResult lsblkResult = await lsblkTask;
        ProcessResult findmntResult = await findmntTask;
        lsblkResult.EnsureSuccess("Disk enumeration (lsblk)");
        findmntResult.EnsureSuccess("Mounted-filesystem enumeration (findmnt)");

        string swaps = File.Exists("/proc/swaps")
            ? await File.ReadAllTextAsync("/proc/swaps", cancellationToken)
            : string.Empty;

        return LinuxDiskInventory.Parse(lsblkResult.StandardOutput, findmntResult.StandardOutput, swaps, protectedPaths);
    }
}

internal sealed class LinuxDiskBackend : IDiskBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DiskInfo>> EnumerateAsync(CancellationToken cancellationToken)
    {
        EnsureLinux();
        LinuxInventory inventory = await LinuxDiskEnumerator.ReadAsync(GetProtectedPaths(), cancellationToken);
        return inventory.EligibleDisks;
    }

    public async Task<DiskInfo> RevalidateAsync(DiskIdentity selected, CancellationToken cancellationToken)
    {
        IReadOnlyList<DiskInfo> current = await EnumerateAsync(cancellationToken);
        DiskInfo? match = current.FirstOrDefault(disk =>
            string.Equals(disk.DevicePath, selected.DevicePath, StringComparison.Ordinal));

        if (match is null)
            throw new DiskSafetyException($"The selected USB disk {selected.DevicePath} is disconnected, read-only, or no longer safe to use.");
        if (!selected.IsExactMatch(match.Identity))
            throw new DiskSafetyException($"The identity of {selected.DevicePath} changed. Select the disk again before formatting.");

        return match;
    }

    public async Task<PreparedTarget> PrepareAsync(DiskIdentity selected, CancellationToken cancellationToken)
    {
        await RevalidateAsync(selected, cancellationToken);
        string token = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        uint uid = LinuxNative.GetUserId();
        uint gid = LinuxNative.GetGroupId();

        List<string> helperArguments =
        [
            "--disk-helper", "prepare",
            "--device", selected.DevicePath,
            "--fingerprint", selected.Fingerprint,
            "--uid", uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--gid", gid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--token", token,
            "--workspace", AppPaths.WorkspaceRoot,
            "--cache", AppPaths.CacheRoot,
        ];

        LinuxHelperResponse response = await InvokeHelperAsync(helperArguments, cancellationToken);
        if (!response.Success || string.IsNullOrWhiteSpace(response.MountRoot))
            throw new DiskSafetyException(response.Error ?? "The privileged disk helper did not prepare the USB disk.");

        return new PreparedTarget(response.MountRoot, selected, "linux", token, RequiresFinalize: true);
    }

    public async Task<FinalizeResult> FinalizeAsync(PreparedTarget target, CancellationToken cancellationToken)
    {
        List<string> helperArguments =
        [
            "--disk-helper", "finalize",
            "--device", target.Identity.DevicePath,
            "--fingerprint", target.Identity.Fingerprint,
            "--uid", LinuxNative.GetUserId().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--gid", LinuxNative.GetGroupId().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--token", target.CleanupToken,
            "--mount", target.MountRoot,
            "--workspace", AppPaths.WorkspaceRoot,
            "--cache", AppPaths.CacheRoot,
        ];

        try
        {
            LinuxHelperResponse response = await InvokeHelperAsync(helperArguments, cancellationToken);
            return new FinalizeResult(response.Success, response.StillMounted, response.Error);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new FinalizeResult(Success: false, StillMounted: true, Error: ex.Message);
        }
    }

    internal static (string FileName, IReadOnlyList<string> Arguments) BuildSudoInvocation(
        string sudoPath,
        IReadOnlyList<string> helperArguments,
        string? processPath = null,
        string? entryAssemblyPath = null)
    {
        processPath ??= Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable.");
        entryAssemblyPath ??= Assembly.GetEntryAssembly()?.Location;

        List<string> arguments = ["--", processPath];
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(entryAssemblyPath))
                throw new InvalidOperationException("The BadBuilder assembly path is unavailable for helper invocation.");
            arguments.Add(entryAssemblyPath);
        }

        arguments.AddRange(helperArguments);
        return (sudoPath, arguments);
    }

    private static async Task<LinuxHelperResponse> InvokeHelperAsync(IReadOnlyList<string> helperArguments, CancellationToken cancellationToken)
    {
        string sudo = ProcessRunner.RequireExecutable("sudo");
        (string fileName, IReadOnlyList<string> arguments) = BuildSudoInvocation(sudo, helperArguments);
        ProcessResult process = await ProcessRunner.RunAsync(fileName, arguments, cancellationToken);

        LinuxHelperResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<LinuxHelperResponse>(process.StandardOutput.Trim(), JsonOptions);
        }
        catch (JsonException ex)
        {
            string diagnostic = string.IsNullOrWhiteSpace(process.StandardError) ? "no diagnostics" : process.StandardError.Trim();
            throw new IOException($"The privileged disk helper returned invalid output ({diagnostic}).", ex);
        }

        if (response is null)
            throw new IOException("The privileged disk helper returned no result.");
        if (process.ExitCode != 0 && response.Success)
            throw new IOException($"The privileged disk helper exited with code {process.ExitCode}.");

        return response;
    }

    private static IEnumerable<string> GetProtectedPaths()
    {
        yield return AppPaths.WorkspaceRoot;
        yield return AppPaths.CacheRoot;
        yield return AppContext.BaseDirectory;
    }

    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Linux disk backend can only run on Linux.");
    }
}

internal sealed record LinuxHelperResponse(
    bool Success,
    string? MountRoot = null,
    bool StillMounted = false,
    string? Error = null);
