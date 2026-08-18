using System.ComponentModel;
using System.Diagnostics;

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

        return startInfo;
    }

    internal static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        bool redirectOutput = true)
    {
        ProcessStartInfo startInfo = CreateStartInfo(fileName, arguments, redirectOutput);

        try
        {
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
                throw new IOException($"Failed to start required command '{fileName}'.");

            Task<string> stdout = redirectOutput ? process.StandardOutput.ReadToEndAsync(cancellationToken) : Task.FromResult(string.Empty);
            Task<string> stderr = redirectOutput ? process.StandardError.ReadToEndAsync(cancellationToken) : Task.FromResult(string.Empty);

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
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
        if (Path.IsPathFullyQualified(command) && File.Exists(command))
            return command;

        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"Required command '{command}' is not installed or is not on PATH.");
    }
}
