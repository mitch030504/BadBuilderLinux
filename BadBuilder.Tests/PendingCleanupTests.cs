using System.Text.Json;
using BadBuilder.Services;
using BadBuilder.Services.Disks;

namespace BadBuilder.Tests;

public sealed class PendingCleanupTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsEveryField()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "pending-cleanup.json");
        DiskIdentity identity = CreateIdentity();
        PreparedTarget target = CreateTarget();

        PendingCleanupStore.Save(target, path);
        PreparedTarget loaded = Assert.IsType<PreparedTarget>(PendingCleanupStore.TryLoad(path));

        Assert.Equal(target.MountRoot, loaded.MountRoot);
        Assert.Equal(target.Backend, loaded.Backend);
        Assert.Equal(target.CleanupToken, loaded.CleanupToken);
        Assert.Equal(target.RequiresFinalize, loaded.RequiresFinalize);
        Assert.Equal(identity.Fingerprint, loaded.Identity.Fingerprint);
        Assert.Equal(identity.DevicePath, loaded.Identity.DevicePath);
        Assert.Equal(identity.Model, loaded.Identity.Model);
        Assert.Equal(identity.Serial, loaded.Identity.Serial);
        Assert.Equal(identity.Wwn, loaded.Identity.Wwn);
        Assert.Equal(identity.SizeBytes, loaded.Identity.SizeBytes);
        Assert.Equal(identity.Transport, loaded.Identity.Transport);
        Assert.True(identity.IsExactMatch(loaded.Identity));
        Assert.Single(Directory.GetFiles(temporary.Path));
    }

    [Fact]
    public void Save_ReplacesAnExistingRecord()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "pending-cleanup.json");
        PreparedTarget first = CreateTarget("/run/badbuilder/1000/first");
        PreparedTarget second = CreateTarget("/run/badbuilder/1000/second", new string('c', 32));

        PendingCleanupStore.Save(first, path);
        PendingCleanupStore.Save(second, path);
        PreparedTarget loaded = Assert.IsType<PreparedTarget>(PendingCleanupStore.TryLoad(path));

        Assert.Equal(second.MountRoot, loaded.MountRoot);
        Assert.Equal(second.CleanupToken, loaded.CleanupToken);
        Assert.Single(Directory.GetFiles(temporary.Path));
    }

    [Fact]
    public void Clear_RemovesTheRecordAndTryLoadReturnsNull()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "pending-cleanup.json");
        PendingCleanupStore.Save(CreateTarget(), path);
        Assert.True(PendingCleanupStore.Exists(path));

        PendingCleanupStore.Clear(path);

        Assert.False(PendingCleanupStore.Exists(path));
        Assert.Null(PendingCleanupStore.TryLoad(path));
        PendingCleanupStore.Clear(path);
    }

    [Fact]
    public void TryLoad_ReturnsNullWhenTheFileIsMissing()
    {
        using TemporaryDirectory temporary = new();
        Assert.Null(PendingCleanupStore.TryLoad(Path.Combine(temporary.Path, "pending-cleanup.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"not a record\"")]
    public void TryLoad_ReturnsNullForMalformedJson(string contents)
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "pending-cleanup.json");
        File.WriteAllText(path, contents);

        Assert.Null(PendingCleanupStore.TryLoad(path));
    }

    [Fact]
    public void TryLoad_ReturnsNullForStructurallyInvalidRecords()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "pending-cleanup.json");
        string fingerprint = new('a', 64);
        string token = new('b', 32);

        (string Fingerprint, string Token, string DevicePath, string MountRoot)[] invalidRecords =
        [
            (new string('a', 63), token, "/dev/sdb", "/mnt/usb"),
            (new string('g', 64), token, "/dev/sdb", "/mnt/usb"),
            (fingerprint, new string('b', 31), "/dev/sdb", "/mnt/usb"),
            (fingerprint, new string('g', 32), "/dev/sdb", "/mnt/usb"),
            (fingerprint, token, string.Empty, "/mnt/usb"),
            (fingerprint, token, "   ", "/mnt/usb"),
            (fingerprint, token, "/dev/sdb", string.Empty),
            (fingerprint, token, "/dev/sdb", "   "),
        ];

        foreach ((string recordFingerprint, string recordToken, string devicePath, string mountRoot) in invalidRecords)
        {
            WriteRecord(path, recordFingerprint, recordToken, devicePath, mountRoot);
            Assert.Null(PendingCleanupStore.TryLoad(path));
        }
    }

    [Fact]
    public void Save_SetsPrivateFileModeOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "pending-cleanup.json");
        PendingCleanupStore.Save(CreateTarget(), path);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path));
    }

    private static DiskIdentity CreateIdentity() => new(
        new string('a', 64),
        "/dev/sdb1",
        "Test USB Disk",
        "SERIAL-1234",
        "0x5000c5000000001",
        64L * 1024 * 1024 * 1024,
        "usb");

    private static PreparedTarget CreateTarget(
        string mountRoot = "/run/badbuilder/1000/0123456789abcdef0123456789abcdef",
        string token = "0123456789abcdef0123456789abcdef") =>
        new(mountRoot, CreateIdentity(), "linux", token, RequiresFinalize: true);

    private static void WriteRecord(
        string path,
        string fingerprint,
        string token,
        string devicePath,
        string mountRoot)
    {
        string json = JsonSerializer.Serialize(new
        {
            devicePath,
            fingerprint,
            model = "Test USB Disk",
            serial = "SERIAL-1234",
            wwn = (string?)null,
            sizeBytes = 64L * 1024 * 1024 * 1024,
            transport = "usb",
            mountRoot,
            cleanupToken = token,
            backend = "linux",
            requiresFinalize = true,
        });
        File.WriteAllText(path, json);
    }
}
