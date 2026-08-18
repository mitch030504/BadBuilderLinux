using System.Text;

namespace BadBuilder.Services;

internal static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static string? _path;

    internal static string Initialize()
    {
        Directory.CreateDirectory(AppPaths.LogRoot);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(AppPaths.LogRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _path = Path.Combine(AppPaths.LogRoot, $"badbuilder-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        using (new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Write("INFO", $"BadBuilder started on {Environment.OSVersion}; runtime {Environment.Version}.");
        return _path;
    }

    internal static void Info(string message) => Write("INFO", message);

    internal static void Error(Exception exception, string context) =>
        Write("ERROR", $"{context}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        if (_path is null)
            return;
        string line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
        lock (Sync)
        {
            try
            {
                File.AppendAllText(_path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (IOException)
            {
                // Diagnostics must not turn a handled application failure into another failure.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
