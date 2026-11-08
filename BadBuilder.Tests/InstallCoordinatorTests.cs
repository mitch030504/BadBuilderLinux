using BadBuilder.Application;
using BadBuilder.Services;
using BadBuilder.Services.Disks;
using BadBuilder.UI;

namespace BadBuilder.Tests;

public sealed class InstallCoordinatorTests
{
    private static readonly InstallPlan EmptyPlan = new([], 0);

    [Fact]
    public async Task HappyPath_RevalidatesPreparesCopiesAndFinalizesInOrder()
    {
        FakeDiskBackend backend = new();
        RecordingUserInterface ui = new("/dev/sdb");
        FakeCleanupStore store = new();
        List<string> calls = [];

        backend.OnRevalidate = disk =>
        {
            calls.Add("revalidate");
            return disk;
        };
        backend.OnPrepare = _ =>
        {
            calls.Add("prepare");
            return backend.Prepared;
        };
        backend.OnFinalize = _ =>
        {
            calls.Add("finalize");
            return new FinalizeResult(true);
        };

        InstallCoordinator coordinator = new(backend, ui, store, (_, root, _) =>
        {
            calls.Add($"copy:{root}");
            return Task.CompletedTask;
        });

        InstallExecutionResult result = await coordinator.ExecuteAsync(
            backend.Disk,
            EmptyPlan,
            CancellationToken.None);

        Assert.Equal(
            ["revalidate", "prepare", "revalidate", "copy:/run/badbuilder/1000/token", "finalize"],
            calls);
        Assert.True(result.CopiedSuccessfully);
        Assert.True(result.FormatCompleted);
        Assert.True(result.FinalizedSuccessfully);
        Assert.False(result.CancelledBeforePrepare);
        Assert.Null(result.Error);
        Assert.True(store.Cleared);
    }

    [Fact]
    public async Task ConfirmationMismatch_NeverPreparesOrFormats()
    {
        FakeDiskBackend backend = new();
        RecordingUserInterface ui = new("/dev/sdb1");
        FakeCleanupStore store = new();
        InstallCoordinator coordinator = new(backend, ui, store, (_, _, _) => Task.CompletedTask);

        InstallExecutionResult result = await coordinator.ExecuteAsync(
            backend.Disk,
            EmptyPlan,
            CancellationToken.None);

        Assert.False(backend.PrepareCalled);
        Assert.False(backend.FinalizeCalled);
        Assert.False(result.FormatCompleted);
        Assert.False(result.CopiedSuccessfully);
        Assert.Contains(ui.Warnings, warning => warning.Contains("did not match", StringComparison.Ordinal));
        Assert.Equal(1, ui.Pauses);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/dev/sdb ")]
    public async Task ConfirmationVariants_NeverPrepare(string confirmation)
    {
        FakeDiskBackend backend = new();
        InstallCoordinator coordinator = new(
            backend,
            new RecordingUserInterface(confirmation),
            new FakeCleanupStore(),
            (_, _, _) => Task.CompletedTask);

        InstallExecutionResult result = await coordinator.ExecuteAsync(
            backend.Disk,
            EmptyPlan,
            CancellationToken.None);

        Assert.False(backend.PrepareCalled);
        Assert.False(result.FormatCompleted);
    }

    [Fact]
    public async Task ConfirmationCaseChange_IsRejectedOnCaseSensitivePlatforms()
    {
        // Windows device paths are compared case-insensitively by design.
        if (OperatingSystem.IsWindows())
            return;

        FakeDiskBackend backend = new();
        InstallCoordinator coordinator = new(
            backend,
            new RecordingUserInterface("/DEV/SDB"),
            new FakeCleanupStore(),
            (_, _, _) => Task.CompletedTask);

        InstallExecutionResult result = await coordinator.ExecuteAsync(
            backend.Disk,
            EmptyPlan,
            CancellationToken.None);

        Assert.False(backend.PrepareCalled);
        Assert.False(result.FormatCompleted);
    }

    [Fact]
    public async Task CancellationBeforePrepare_ReturnsWithoutFormatting()
    {
        FakeDiskBackend backend = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        backend.OnRevalidate = disk =>
        {
            cancellation.Token.ThrowIfCancellationRequested();
            return disk;
        };

        InstallCoordinator coordinator = new(
            backend,
            new RecordingUserInterface("/dev/sdb"),
            new FakeCleanupStore(),
            (_, _, _) => Task.CompletedTask);

        InstallExecutionResult result = await coordinator.ExecuteAsync(
            backend.Disk,
            EmptyPlan,
            cancellation.Token);

        Assert.True(result.CancelledBeforePrepare);
        Assert.False(backend.PrepareCalled);
        Assert.False(result.FormatCompleted);
    }

    [Fact]
    public async Task PrepareFailure_PropagatesAndSkipsFinalize()
    {
        FakeDiskBackend backend = new()
        {
            OnPrepare = _ => throw new IOException("format failed"),
        };
        FakeCleanupStore store = new();
        InstallCoordinator coordinator = new(
            backend,
            new RecordingUserInterface("/dev/sdb"),
            store,
            (_, _, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() => coordinator.ExecuteAsync(
            backend.Disk,
            EmptyPlan,
            CancellationToken.None));

        Assert.False(backend.FinalizeCalled);
        Assert.Null(store.Stored);
    }

    [Fact]
    public async Task CopyFailure_StillFinalizesAndReportsThatTheDiskWasFormatted()
    {
        FakeDiskBackend backend = new();
        FakeCleanupStore store = new();
        InstallCoordinator coordinator = new(
            backend,
            new RecordingUserInterface("/dev/sdb"),
            store,
            (_, _, _) => throw new IOException("copy failed"));

        InstallExecutionResult result = await coordinator.ExecuteAsync(
            backend.Disk,
            EmptyPlan,
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.True(result.FormatCompleted);
        Assert.False(result.CopiedSuccessfully);
        Assert.True(backend.FinalizeCalled);
        Assert.True(result.FinalizedSuccessfully);
    }

    [Fact]
    public async Task FinalizeFailure_KeepsTheCleanupRecord()
    {
        FakeDiskBackend backend = new()
        {
            OnFinalize = _ => new FinalizeResult(false, StillMounted: true, Error: "busy"),
        };
        FakeCleanupStore store = new();
        RecordingUserInterface ui = new("/dev/sdb");
        InstallCoordinator coordinator = new(backend, ui, store, (_, _, _) => Task.CompletedTask);

        InstallExecutionResult result = await coordinator.ExecuteAsync(
            backend.Disk,
            EmptyPlan,
            CancellationToken.None);

        Assert.False(result.FinalizedSuccessfully);
        Assert.False(store.Cleared);
        Assert.NotNull(store.Stored);
        Assert.Contains(ui.Warnings, warning => warning.Contains("remains mounted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanupRecordSaveFailure_StillCopies()
    {
        FakeDiskBackend backend = new();
        FakeCleanupStore store = new() { FailSave = true };
        RecordingUserInterface ui = new("/dev/sdb");
        bool copied = false;
        InstallCoordinator coordinator = new(backend, ui, store, (_, _, _) =>
        {
            copied = true;
            return Task.CompletedTask;
        });

        InstallExecutionResult result = await coordinator.ExecuteAsync(
            backend.Disk,
            EmptyPlan,
            CancellationToken.None);

        Assert.True(copied);
        Assert.True(result.CopiedSuccessfully);
        Assert.Contains(ui.Warnings, warning => warning.Contains("could not be saved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FinalizeClearFailure_ReportsSuccessButWarns()
    {
        FakeDiskBackend backend = new();
        FakeCleanupStore store = new() { FailClear = true };
        RecordingUserInterface ui = new("/dev/sdb");
        InstallCoordinator coordinator = new(backend, ui, store, (_, _, _) => Task.CompletedTask);

        InstallExecutionResult result = await coordinator.ExecuteAsync(
            backend.Disk,
            EmptyPlan,
            CancellationToken.None);

        Assert.True(result.FinalizedSuccessfully);
        Assert.Contains(ui.Warnings, warning => warning.Contains("could not be removed", StringComparison.Ordinal));
    }

    private sealed class FakeDiskBackend : IDiskBackend
    {
        internal FakeDiskBackend()
        {
            Disk = CreateDisk();
            Prepared = new PreparedTarget(
                "/run/badbuilder/1000/token",
                Disk.Identity,
                "linux",
                "token",
                RequiresFinalize: true);
            OnPrepare = _ => Prepared;
        }

        internal DiskInfo Disk { get; }
        internal PreparedTarget Prepared { get; }
        internal Func<DiskInfo, DiskInfo> OnRevalidate { get; set; } = disk => disk;
        internal Func<DiskIdentity, PreparedTarget> OnPrepare { get; set; }
        internal Func<PreparedTarget, FinalizeResult> OnFinalize { get; set; } = _ => new FinalizeResult(true);
        internal bool PrepareCalled { get; private set; }
        internal bool FinalizeCalled { get; private set; }

        public Task<IReadOnlyList<DiskInfo>> EnumerateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiskInfo>>([Disk]);

        public Task<DiskInfo> RevalidateAsync(DiskIdentity selected, CancellationToken cancellationToken) =>
            Task.FromResult(OnRevalidate(Disk));

        public Task<PreparedTarget> PrepareAsync(DiskIdentity selected, CancellationToken cancellationToken)
        {
            PrepareCalled = true;
            return Task.FromResult(OnPrepare(selected));
        }

        public Task<FinalizeResult> FinalizeAsync(PreparedTarget target, CancellationToken cancellationToken)
        {
            FinalizeCalled = true;
            return Task.FromResult(OnFinalize(target));
        }
    }

    private sealed class FakeCleanupStore : IPendingCleanupStore
    {
        internal PreparedTarget? Stored { get; private set; }
        internal bool Cleared { get; private set; }
        internal bool FailSave { get; init; }
        internal bool FailClear { get; init; }

        public PreparedTarget? TryLoad() => Stored;

        public void Save(PreparedTarget target)
        {
            if (FailSave)
                throw new IOException("save failed");
            Stored = target;
        }

        public void Clear()
        {
            if (FailClear)
                throw new IOException("clear failed");
            Cleared = true;
            Stored = null;
        }
    }

    private sealed class RecordingUserInterface(string confirmation) : IUserInterface
    {
        internal List<string> Warnings { get; } = [];
        internal List<string> Infos { get; } = [];
        internal List<string> Errors { get; } = [];
        internal List<string> Successes { get; } = [];
        internal int Pauses { get; private set; }

        public void PadLine()
        {
        }

        public void WriteInfo(string message) => Infos.Add(message);
        public void WriteWarning(string message) => Warnings.Add(message);
        public void WriteError(string message) => Errors.Add(message);
        public void WriteSuccess(string message) => Successes.Add(message);
        public bool Confirm(string title, bool defaultValue = true, bool warning = false) => true;
        public string PromptText(string title, string? defaultValue = null) => confirmation;
        public void Pause(string message = "Press enter to continue...") => Pauses++;
    }

    private static DiskInfo CreateDisk() =>
        new(
            DiskIdentity.Create("/dev/sdb", "Test Disk", "SERIAL123", null, 32L * 1024 * 1024 * 1024, "usb"),
            IsRemovable: true,
            IsHotPlug: false,
            IsReadOnly: false,
            Volumes:
            [
                new VolumeInfo(
                    "/dev/sdb1",
                    "part",
                    "vfat",
                    "BADUPDATE",
                    ["/run/badbuilder/1000/token"]),
            ]);
}
