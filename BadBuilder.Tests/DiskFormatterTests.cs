using System.Text;
using BadBuilder.Services.Disks;

namespace BadBuilder.Tests;

public sealed class DiskFormatterTests
{
    private static readonly long ImageSize = DiskFormatter.MinimumDiskSize;

    [Fact]
    public void EraseSignatures_ClearsLeadingAndTrailingMegabyte()
    {
        using TemporaryDirectory temporary = new();
        string image = Path.Combine(temporary.Path, "disk.img");
        using (FileStream stream = CreateImage(image))
        {
            WriteMarker(stream, 0, "MBR-OLD!");
            WriteMarker(stream, 512 * 1024, "MIDDLE!!");
            WriteMarker(stream, ImageSize - 512 * 1024, "TAIL-OLD");

            DiskFormatter.EraseSignatures(stream, ImageSize);
        }

        using FileStream verify = File.OpenRead(image);
        Assert.Equal(new byte[8], ReadAt(verify, 0));
        Assert.Equal(new byte[8], ReadAt(verify, 512 * 1024));
        Assert.Equal(new byte[8], ReadAt(verify, ImageSize - 512 * 1024));
    }

    [Fact]
    public void FormatFat32_RemovesPreExistingGptHeaders()
    {
        using TemporaryDirectory temporary = new();
        string image = Path.Combine(temporary.Path, "disk.img");
        using (FileStream stream = CreateImage(image))
        {
            WriteMarker(stream, 512, "EFI PART");
            WriteMarker(stream, ImageSize - 512, "EFI PART");

            DiskFormatter.EraseSignatures(stream, ImageSize);
            DiskFormatter.FormatFat32(stream, ImageSize);
            stream.Flush(flushToDisk: true);
        }

        using FileStream verify = File.OpenRead(image);
        Assert.NotEqual("EFI PART", Encoding.ASCII.GetString(ReadAt(verify, 512)));
        Assert.NotEqual("EFI PART", Encoding.ASCII.GetString(ReadAt(verify, ImageSize - 512)));
    }

    [Fact]
    public void FormatFat32_ProducesOneBadUpdateFat32Partition()
    {
        using TemporaryDirectory temporary = new();
        string image = Path.Combine(temporary.Path, "disk.img");
        using (FileStream stream = CreateImage(image))
        {
            DiskFormatter.EraseSignatures(stream, ImageSize);
            DiskFormatter.FormatFat32(stream, ImageSize);
            stream.Flush(flushToDisk: true);
        }

        using FileStream verify = File.OpenRead(image);
        byte[] sector = new byte[512];
        verify.ReadExactly(sector);
        Assert.Equal(0x55, sector[510]);
        Assert.Equal(0xAA, sector[511]);
        // Exactly one primary partition entry.
        Assert.Equal(1, Enumerable.Range(0, 4).Count(index => sector[446 + index * 16 + 4] != 0));
    }

    [Fact]
    public void ExclusiveDeviceHandle_RejectsMissingDevice()
    {
        if (!OperatingSystem.IsLinux())
            return;

        Assert.Throws<DiskSafetyException>(() =>
            ExclusiveDeviceHandle.Open($"/dev/badbuilder-missing-{Guid.NewGuid():N}", 1024));
    }

    [Fact]
    public void EraseSignatures_RejectsUnsupportedSize()
    {
        using MemoryStream stream = new(new byte[4096]);

        Assert.Throws<DiskSafetyException>(() => DiskFormatter.EraseSignatures(stream, 4096));
    }

    private static FileStream CreateImage(string path)
    {
        FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        stream.SetLength(ImageSize);
        return stream;
    }

    private static void WriteMarker(Stream stream, long offset, string marker)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(marker);
        stream.Position = offset;
        stream.Write(bytes);
    }

    private static byte[] ReadAt(Stream stream, long offset)
    {
        byte[] buffer = new byte[8];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
