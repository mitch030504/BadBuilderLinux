using BadBuilder.Services;
using BadBuilder.Services.Disks;
using BadBuilder.UI;

namespace BadBuilder.Application;

internal sealed record InstallExecutionResult(
    DiskInfo RevalidatedDisk,
    bool CopiedSuccessfully,
    bool FinalizedSuccessfully,
    bool FormatCompleted,
    PreparedTarget? PreparedTarget,
    bool CancelledBeforePrepare,
    Exception? Error = null);

/// <summary>
/// Owns the irreversible part of an installation: identity revalidation, exact
/// device-path confirmation, format, copy, and cleanup. Every dependency is
/// injected so the safety properties can be proven without a physical disk.
/// </summary>
internal sealed class InstallCoordinator
{
    private readonly IDiskBackend _backend;
    private readonly IUserInterface _ui;
    private readonly IPendingCleanupStore _cleanupStore;
    private readonly Func<InstallPlan, string, CancellationToken, Task> _copy;

    internal InstallCoordinator(
        IDiskBackend backend,
        IUserInterface ui,
        IPendingCleanupStore cleanupStore,
        Func<InstallPlan, string, CancellationToken, Task>? copy = null)
    {
        _backend = backend;
        _ui = ui;
        _cleanupStore = cleanupStore;
        _copy = copy ?? InstallService.ExecuteAsync;
    }

    internal async Task<InstallExecutionResult> ExecuteAsync(
        DiskInfo selected,
        InstallPlan plan,
        CancellationToken cancellationToken)
    {
        bool formatCompleted = false;
        bool copied = false;
        bool finalized = true;
        Exception? copyError = null;
        PreparedTarget? prepared = null;
        IDisposable? cancellationBlock = null;

        try
        {
            try
            {
                selected = await _backend.RevalidateAsync(selected.Identity, cancellationToken);

                _ui.PadLine();
                _ui.WriteWarning("The next step permanently erases the selected disk.");
                _ui.WriteInfo($"Device: {selected.DevicePath}");
                _ui.WriteInfo($"Model: {selected.Name}");
                _ui.WriteInfo($"Size: {selected.Size / 1024d / 1024d / 1024d:0.00} GiB");
                _ui.WriteInfo($"Serial/WWN: {selected.Serial ?? selected.Wwn ?? "not reported"}");

                string confirmation = _ui.PromptText(
                    $"Type the exact device path '{selected.DevicePath}' to continue");
                StringComparison comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                if (!string.Equals(confirmation, selected.DevicePath, comparison))
                {
                    _ui.WriteWarning("Device-path confirmation did not match. Nothing was formatted.");
                    _ui.Pause();
                    return new InstallExecutionResult(selected, false, true, false, null, false);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new InstallExecutionResult(selected, false, true, false, null, true);
            }

            _ui.WriteWarning(
                "Disk preparation has started. Ctrl+C cancellation is disabled until cleanup finishes.");
            cancellationBlock = CancellationGate.Block();
            prepared = await _backend.PrepareAsync(selected.Identity, CancellationToken.None);
            formatCompleted = true;

            if (prepared.RequiresFinalize)
            {
                try
                {
                    _cleanupStore.Save(prepared);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Error(ex, "Could not save the pending USB cleanup record.");
                    _ui.WriteWarning(
                        "The pending USB cleanup record could not be saved. If cleanup fails, the USB may remain mounted.");
                }
            }

            _ui.WriteInfo("Copying the fully validated installation plan.");
            try
            {
                // The device can be swapped or the volume unmounted while the plan
                // is copied. Revalidate immediately before writing and confirm the
                // prepared mount still belongs to this disk.
                selected = await _backend.RevalidateAsync(selected.Identity, CancellationToken.None);
                if (!IsExpectedMount(selected, prepared.MountRoot))
                {
                    throw new DiskSafetyException(
                        $"The prepared volume {prepared.MountRoot} is no longer associated with {selected.DevicePath}.");
                }

                await _copy(plan, prepared.MountRoot, CancellationToken.None);
                copied = true;
            }
            catch (Exception ex)
            {
                copyError = ex;
            }
        }
        finally
        {
            if (prepared?.RequiresFinalize == true)
                finalized = await FinalizeAsync(prepared);
            cancellationBlock?.Dispose();
        }

        return new InstallExecutionResult(
            selected,
            copied,
            finalized,
            formatCompleted,
            prepared,
            false,
            copyError);
    }

    private static bool IsExpectedMount(DiskInfo disk, string mountRoot)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string expected = mountRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return disk.Volumes
            .SelectMany(volume => volume.MountPoints)
            .Select(path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Any(path => string.Equals(path, expected, comparison));
    }

    private async Task<bool> FinalizeAsync(PreparedTarget prepared)
    {
        try
        {
            FinalizeResult result = await _backend.FinalizeAsync(prepared, CancellationToken.None);
            if (result.Success)
            {
                try
                {
                    _cleanupStore.Clear();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Error(ex, "Could not clear the pending USB cleanup record.");
                    _ui.WriteWarning(
                        "The USB was cleaned up, but the pending cleanup record could not be removed. Retry cleanup to clear it.");
                }
                return true;
            }

            string message = result.Error ?? "USB cleanup failed.";
            _ui.WriteWarning(result.StillMounted
                ? $"{message} The USB remains mounted at {prepared.MountRoot}."
                : message);
            DiagnosticLog.Info($"USB finalization failed: {message}");
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(ex, "Unexpected USB finalization failure.");
            _ui.WriteWarning(
                $"USB cleanup failed unexpectedly. Treat the USB as still mounted at {prepared.MountRoot} and close programs using it before retrying.");
            return false;
        }
    }
}
