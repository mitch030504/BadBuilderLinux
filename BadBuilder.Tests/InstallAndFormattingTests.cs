using System.Text;
using BadBuilder.Configuration;
using BadBuilder.Services;
using BadBuilder.Services.Disks;
using DiscUtils.Fat;
using DiscUtils.Raw;
using DiscUtils.Streams;
using DiscUtils.Partitions;

namespace BadBuilder.Tests;

public sealed class InstallAndFormattingTests
{
    [Fact]
    public void XboxPath_UsesBackslashesAndRejectsTraversal()
    {
        Assert.Equal(@"Usb:\Apps\Aurora\Aurora.xex", XboxPath.Combine("Usb", "Apps", "Aurora", "Aurora.xex"));
        Assert.Throws<InvalidDataException>(() => XboxPath.Combine("Usb", "Apps", "../escape.xex"));
    }

    [Fact]
    public async Task LaunchIni_UpdatesOnlyPathsSectionAndPreservesBomAndCrlf()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "launch.ini");
        string original = "[Other]\r\nDefault = leave-me\r\n[Paths]\r\nfoo = bar\r\n[Plugins]\r\nplugin1 = Usb:\\x.xex\r\n";
        byte[] preamble = Encoding.UTF8.GetPreamble();
        await File.WriteAllBytesAsync(path, [..preamble, ..Encoding.UTF8.GetBytes(original)]);

        bool updated = await LaunchIniService.UpdateDefaultAsync(
            path, @"Usb:\Apps\Aurora\Aurora.xex", CancellationToken.None);

        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.True(updated);
        Assert.True(bytes.AsSpan().StartsWith(preamble));
        string text = Encoding.UTF8.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
        Assert.Contains("[Other]\r\nDefault = leave-me\r\n", text, StringComparison.Ordinal);
        Assert.Contains("[Paths]\r\nfoo = bar\r\nDefault = Usb:\\Apps\\Aurora\\Aurora.xex\r\n[Plugins]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchIni_PreservesNonUtf8Bytes()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "launch.ini");
        byte[] original = [0x5B, 0x50, 0x61, 0x74, 0x68, 0x73, 0x5D, 0x0D, 0x0A, 0x3B, 0x20, 0xE9, 0x0D, 0x0A];
        await File.WriteAllBytesAsync(path, original);

        await LaunchIniService.UpdateDefaultAsync(path, @"Usb:\default.xex", CancellationToken.None);

        Assert.Contains((byte)0xE9, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InstallPlan_ValidatesSourcesBeforeCopyAndExecutesCompletePlan()
    {
        using TemporaryDirectory temporary = new();
        string staging = Path.Combine(temporary.Path, "staging");
        string target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(Path.Combine(staging, "payload"));
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(staging, "payload", "default.xex"), "xex");
        ArtifactDefinition artifact = new(
            "artifact", "Artifact", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, "BadUpdatePayload", "payload")],
            Layout: new ArchiveLayout(["payload/default.xex"]));

        InstallPlan plan = InstallService.BuildPlan(
            [(artifact, staging)],
            [new InstallOperation(InstallOperationKind.WriteFile, "name.txt", Contents: "USB")],
            128L * 1024 * 1024);
        await InstallService.ExecuteAsync(plan, target, CancellationToken.None);

        Assert.Equal("xex", await File.ReadAllTextAsync(Path.Combine(target, "BadUpdatePayload", "default.xex")));
        Assert.Equal("USB", await File.ReadAllTextAsync(Path.Combine(target, "name.txt")));
    }

    [Fact]
    public void InstallPlan_RejectsMissingExpectedSource()
    {
        using TemporaryDirectory temporary = new();
        ArtifactDefinition artifact = new(
            "artifact", "Artifact", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", "missing")]);

        Assert.Throws<DirectoryNotFoundException>(() => InstallService.BuildPlan(
            [(artifact, temporary.Path)], [], 128L * 1024 * 1024));
    }

    [Fact]
    public async Task InstallPlan_AllowsOnlyAnExplicitExpectedOverlay()
    {
        using TemporaryDirectory temporary = new();
        string first = Path.Combine(temporary.Path, "first");
        string second = Path.Combine(temporary.Path, "second");
        string target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(Path.Combine(first, "BadUpdatePayload"));
        Directory.CreateDirectory(Path.Combine(second, "BadUpdatePayload"));
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(first, "BadUpdatePayload", "default.xex"), "exploit");
        await File.WriteAllTextAsync(Path.Combine(second, "BadUpdatePayload", "default.xex"), "bootstrap");

        ArtifactDefinition exploit = new(
            "exploit", "Exploit", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")]);
        ArtifactDefinition bootstrap = new(
            "bootstrap", "Bootstrap", "Test", string.Empty, null,
            [new InstallOperation(
                InstallOperationKind.CopyDirectory,
                ".",
                ".",
                AllowedOverwritePaths: ["BadUpdatePayload/default.xex"])]);

        InstallPlan plan = InstallService.BuildPlan(
            [(exploit, first), (bootstrap, second)], [], 128L * 1024 * 1024);
        await InstallService.ExecuteAsync(plan, target, CancellationToken.None);

        Assert.Equal("bootstrap", await File.ReadAllTextAsync(Path.Combine(target, "BadUpdatePayload", "default.xex")));
    }

    [Fact]
    public void InstallPlan_RejectsUndeclaredAndMissingExpectedOverlays()
    {
        using TemporaryDirectory temporary = new();
        string first = Path.Combine(temporary.Path, "first");
        string second = Path.Combine(temporary.Path, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "same.bin"), "one");
        File.WriteAllText(Path.Combine(second, "same.bin"), "two");

        ArtifactDefinition firstArtifact = new(
            "first", "First", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")]);
        ArtifactDefinition undeclared = new(
            "second", "Second", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")]);
        ArtifactDefinition missingExpected = new(
            "third", "Third", "Test", string.Empty, null,
            [new InstallOperation(
                InstallOperationKind.CopyDirectory,
                ".",
                ".",
                AllowedOverwritePaths: ["different.bin"])]);

        Assert.Throws<InvalidDataException>(() => InstallService.BuildPlan(
            [(firstArtifact, first), (undeclared, second)], [], 128L * 1024 * 1024));
        Assert.Throws<InvalidDataException>(() => InstallService.BuildPlan(
            [(firstArtifact, first), (missingExpected, second)], [], 128L * 1024 * 1024));
    }

    [Fact]
    public void FormatterSizePolicy_StaysWithinGuaranteedFat32Range()
    {
        Assert.False(DiskFormatter.IsSupportedDiskSize(DiskFormatter.MinimumDiskSize - 1));
        Assert.True(DiskFormatter.IsSupportedDiskSize(DiskFormatter.MinimumDiskSize));
        Assert.True(DiskFormatter.IsSupportedDiskSize(DiskFormatter.MaximumDiskSize));
        Assert.False(DiskFormatter.IsSupportedDiskSize(DiskFormatter.MaximumDiskSize + 1));
    }

    [Fact]
    public void Formatter_CreatesMbrFat32LabelAndReadableFiles()
    {
        using TemporaryDirectory temporary = new();
        string imagePath = Path.Combine(temporary.Path, "disk.img");
        const long imageSize = 1L * 1024 * 1024 * 1024;
        using (FileStream create = new(imagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            create.SetLength(imageSize);

        using (FileStream stream = new(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            DiskFormatter.FormatFat32(stream, imageSize);

        byte[] sector = new byte[512];
        using (FileStream raw = File.OpenRead(imagePath))
            Assert.Equal(sector.Length, raw.Read(sector));
        Assert.Equal(0x55, sector[510]);
        Assert.Equal(0xAA, sector[511]);

        using FileStream image = new(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using Disk disk = new(image, Ownership.None);
        PartitionTable partitions = Assert.IsAssignableFrom<PartitionTable>(disk.Partitions);
        Assert.Equal(1, partitions.Count);
        using Stream partition = partitions[0].Open();
        byte[] primaryBootSector = new byte[512];
        Assert.Equal(primaryBootSector.Length, partition.Read(primaryBootSector));
        Assert.Equal("FAT32", Encoding.ASCII.GetString(primaryBootSector, 82, 8).Trim());
        byte[] bootSectorLabel = new byte[11];
        partition.Position = 71;
        Assert.Equal(bootSectorLabel.Length, partition.Read(bootSectorLabel));
        Assert.Equal("BADUPDATE", Encoding.ASCII.GetString(bootSectorLabel).Trim());
        partition.Position = 50;
        Span<byte> backupBytes = stackalloc byte[2];
        partition.ReadExactly(backupBytes);
        ushort backupSector = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(backupBytes);
        partition.Position = backupSector * 512L + 71;
        Assert.Equal(bootSectorLabel.Length, partition.Read(bootSectorLabel));
        Assert.Equal("BADUPDATE", Encoding.ASCII.GetString(bootSectorLabel).Trim());
        byte[] backupBootSector = new byte[512];
        partition.Position = backupSector * 512L;
        Assert.Equal(backupBootSector.Length, partition.Read(backupBootSector));
        Assert.Equal(primaryBootSector, backupBootSector);

        ushort fsInfoSector = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(primaryBootSector.AsSpan(48, 2));
        byte[] fsInfo = new byte[512];
        partition.Position = fsInfoSector * 512L;
        Assert.Equal(fsInfo.Length, partition.Read(fsInfo));
        Assert.Equal(0x41615252U, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(0, 4)));
        Assert.Equal(0x61417272U, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(484, 4)));
        Assert.Equal(0xAA550000U, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(508, 4)));
        byte[] backupFsInfo = new byte[512];
        partition.Position = (backupSector + fsInfoSector) * 512L;
        Assert.Equal(backupFsInfo.Length, partition.Read(backupFsInfo));
        Assert.Equal(fsInfo, backupFsInfo);

        partition.Position = 0;
        using FatFileSystem fat = new(partition);
        Assert.Equal(FatType.Fat32, fat.FatVariant);
        Assert.Equal("BADUPDATE", fat.VolumeLabel.Trim());
        Assert.True(fat.AvailableSpace > 900L * 1024 * 1024);

        byte[] contents = Encoding.ASCII.GetBytes("installed");
        using (Stream file = fat.OpenFile("ready.txt", FileMode.Create, FileAccess.ReadWrite))
            file.Write(contents);
        using Stream readBack = fat.OpenFile("ready.txt", FileMode.Open, FileAccess.Read);
        byte[] actual = new byte[contents.Length];
        Assert.Equal(actual.Length, readBack.Read(actual));
        Assert.Equal(contents, actual);
    }
}
