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
    public void Parse_ExcludesSeparatelyMountedHomeDirectory()
    {
        const string home = "/home/alice";

        LinuxInventory inventory = LinuxDiskInventory.Parse(
            BuildLsblk(home),
            BuildFindmnt("/dev/sdb1", home),
            "Filename Type Size Used Priority\n",
            [home]);

        Assert.Empty(inventory.EligibleDisks);
    }

    [Fact]
    public void Parse_ExcludesSeparatelyMountedHomeDirectoryFromLsblkFallback()
    {
        const string home = "/home/alice";

        LinuxInventory inventory = LinuxDiskInventory.Parse(
            BuildLsblk(home),
            "{\"filesystems\":[{\"source\":\"unmapped-root\",\"target\":\"/\"}]}",
            "Filename Type Size Used Priority\n",
            [home]);

        Assert.Empty(inventory.EligibleDisks);
    }

    [Fact]
    public void Parse_ExcludesDiskBackingSymlinkedCacheDirectory()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = Path.Combine(Path.GetTempPath(), $"badbuilder-protection-{Guid.NewGuid():N}");
        string realMount = Path.Combine(root, "real");
        string cacheLink = Path.Combine(root, "cache");
        Directory.CreateDirectory(realMount);
        try
        {
            Directory.CreateSymbolicLink(cacheLink, realMount);

            LinuxInventory inventory = LinuxDiskInventory.Parse(
                BuildLsblk(realMount),
                BuildFindmnt("/dev/sdb1", realMount),
                "Filename Type Size Used Priority\n",
                [cacheLink]);

            Assert.Empty(inventory.EligibleDisks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Parse_KeepsOrdinaryUsbOutsideProtectedPathsEligible()
    {
        LinuxInventory inventory = LinuxDiskInventory.Parse(
            BuildLsblk("/media/usb"),
            BuildFindmnt("/dev/sdb1", "/media/usb"),
            "Filename Type Size Used Priority\n",
            ["/workspace", "/home/alice"]);

        DiskInfo disk = Assert.Single(inventory.EligibleDisks);
        Assert.Equal("/dev/sdb", disk.DevicePath);
    }

    [Fact]
    public void UserHome_ParsesCallerHomeFromPasswdFixture()
    {
        const string passwd =
            """
            root:x:0:0:root:/root:/bin/bash
            alice:x:1000:1000:Alice:/home/alice:/bin/bash
            bob:x:1001:1001:Bob:/srv/bob:/bin/sh
            nohome:x:1002:1002:No Home::/bin/false
            malformed line
            """;

        Assert.Equal("/home/alice", LinuxUserHome.ParsePasswdHome(passwd, 1000));
        Assert.Equal("/srv/bob", LinuxUserHome.ParsePasswdHome(passwd, 1001));
        Assert.Null(LinuxUserHome.ParsePasswdHome(passwd, 1002));
        Assert.Null(LinuxUserHome.ParsePasswdHome(passwd, 4242));
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
        if (!OperatingSystem.IsLinux())
            return;

        string token = new('b', 32);

        Assert.Throws<ArgumentException>(() => LinuxDiskHelper.ParseRequest(["wipe"], token));
        Assert.Throws<ArgumentException>(() => LinuxDiskHelper.ParseRequest([
            "prepare", "--device", "/dev/sdb", "--fingerprint", new string('A', 64),
            "--uid", "1000", "--gid", "1000",
            "--workspace", "/work", "--cache", "/cache", "--force", "true"], token));
        // The cleanup token must never be passed on the command line.
        Assert.Throws<ArgumentException>(() => LinuxDiskHelper.ParseRequest([
            "prepare", "--device", "/dev/sdb", "--fingerprint", new string('A', 64),
            "--uid", "1000", "--gid", "1000", "--token", token,
            "--workspace", "/work", "--cache", "/cache"], token));
        // A missing or malformed stdin token is rejected.
        Assert.Throws<ArgumentException>(() => LinuxDiskHelper.ParseRequest([
            "prepare", "--device", "/dev/sdb", "--fingerprint", new string('A', 64),
            "--uid", "1000", "--gid", "1000",
            "--workspace", "/work", "--cache", "/cache"], null));
    }

    [Fact]
    public void HelperParser_AcceptsAWellFormedPrepareRequest()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string token = new('b', 32);

        HelperRequest request = LinuxDiskHelper.ParseRequest([
            "prepare", "--device", "/dev/sdb", "--fingerprint", new string('A', 64),
            "--uid", "1000", "--gid", "1000",
            "--workspace", "/work", "--cache", "/cache"], token);

        Assert.Equal("prepare", request.Operation);
        Assert.Equal("/dev/sdb", request.Device);
        Assert.Equal(token, request.Token);
        Assert.Null(request.Mount);
    }

    [Fact]
    public void Parse_ExcludesHotPlugOnlyInternalDisk()
    {
        const string json =
            """
            {
              "blockdevices": [
                {
                  "name":"/dev/sda","kname":"sda","path":"/dev/sda","type":"disk","size":1000000000,
                  "model":"Internal SATA","serial":"SATA1","tran":"sata","rm":false,"hotplug":true,"ro":false
                }
              ]
            }
            """;

        LinuxInventory inventory = LinuxDiskInventory.Parse(
            json,
            "{\"filesystems\":[]}",
            "Filename Type Size Used Priority\n",
            ["/workspace"]);

        Assert.Empty(inventory.EligibleDisks);
    }

    [Fact]
    public void Parse_ExcludesUsbDiskWithoutSerialWwnOrStableId()
    {
        const string json =
            """
            {
              "blockdevices": [
                {
                  "name":"/dev/sdb","kname":"sdb","path":"/dev/sdb","type":"disk","size":2000000000,
                  "model":"Anonymous USB","tran":"usb","rm":true,"ro":false
                }
              ]
            }
            """;

        LinuxInventory inventory = LinuxDiskInventory.Parse(
            json,
            "{\"filesystems\":[]}",
            "Filename Type Size Used Priority\n",
            ["/workspace"]);

        Assert.Empty(inventory.EligibleDisks);
    }

    [Fact]
    public void Parse_AcceptsUsbDiskIdentifiedByStableId()
    {
        const string json =
            """
            {
              "blockdevices": [
                {
                  "name":"/dev/sdb","kname":"sdb","path":"/dev/sdb","type":"disk","size":2000000000,
                  "model":"Anonymous USB","tran":"usb","rm":true,"ro":false
                }
              ]
            }
            """;

        LinuxInventory inventory = LinuxDiskInventory.Parse(
            json,
            "{\"filesystems\":[]}",
            "Filename Type Size Used Priority\n",
            ["/workspace"],
            _ => "usb-Generic_Flash_Disk-0:0");

        DiskInfo disk = Assert.Single(inventory.EligibleDisks);
        Assert.Equal("/dev/sdb", disk.DevicePath);
    }

    [Fact]
    public void Identity_StableIdSurvivesDeviceRenumbering()
    {
        DiskIdentity first = DiskIdentity.Create("/dev/sdb", "USB", null, null, 1_000_000, "usb", "usb-port-1");
        DiskIdentity second = DiskIdentity.Create("/dev/sdc", "USB", null, null, 1_000_000, "usb", "usb-port-1");

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.False(first.IsExactMatch(second));
    }

    [Fact]
    public void Parse_ExcludesEveryMemberOfProtectedMultiDeviceFilesystem()
    {
        const string json =
            """
            {
              "blockdevices": [
                {
                  "name":"/dev/sda","kname":"sda","path":"/dev/sda","type":"disk","size":1000000000,
                  "model":"Member A","serial":"A","tran":"usb","rm":true,"ro":false,"uuid":"BTRFS-1",
                  "children":[{"name":"/dev/sda1","kname":"sda1","path":"/dev/sda1","pkname":"sda","type":"part","size":900000000,"fstype":"btrfs","uuid":"BTRFS-1","mountpoints":["/"]}]
                },
                {
                  "name":"/dev/sdb","kname":"sdb","path":"/dev/sdb","type":"disk","size":2000000000,
                  "model":"Member B","serial":"B","tran":"usb","rm":true,"ro":false,"uuid":"BTRFS-1",
                  "children":[{"name":"/dev/sdb1","kname":"sdb1","path":"/dev/sdb1","pkname":"sdb","type":"part","size":1900000000,"fstype":"btrfs","uuid":"BTRFS-1","mountpoints":[]}]
                }
              ]
            }
            """;

        LinuxInventory inventory = LinuxDiskInventory.Parse(
            json,
            "{\"filesystems\":[{\"source\":\"/dev/sda1\",\"target\":\"/\"}]}",
            "Filename Type Size Used Priority\n",
            ["/workspace"]);

        Assert.Empty(inventory.EligibleDisks);
    }

    [Fact]
    public void Parse_ExcludesEveryMemberOfProtectedMdArray()
    {
        const string json =
            """
            {
              "blockdevices": [
                {
                  "name":"/dev/sda","kname":"sda","path":"/dev/sda","type":"disk","size":1000000000,
                  "model":"Member A","serial":"A","tran":"usb","rm":true,"ro":false,"mduuid":"MD-1",
                  "children":[{"name":"/dev/md0","kname":"md0","path":"/dev/md0","pkname":"sda","type":"raid1","size":900000000,"fstype":"ext4","mountpoints":["/"]}]
                },
                {
                  "name":"/dev/sdb","kname":"sdb","path":"/dev/sdb","type":"disk","size":2000000000,
                  "model":"Member B","serial":"B","tran":"usb","rm":true,"ro":false,"mduuid":"MD-1"
                }
              ]
            }
            """;

        LinuxInventory inventory = LinuxDiskInventory.Parse(
            json,
            "{\"filesystems\":[{\"source\":\"/dev/md0\",\"target\":\"/\"}]}",
            "Filename Type Size Used Priority\n",
            ["/workspace"]);

        Assert.Empty(inventory.EligibleDisks);
    }

    [Fact]
    public void Parse_FailsClosedWhenFindmntOutputIsMalformed()
    {
        Assert.Throws<InvalidDataException>(() => LinuxDiskInventory.Parse(
            BuildLsblk("/media/usb"),
            "{\"mounts\":[]}",
            "Filename Type Size Used Priority\n",
            ["/workspace"]));
    }

    [Fact]
    public void PasswdHomes_ReturnsEveryQualifiedHome()
    {
        const string passwd =
            """
            root:x:0:0:root:/root:/bin/bash
            alice:x:1000:1000:Alice:/home/alice:/bin/bash
            bob:x:1001:1001:Bob:/srv/bob:/bin/sh
            nohome:x:1002:1002:No Home::/bin/false
            relative:x:1003:1003:Relative:relative/path:/bin/false
            """;

        IReadOnlyList<string> homes = LinuxUserHome.ParsePasswdHomes(passwd);

        Assert.Equal(["/root", "/home/alice", "/srv/bob"], homes);
    }

    private static string BuildLsblk(string mountPoint) =>
        $$"""
        {
          "blockdevices": [
            {
              "name":"/dev/sdb","kname":"sdb","path":"/dev/sdb","type":"disk","size":2000000000,
              "model":"Safe USB","serial":"SAFE","tran":"usb","rm":true,"hotplug":true,"ro":false,
              "children":[{"name":"/dev/sdb1","kname":"sdb1","path":"/dev/sdb1","pkname":"sdb","type":"part","size":1900000000,"fstype":"vfat","mountpoints":["{{mountPoint}}"]}]
            }
          ]
        }
        """;

    private static string BuildFindmnt(string source, string target) =>
        $$"""{"filesystems":[{"source":"{{source}}","target":"{{target}}"}]}""";
}
