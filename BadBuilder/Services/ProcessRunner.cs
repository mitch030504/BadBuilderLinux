using System.ComponentModel;
using System.Diagnostics;
using BadBuilder.Services.Disks;

namespace BadBuilder.Services;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    internal void EnsureSuccess(string description)
    {
        if (ExitCode != 0)
        {
            string details = string.IsNullOrWhiteSpace(StandardError) ? "No diagnostic output was returned." : StandardError.Trim();
            throw new IOException($"{description} failed with exit code {ExitCode}: {details}");
        }
    }
}

internal static class ProcessRunner
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(5);

    private static readonly string[] SystemSearchPath =
    [
        "/usr/sbin", "/usr/bin", "/sbin", "/bin", "/usr/local/sbin", "/usr/local/bin",
    ];

    internal static ProcessStartInfo CreateStartInfo(
        string fileName,
        IEnumerable<string> arguments,
        bool redirectOutput = true)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (OperatingSystem.IsLinux() && LinuxNative.GetEffectiveUserId() == 0)
        {
            // Running as root: ignore anything the caller could have influenced.
            startInfo.Environment["PATH"] = string.Join(':', SystemSearchPath);
            foreach (string variable in new[] { "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "LD_DEBUG" })
                startInfo.Environment.Remove(variable);
        }

        return startInfo;
    }

    internal static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        bool redirectOutput = true,
        TimeSpan? timeout = null,
        string? standardInput = null)
    {
        ProcessStartInfo startInfo = CreateStartInfo(fileName, arguments, redirectOutput);
        if (standardInput is not null)
            startInfo.RedirectStandardInput = true;

        TimeSpan effectiveTimeout = timeout ?? DefaultCommandTimeout;

        try
        {
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
                throw new IOException($"Failed to start required command '{fileName}'.");

            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(effectiveTimeout);

            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutSource.Token);
                await process.StandardInput.FlushAsync(timeoutSource.Token);
                process.StandardInput.Close();
            }

            Task<string> stdout = redirectOutput
                ? process.StandardOutput.ReadToEndAsync(timeoutSource.Token)
                : Task.FromResult(string.Empty);
            Task<string> stderr = redirectOutput
                ? process.StandardError.ReadToEndAsync(timeoutSource.Token)
                : Task.FromResult(string.Empty);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None);
                    await Task.WhenAll(stdout, stderr);
                }
                catch (Exception)
                {
                    // The command was killed; its output is no longer needed.
                }

                if (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Command '{fileName}' did not finish within {effectiveTimeout}.");
                }

                throw;
            }

            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (Win32Exception ex)
        {
            throw new IOException($"Required command '{fileName}' could not be started. Ensure it is installed and on PATH.", ex);
        }
    }

    internal static string RequireExecutable(string command)
    {
        if (Path.IsPathFullyQualified(command))
            return ValidateExecutable(command);

        IEnumerable<string> directories = OperatingSystem.IsLinux() && LinuxNative.GetEffectiveUserId() == 0
            ? SystemSearchPath
            : TrustedPathDirectories();

        foreach (string directory in directories)
        {
            string candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
                return ValidateExecutable(candidate);
        }

        throw new IOException($"Required command '{command}' is not installed or is not on a trusted PATH.");
    }

    internal static string RequireSystemExecutable(string command)
    {
        foreach (string directory in SystemSearchPath)
        {
            string candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
                return ValidateExecutable(candidate);
        }

        throw new IOException($"Required command '{command}' is not installed in a system directory.");
    }

    private static IEnumerable<string> TrustedPathDirectories()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string entry in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // A relative entry (including "." and "") lets the working directory
            // shadow a system binary.
            if (Path.IsPathFullyQualified(entry))
                yield return entry;
        }
    }

    private static string ValidateExecutable(string path)
    {
        FileInfo info = new(path);
        if (!info.Exists)
            throw new IOException($"Required command '{path}' does not exist.");
        if ((info.Attributes & FileAttributes.Directory) != 0)
            throw new IOException($"Required command '{path}' is a directory.");

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                throw new IOException(
                    $"Required command '{path}' is writable by other users and will not be executed.");
            }
            if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                throw new IOException($"Required command '{path}' is not executable.");
        }

        return path;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
