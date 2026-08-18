using System.Globalization;
using System.Text.Json;
using BadBuilder.Services;
using BadBuilder.Services.Disks;

namespace BadBuilder.Tests;

public sealed class LinuxLoopIntegrationTests
{
    [Fact]
    public async Task OptInLoopDevice_PrepareCopyFlushFinalize()
    {
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("BADBUILDER_LOOP_TEST") != "1" ||
            LinuxNative.GetEffectiveUserId() != 0)
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        string image = Path.Combine(temporary.Path, "owned-loop.img");
        await using (FileStream file = new(image, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            file.SetLength(1L * 1024 * 1024 * 1024);

        string losetup = ProcessRunner.RequireExecutable("losetup");
        ProcessResult attach = await ProcessRunner.RunAsync(
            losetup, ["--find", "--show", "--partscan", "--", image], CancellationToken.None);
        attach.EnsureSuccess("Attaching the test loop device");
        string loop = attach.StandardOutput.Trim();

        try
        {
            Assert.StartsWith("/dev/loop", loop, StringComparison.Ordinal);
            ProcessResult proof = await ProcessRunner.RunAsync(
                losetup, ["--json", "--output", "NAME,BACK-FILE", "--", loop], CancellationToken.None);
            proof.EnsureSuccess("Proving loop backing file ownership");
            using (JsonDocument document = JsonDocument.Parse(proof.StandardOutput))
            {
                JsonElement device = document.RootElement.GetProperty("loopdevices").EnumerateArray().Single();
                string? backing = device.GetProperty("back-file").GetString();
                Assert.Equal(Path.GetFullPath(image), Path.GetFullPath(backing!));
            }

            // No destructive command occurs before the exact loop/backing-file proof above.
            await using (FileStream raw = new(loop, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                DiskFormatter.FormatFat32(raw, 1L * 1024 * 1024 * 1024);
                raw.Flush(flushToDisk: true);
            }

            ProcessResult reread = await ProcessRunner.RunAsync(
                ProcessRunner.RequireExecutable("blockdev"), LinuxDiskHelper.BuildBlockdevRereadArguments(loop), CancellationToken.None);
            reread.EnsureSuccess("Refreshing loop partitions");
            await ProcessRunner.RunAsync(ProcessRunner.RequireExecutable("udevadm"), ["settle", "--timeout=10"], CancellationToken.None);

            string partition = loop + "p1";
            Assert.True(File.Exists(partition), $"Expected partition {partition} was not created.");
            string mount = Path.Combine(temporary.Path, "mount");
            Directory.CreateDirectory(mount);
            ProcessResult mounted = await ProcessRunner.RunAsync(
                ProcessRunner.RequireExecutable("mount"),
                ["--options", "nodev,nosuid,noexec", "--", partition, mount],
                CancellationToken.None);
            mounted.EnsureSuccess("Mounting the test loop partition");

            try
            {
                await File.WriteAllTextAsync(Path.Combine(mount, "ready.txt"), "ready");
                ProcessResult flush = await ProcessRunner.RunAsync(
                    ProcessRunner.RequireExecutable("sync"), ["--file-system", mount], CancellationToken.None);
                flush.EnsureSuccess("Flushing the test filesystem");
            }
            finally
            {
                ProcessResult unmount = await ProcessRunner.RunAsync(
                    ProcessRunner.RequireExecutable("umount"), ["--", mount], CancellationToken.None);
                unmount.EnsureSuccess("Unmounting the test filesystem");
            }
        }
        finally
        {
            ProcessResult detach = await ProcessRunner.RunAsync(
                losetup, ["--detach", "--", loop], CancellationToken.None);
            detach.EnsureSuccess($"Detaching {loop} at {DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}");
        }
    }
}
