using BadBuilder.Services;
using BadBuilder.Services.Disks;

namespace BadBuilder.Tests;

public sealed class LinuxDiskInventoryTests
{
    private const string LsblkJson =
        """
        {
          "blockdevices": [
            {
              "name":"/dev/sda","kname":"sda","path":"/dev/sda","type":"disk","size":1000000000,
              "model":"System USB","serial":"SYS","tran":"usb","rm":true,"hotplug":true,"ro":false,
              "children":[{"name":"/dev/sda2","kname":"sda2","path":"/dev/sda2","pkname":"sda","type":"part","size":900000000,"fstype":"ext4","mountpoints":["/"]}]
            },
            {
              "name":"/dev/sdb","kname":"sdb","path":"/dev/sdb","type":"disk","size":2000000000,
              "model":"Safe USB","serial":"SAFE","wwn":"WWN-SAFE","tran":"usb","rm":true,"hotplug":true,"ro":false,
              "children":[{"name":"/dev/sdb1","kname":"sdb1","path":"/dev/sdb1","pkname":"sdb","type":"part","size":1900000000,"fstype":"vfat","label":"OLD","mountpoints":["/media/usb"]}]
            },
            {
              "name":"/dev/sdc","kname":"sdc","path":"/dev/sdc","type":"disk","size":3000000000,
              "model":"Read only","serial":"RO","tran":"usb","rm":true,"hotplug":true,"ro":true
            },
            {
              "name":"/dev/nvme0n1","kname":"nvme0n1","path":"/dev/nvme0n1","type":"disk","size":4000000000,
              "model":"Internal","serial":"NVME","tran":"nvme","rm":false,"hotplug":false,"ro":false
            }
          ]
        }
        """;

    private const string FindmntJson =
        """
        {"filesystems":[{"source":"/dev/sda2","target":"/","children":[{"source":"/dev/sda2[/home]","target":"/home"}]}]}
        """;

    [Fact]
    public void Parse_OffersOnlyWritableNonSystemUsbDisk()
    {
        LinuxInventory inventory = LinuxDiskInventory.Parse(LsblkJson, FindmntJson, "Filename Type Size Used Priority\n", ["/workspace"]);

        DiskInfo disk = Assert.Single(inventory.EligibleDisks);
        Assert.Equal("/dev/sdb", disk.DevicePath);
        Assert.Equal("Safe USB", disk.Name);
        Assert.Equal("SAFE", disk.Serial);
        Assert.True(disk.IsRemovable);
        VolumeInfo volume = Assert.Single(disk.Volumes);
        Assert.Equal("/media/usb", Assert.Single(volume.MountPoints));
    }

    [Fact]
    public void Parse_ExcludesAncestorOfSwap()
    {
        string swaps = "Filename Type Size Used Priority\n/dev/sdb1 partition 1 0 -2\n";

        LinuxInventory inventory = LinuxDiskInventory.Parse(LsblkJson, "{\"filesystems\":[]}", swaps, ["/workspace"]);

        Assert.DoesNotContain(inventory.EligibleDisks, disk => disk.DevicePath == "/dev/sdb");
    }

    [Fact]
    public void Parse_UsesLsblkMountsWhenFindmntSourceCannotBeMapped()
    {
        const string unmappedFindmnt = "{\"filesystems\":[{\"source\":\"unmapped-root\",\"target\":\"/\"}]}";

        LinuxInventory inventory = LinuxDiskInventory.Parse(
            LsblkJson,
            unmappedFindmnt,
            "Filename Type Size Used Priority\n",
            ["/workspace"]);

        DiskInfo disk = Assert.Single(inventory.EligibleDisks);
        Assert.Equal("/dev/sdb", disk.DevicePath);
    }

    [Fact]
    public void Identity_FingerprintIsStableButExactMatchRejectsChangedPath()
    {
        DiskIdentity original = DiskIdentity.Create("/dev/sdb", "USB", "SERIAL", null, 1_000_000, "usb");
        DiskIdentity replugged = DiskIdentity.Create("/dev/sdc", "USB", "SERIAL", null, 1_000_000, "usb");

        Assert.Equal(original.Fingerprint, replugged.Fingerprint);
        Assert.False(original.IsExactMatch(replugged));
    }

    [Fact]
    public void Commands_AreConstructedWithoutShellParsing()
    {
        const string hostile = "/dev/sdb; touch /tmp/not-run";
        System.Diagnostics.ProcessStartInfo startInfo = ProcessRunner.CreateStartInfo("umount", ["--", hostile]);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(["--", hostile], startInfo.ArgumentList);
        Assert.Contains("MOUNTPOINTS", LinuxDiskEnumerator.LsblkArguments[^1], StringComparison.Ordinal);
        Assert.Equal(["--rereadpt", "/dev/sdb"], LinuxDiskHelper.BuildBlockdevRereadArguments("/dev/sdb"));
        Assert.DoesNotContain("--", LinuxDiskHelper.BuildBlockdevRereadArguments("/dev/sdb"));
    }

    [Fact]
    public void SudoInvocation_PreservesEveryArgument()
    {
        (string fileName, IReadOnlyList<string> args) = LinuxDiskBackend.BuildSudoInvocation(
            "/usr/bin/sudo",
            ["--disk-helper", "prepare", "--device", "/dev/sdb"],
            "/usr/bin/dotnet",
            "/opt/BadBuilder.dll");

        Assert.Equal("/usr/bin/sudo", fileName);
        Assert.Equal(["--", "/usr/bin/dotnet", "/opt/BadBuilder.dll", "--disk-helper", "prepare", "--device", "/dev/sdb"], args);
    }

    [Fact]
    public void HelperParser_RejectsUnknownOperationsAndArguments()
    {
        Assert.Throws<ArgumentException>(() => LinuxDiskHelper.ParseRequest(["wipe"]));
        Assert.Throws<ArgumentException>(() => LinuxDiskHelper.ParseRequest([
            "prepare", "--device", "/dev/sdb", "--fingerprint", new string('A', 64),
            "--uid", "1000", "--gid", "1000", "--token", new string('b', 32),
            "--workspace", "/work", "--cache", "/cache", "--force", "true"]));
    }
}
