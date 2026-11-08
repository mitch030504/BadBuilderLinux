using BadBuilder.Services;

namespace BadBuilder.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public void RequireExecutable_RejectsWorldWritableFile()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "tool");
        File.WriteAllText(path, "#!/bin/sh\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        IOException error = Assert.Throws<IOException>(() => ProcessRunner.RequireExecutable(path));

        Assert.Contains("writable by other users", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireExecutable_RejectsNonExecutableFile()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "tool");
        File.WriteAllText(path, "#!/bin/sh\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        IOException error = Assert.Throws<IOException>(() => ProcessRunner.RequireExecutable(path));

        Assert.Contains("not executable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireExecutable_RejectsMissingAbsolutePath()
    {
        Assert.Throws<IOException>(() =>
            ProcessRunner.RequireExecutable($"/nonexistent/badbuilder-{Guid.NewGuid():N}"));
    }

    [Fact]
    public async Task RunAsync_TimesOutAndKillsTheChild()
    {
        if (!File.Exists("/bin/sleep"))
            return;

        await Assert.ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(
            "/bin/sleep",
            ["30"],
            CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task RunAsync_FeedsStandardInputToTheChild()
    {
        if (!File.Exists("/bin/cat"))
            return;

        ProcessResult result = await ProcessRunner.RunAsync(
            "/bin/cat",
            [],
            CancellationToken.None,
            standardInput: "cleanup-token\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("cleanup-token\n", result.StandardOutput);
    }

    [Fact]
    public void CreateStartInfo_NeverUsesAShell()
    {
        System.Diagnostics.ProcessStartInfo startInfo = ProcessRunner.CreateStartInfo(
            "/bin/echo",
            ["--", "a b", "$(touch /tmp/nope)"]);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(["--", "a b", "$(touch /tmp/nope)"], startInfo.ArgumentList);
    }
}
