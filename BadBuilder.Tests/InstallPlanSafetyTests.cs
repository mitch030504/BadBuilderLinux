using System.Buffers.Binary;
using System.Diagnostics;
using BadBuilder.Configuration;
using BadBuilder.Services;
using BadBuilder.Services.Disks;
using DiscUtils.Partitions;
using DiscUtils.Raw;
using DiscUtils.Streams;

namespace BadBuilder.Tests;

public sealed class InstallPlanSafetyTests
{
    private const long OneGiB = 1L * 1024 * 1024 * 1024;

    [Fact]
    public void InstallPlan_RejectsFileLargerThanFat32Limit()
    {
        using TemporaryDirectory temporary = new();
        string staging = Path.Combine(temporary.Path, "staging");
        Directory.CreateDirectory(staging);
        string oversized = Path.Combine(staging, "oversized.bin");
        using (FileStream file = new(oversized, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.SetLength(4L * 1024 * 1024 * 1024);

        ArtifactDefinition artifact = new(
            "artifact", "Artifact", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            InstallService.BuildPlan([(artifact, staging)], [], OneGiB));

        Assert.Contains("FAT32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallPlan_RejectsPlanWhoseFat32AllocationExceedsTarget()
    {
        using TemporaryDirectory temporary = new();
        string staging = Path.Combine(temporary.Path, "staging");
        Directory.CreateDirectory(staging);
        string payload = Path.Combine(staging, "payload.bin");
        using (FileStream file = new(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.SetLength(960L * 1024 * 1024);
        for (int index = 0; index < 20_000; index++)
            Directory.CreateDirectory(Path.Combine(staging, $"dir{index:D5}"));

        ArtifactDefinition artifact = new(
            "artifact", "Artifact", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")]);

        Assert.Throws<IOException>(() =>
            InstallService.BuildPlan([(artifact, staging)], [], OneGiB));
    }

    [Fact]
    public void InstallPlan_AcceptsPlanThatFitsFat32Allocation()
    {
        using TemporaryDirectory temporary = new();
        string staging = Path.Combine(temporary.Path, "staging");
        Directory.CreateDirectory(staging);
        string payload = Path.Combine(staging, "payload.bin");
        using (FileStream file = new(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.SetLength(512L * 1024 * 1024);
        for (int index = 0; index < 100; index++)
            Directory.CreateDirectory(Path.Combine(staging, $"dir{index:D3}"));

        ArtifactDefinition artifact = new(
            "artifact", "Artifact", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, ".", ".")]);

        InstallPlan plan = InstallService.BuildPlan([(artifact, staging)], [], OneGiB);

        Assert.InRange(plan.RequiredBytes, 512L * 1024 * 1024 + 1, OneGiB - 1);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(4L)]
    [InlineData(8L)]
    [InlineData(16L)]
    public void Fat32Layout_MatchesFormattedImageBootSector(long sizeGiB)
    {
        long diskSize = sizeGiB * OneGiB;
        using TemporaryDirectory temporary = new();
        string imagePath = Path.Combine(temporary.Path, "disk.img");
        using (FileStream create = new(imagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            create.SetLength(diskSize);
        using (FileStream stream = new(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            DiskFormatter.FormatFat32(stream, diskSize);

        Assert.True(DiskFormatter.TryGetFat32Layout(diskSize, out DiskFormatter.Fat32Layout layout));

        using FileStream image = new(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using Disk disk = new(image, Ownership.None);
        PartitionTable partitions = Assert.IsAssignableFrom<PartitionTable>(disk.Partitions);
        Assert.Equal(1, partitions.Count);
        using Stream partition = partitions[0].Open();
        byte[] bootSector = new byte[512];
        partition.ReadExactly(bootSector);

        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.AsSpan(11, 2));
        byte sectorsPerCluster = bootSector[13];
        ushort reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.AsSpan(14, 2));
        byte fatCount = bootSector[16];
        ushort totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.AsSpan(19, 2));
        uint totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(bootSector.AsSpan(32, 4));
        uint fatSizeSectors = BinaryPrimitives.ReadUInt32LittleEndian(bootSector.AsSpan(36, 4));

        Assert.Equal(512, bytesPerSector);
        Assert.Equal(32, reservedSectors);
        Assert.Equal(2, fatCount);
        Assert.Equal(0, totalSectors16);
        long totalSectors = totalSectors32;
        long dataSectors = totalSectors - reservedSectors - (long)fatCount * fatSizeSectors;

        Assert.Equal(layout.SectorCount, totalSectors);
        Assert.Equal(layout.SectorsPerCluster, sectorsPerCluster);
        Assert.Equal(layout.ClusterSize, sectorsPerCluster * bytesPerSector);
        Assert.Equal(layout.FatSizeSectors, fatSizeSectors);
        Assert.Equal(layout.TotalDataClusters, dataSectors / sectorsPerCluster);
    }

    [Fact]
    public void InstallPlan_RejectsFilePlacedBeneathAnotherFilePath()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => InstallService.BuildPlan(
            [],
            [
                new InstallOperation(InstallOperationKind.WriteFile, "bundle", Contents: "file"),
                new InstallOperation(InstallOperationKind.WriteFile, "bundle/child.txt", Contents: "child"),
            ],
            128L * 1024 * 1024));

        Assert.Contains("beneath file path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallPlan_RejectsDirectoryPlacedBeneathAnotherFilePath()
    {
        using TemporaryDirectory temporary = new();
        string staging = Path.Combine(temporary.Path, "staging");
        Directory.CreateDirectory(staging);

        ArtifactDefinition artifact = new(
            "artifact", "Artifact", "Test", string.Empty, null,
            [new InstallOperation(InstallOperationKind.CopyDirectory, "bundle/sub", ".")]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => InstallService.BuildPlan(
            [(artifact, staging)],
            [new InstallOperation(InstallOperationKind.WriteFile, "bundle", Contents: "file")],
            128L * 1024 * 1024));

        Assert.Contains("beneath file path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallPlan_HandlesLargeFileSetPromptly()
    {
        List<InstallOperation> operations = new(20_000);
        for (int index = 0; index < 20_000; index++)
        {
            operations.Add(new InstallOperation(
                InstallOperationKind.WriteFile,
                $"file{index:D5}.bin",
                Contents: string.Empty));
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        InstallPlan plan = InstallService.BuildPlan([], operations, OneGiB);
        stopwatch.Stop();

        Assert.Equal(20_000, plan.Actions.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Building the 20,000-file plan took {stopwatch.Elapsed}.");
    }
}
