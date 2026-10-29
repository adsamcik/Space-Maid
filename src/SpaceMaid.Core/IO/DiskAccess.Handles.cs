using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceMaid.Core.IO;

public sealed partial class DiskAccess
{
    // Safety providers query the identity of an already held handle. A failed
    // identity or disposition operation must never become a path-based retry.
    public unsafe bool ReadHandleInformation(SafeFileHandle handle, int kind, void* buffer, uint size)
        => GetFileInformationByHandleEx(handle, kind, buffer, size);
    public unsafe bool ReadHandleInformation(SafeFileHandle handle, int kind, byte[] buffer, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, buffer.Length);
        fixed (byte* pointer = buffer) return GetFileInformationByHandleEx(handle, kind, pointer, (uint)size);
    }
    public unsafe bool WriteHandleInformation(SafeFileHandle handle, int kind, void* buffer, uint size)
        => SetFileInformationByHandle(handle, kind, buffer, size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, void* buffer, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle handle, int kind, void* buffer, uint size);
}
