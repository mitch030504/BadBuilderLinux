using System.Runtime.Versioning;
using BadBuilder.Services.Disks;

namespace BadBuilder.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsDiskBackendTests
{
    [Fact]
    public void SelectPreparedDriveLetter_AcceptsSingleFat32BadupdateVolume()
    {
        VolumeInfo volume = new(@"\\.\PhysicalDrive1", "part", "FAT32", "BADUPDATE", [@"E:\"]);

        Assert.Equal(@"E:\", WindowsDiskBackend.SelectPreparedDriveLetter([volume]));
    }

    [Fact]
    public void SelectPreparedDriveLetter_TrimsFilesystemAndLabel()
    {
        VolumeInfo volume = new(@"\\.\PhysicalDrive1", "part", " fat32 ", " badupdate  ", [@"e:\"]);

        Assert.Equal(@"E:\", WindowsDiskBackend.SelectPreparedDriveLetter([volume]));
    }

    [Fact]
    public void SelectPreparedDriveLetter_AllowsFilesystemAndLabelWhileWmiHasNotReportedThem()
    {
        VolumeInfo volume = new(@"\\.\PhysicalDrive1", "part", null, null, [@"E:\"]);

        Assert.Equal(@"E:\", WindowsDiskBackend.SelectPreparedDriveLetter([volume]));
    }

    [Fact]
    public void SelectPreparedDriveLetter_ReturnsNullWhenNoVolumeExists()
    {
        Assert.Null(WindowsDiskBackend.SelectPreparedDriveLetter([]));
    }

    [Fact]
    public void SelectPreparedDriveLetter_ReturnsNullWhenNoDriveLetterIsAssignedYet()
    {
        VolumeInfo volume = new(@"\\.\PhysicalDrive1", "part", "FAT32", "BADUPDATE", []);

        Assert.Null(WindowsDiskBackend.SelectPreparedDriveLetter([volume]));
    }

    [Fact]
    public void SelectPreparedDriveLetter_RejectsMultiplePartitions()
    {
        VolumeInfo first = new(@"\\.\PhysicalDrive1", "part", "FAT32", "BADUPDATE", [@"E:\"]);
        VolumeInfo second = new(@"\\.\PhysicalDrive2", "part", "FAT32", "BADUPDATE", [@"F:\"]);

        Assert.Throws<DiskSafetyException>(() => WindowsDiskBackend.SelectPreparedDriveLetter([first, second]));
    }

    [Fact]
    public void SelectPreparedDriveLetter_RejectsMultipleDriveLetters()
    {
        VolumeInfo volume = new(@"\\.\PhysicalDrive1", "part", "FAT32", "BADUPDATE", [@"E:\", @"F:\"]);

        Assert.Throws<DiskSafetyException>(() => WindowsDiskBackend.SelectPreparedDriveLetter([volume]));
    }

    [Theory]
    [InlineData("NTFS")]
    [InlineData("FAT")]
    [InlineData("exFAT")]
    public void SelectPreparedDriveLetter_RejectsWrongFilesystem(string fileSystem)
    {
        VolumeInfo volume = new(@"\\.\PhysicalDrive1", "part", fileSystem, "BADUPDATE", [@"E:\"]);

        Assert.Throws<DiskSafetyException>(() => WindowsDiskBackend.SelectPreparedDriveLetter([volume]));
    }

    [Theory]
    [InlineData("OLD")]
    [InlineData("BADUPDATE2")]
    public void SelectPreparedDriveLetter_RejectsWrongLabel(string label)
    {
        VolumeInfo volume = new(@"\\.\PhysicalDrive1", "part", "FAT32", label, [@"E:\"]);

        Assert.Throws<DiskSafetyException>(() => WindowsDiskBackend.SelectPreparedDriveLetter([volume]));
    }

    [Theory]
    [InlineData(@"E:\", @"\\.\E:")]
    [InlineData(@"E:", @"\\.\E:")]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\", @"\\.\Volume{11111111-2222-3333-4444-555555555555}")]
    public void ToDevicePath_NormalizesDriveLettersAndVolumeGuids(string accessPath, string expected)
    {
        Assert.Equal(expected, WindowsDiskBackend.ToDevicePath(accessPath));
    }

    [Theory]
    [InlineData(@"\\.\PhysicalDrive0", 0)]
    [InlineData(@"\\.\PhysicalDrive15", 15)]
    [InlineData(@"\\?\PhysicalDrive3", 3)]
    public void ParseDiskIndex_ReadsTheTrailingIndex(string devicePath, int expected)
    {
        Assert.Equal(expected, WindowsDiskBackend.ParseDiskIndex(devicePath));
    }

    [Theory]
    [InlineData(@"\\.\HardDisk0")]
    [InlineData(@"\\.\PhysicalDrive")]
    [InlineData(@"\\.\PhysicalDriveabc")]
    public void ParseDiskIndex_RejectsUnexpectedPaths(string devicePath)
    {
        Assert.Throws<DiskSafetyException>(() => WindowsDiskBackend.ParseDiskIndex(devicePath));
    }
}
