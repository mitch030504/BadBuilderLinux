using System.Runtime.Versioning;
using System.Security.Principal;
using BadBuilder.Application;
using BadBuilder.Services;
using BadBuilder.Services.Disks;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--disk-helper", StringComparison.Ordinal))
            return await LinuxDiskHelper.RunEntryPointAsync(args[1..]);

        if (args.Length == 1 && string.Equals(args[0], "--version", StringComparison.Ordinal))
        {
            Console.WriteLine(AppVersion.Informational);
            return (int)ExitCode.Success;
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: BadBuilder [--version]");
            return (int)ExitCode.InvalidArguments;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("BadBuilder supports Windows and Linux only; macOS is not supported.");
            return (int)ExitCode.UnsupportedPlatform;
        }

        if (OperatingSystem.IsLinux() && LinuxNative.GetEffectiveUserId() == 0)
        {
            Console.Error.WriteLine("Run BadBuilder as your desktop user, without sudo. It requests sudo only for USB preparation and cleanup.");
            return (int)ExitCode.PrivilegeError;
        }

        string logPath;
        try
        {
            logPath = DiagnosticLog.Initialize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"BadBuilder could not initialize its diagnostic log: {ex.Message}");
            return (int)ExitCode.UnhandledFailure;
        }

        if (OperatingSystem.IsWindows() && !IsWindowsAdministrator())
        {
            Console.Error.WriteLine("On Windows, run BadBuilder from an Administrator terminal so it can safely lock and format the USB drive.");
            return (int)ExitCode.PrivilegeError;
        }

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (!CancellationGate.IsBlocked)
                cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;

        try
        {
            await BuilderApp.RunAsync(cancellation.Token);
            return cancellation.IsCancellationRequested ? (int)ExitCode.Cancelled : (int)ExitCode.Success;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Operation cancelled before destructive disk preparation began.");
            return (int)ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(ex, "Unhandled top-level failure.");
            Console.Error.WriteLine($"BadBuilder encountered an unexpected error: {ex.Message}");
            Console.Error.WriteLine($"Diagnostics were written to {logPath}");
            return (int)ExitCode.UnhandledFailure;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

internal enum ExitCode
{
    Success = 0,
    UnhandledFailure = 1,
    InvalidArguments = 2,
    UnsupportedPlatform = 3,
    PrivilegeError = 4,
    Cancelled = 130,
}
