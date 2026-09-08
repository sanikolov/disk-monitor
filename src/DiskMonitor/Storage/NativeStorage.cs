using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace DiskMonitor.Storage;

internal static class NativeStorage
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint IoctlStoragePredictFailure = 0x002D1100;

    public static bool TryPredictFailure(int diskNumber, out bool predictsFailure, out int errorCode)
    {
        predictsFailure = false;
        errorCode = 0;

        if (diskNumber < 0)
        {
            return false;
        }

        var path = @$"\\.\PhysicalDrive{diskNumber}";
        using var handle = CreateFile(
            path,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }

        var output = new byte[516];
        if (!DeviceIoControl(handle, IoctlStoragePredictFailure, null, 0, output, output.Length, out var bytesReturned, IntPtr.Zero))
        {
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }

        if (bytesReturned < sizeof(uint))
        {
            errorCode = 13; // ERROR_INVALID_DATA
            return false;
        }

        predictsFailure = BitConverter.ToUInt32(output, 0) != 0;
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[]? inputBuffer,
        uint inputBufferSize,
        [Out] byte[] outputBuffer,
        int outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
