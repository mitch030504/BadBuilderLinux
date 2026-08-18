using System.Buffers.Binary;
using System.Text;
using DiscUtils.Fat;
using DiscUtils.Partitions;
using DiscUtils.Raw;
using DiscUtils.Streams;

namespace BadBuilder.Services.Disks;

internal static class DiskFormatter
{
    // DiscUtils deliberately selects FAT16 below 1,048,576 partition sectors.
    // A 1 GiB whole disk leaves ample room for the MBR alignment while keeping
    // the resulting partition above that FAT32 threshold.
    internal const long MinimumDiskSize = 1L * 1024 * 1024 * 1024;
    // DiscUtils' partition formatter accepts a signed 32-bit sector count.
    internal const long MaximumDiskSize = (long)int.MaxValue * 512;

    internal static bool IsSupportedDiskSize(long diskSize) =>
        diskSize >= MinimumDiskSize && diskSize <= MaximumDiskSize;

    internal static void FormatFat32(Stream stream, long diskSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
            throw new ArgumentException("The disk stream must be readable, writable, and seekable.", nameof(stream));
        if (!IsSupportedDiskSize(diskSize))
            throw new DiskSafetyException("The selected disk is outside the supported 1 GiB to 1 TiB MBR/FAT32 size range.");

        using LengthBoundStream bounded = new(stream, diskSize, leaveOpen: true);
        using Disk disk = new(bounded, Ownership.None);

        BiosPartitionTable.Initialize(disk, WellKnownPartitionType.WindowsFat);
        using (FatFileSystem fileSystem = FatFileSystem.FormatPartition(disk, 0, "BADUPDATE  "))
        {
        }
        FinalizeFat32Metadata(disk);
        bounded.Flush();
    }

    private static void FinalizeFat32Metadata(Disk disk)
    {
        byte[] label = Encoding.ASCII.GetBytes("BADUPDATE  ");
        PartitionTable partitions = disk.Partitions
            ?? throw new InvalidDataException("DiscUtils did not create a partition table.");
        if (partitions.Count != 1)
            throw new InvalidDataException("DiscUtils did not create exactly one partition.");
        using Stream partition = partitions[0].Open();
        Span<byte> bootSector = stackalloc byte[512];
        partition.ReadExactly(bootSector);

        if (!bootSector[82..90].SequenceEqual("FAT32   "u8))
            throw new InvalidDataException("DiscUtils did not create the expected FAT32 boot sector.");
        if (bootSector[510] != 0x55 || bootSector[511] != 0xAA)
            throw new InvalidDataException("The FAT32 formatter returned an invalid boot-sector signature.");

        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector[11..13]);
        ushort reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bootSector[14..16]);
        ushort fsInfoSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector[48..50]);
        ushort backupBootSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector[50..52]);
        if (bytesPerSector != 512)
            throw new InvalidDataException("The FAT32 formatter returned an invalid sector size.");
        if (fsInfoSector == 0 || fsInfoSector >= reservedSectors ||
            backupBootSector == 0 || backupBootSector >= reservedSectors ||
            backupBootSector + fsInfoSector >= reservedSectors)
        {
            throw new InvalidDataException("The FAT32 formatter returned invalid backup metadata locations.");
        }

        // Ensure both copies advertise the intended label. DiscUtils writes the
        // primary BPB but leaves its declared FAT32 backup/FSInfo sectors empty.
        bootSector[66] = 0x29;
        label.CopyTo(bootSector[71..82]);
        WriteSector(partition, 0, bootSector);
        WriteSector(partition, backupBootSector, bootSector);

        Span<byte> fsInfo = stackalloc byte[512];
        fsInfo.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(fsInfo[0..4], 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(fsInfo[484..488], 0x61417272);
        BinaryPrimitives.WriteUInt32LittleEndian(fsInfo[488..492], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(fsInfo[492..496], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(fsInfo[508..512], 0xAA550000);
        WriteSector(partition, fsInfoSector, fsInfo);
        WriteSector(partition, backupBootSector + fsInfoSector, fsInfo);
        partition.Flush();
    }

    private static void WriteSector(Stream partition, int sector, ReadOnlySpan<byte> contents)
    {
        long offset = checked((long)sector * contents.Length);
        if (offset + contents.Length > partition.Length)
            throw new InvalidDataException("The FAT32 metadata lies outside the partition.");
        partition.Position = offset;
        partition.Write(contents);
    }

    private sealed class LengthBoundStream(Stream inner, long length, bool leaveOpen) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
