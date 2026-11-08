using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BadBuilder.Services.Disks;

/// <summary>
/// Holds the target block device open with O_EXCL so nothing else can mount or
/// open it while it is being erased, and binds the size check to that handle
/// rather than to the device path.
/// </summary>
internal sealed class ExclusiveDeviceHandle : IDisposable
{
    private const int BusyError = 16;

    private readonly SafeFileHandle _handle;

    private ExclusiveDeviceHandle(SafeFileHandle handle, FileStream stream)
    {
        _handle = handle;
        Stream = stream;
    }

    internal FileStream Stream { get; }

    internal static ExclusiveDeviceHandle Open(string devicePath, long expectedSizeBytes)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Exclusive device handles are available only on Linux.");

        int descriptor = LinuxNative.OpenDeviceExclusive(devicePath);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new DiskSafetyException(error == BusyError
                ? $"{devicePath} is busy. Unmount every volume on it before retrying."
                : $"Could not open {devicePath} exclusively. OS error {error}.");
        }

        SafeFileHandle handle = new((IntPtr)descriptor, ownsHandle: true);
        try
        {
            long size = LinuxNative.GetDeviceSize(descriptor);
            if (size != expectedSizeBytes)
            {
                throw new DiskSafetyException(
                    $"The size of {devicePath} changed after it was selected. Select the disk again.");
            }

            FileStream stream = new(handle, FileAccess.ReadWrite, 4096, isAsync: false);
            return new ExclusiveDeviceHandle(handle, stream);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void RereadPartitionTable()
    {
        if (LinuxNative.RereadPartitions((int)_handle.DangerousGetHandle()) != 0)
        {
            throw new IOException(
                $"Could not re-read the partition table. OS error {Marshal.GetLastPInvokeError()}.");
        }
    }

    public void Dispose() => Stream.Dispose();
}
