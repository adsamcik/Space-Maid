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

    // BY_HANDLE_FILE_INFORMATION consists of thirteen DWORDs. Existing safety
    // providers keep their typed layouts; reject any other size before native IO.
    public unsafe bool ReadHandleInformation<T>(SafeFileHandle handle, out T information) where T : unmanaged
    {
        if (sizeof(T) != 52) throw new ArgumentException("Legacy file information requires a 52-byte native layout.", nameof(information));
        information = default;
        fixed (T* pointer = &information) return GetFileInformationByHandle(handle, pointer);
    }

    public unsafe bool WriteHandleTimes(SafeFileHandle handle, long* created, long* accessed, long* written)
        => SetFileTime(handle, created, accessed, written);

    // Provider code owns the control code and its safety policy. The gateway
    // never retries a control operation or changes its flags, buffers or handle.
    public unsafe bool DeviceControl(SafeFileHandle handle, uint code, byte* input, int inputSize,
        byte* output, int outputSize, out int returned, nint overlapped)
        => DeviceIoControl(handle, code, input, inputSize, output, outputSize, out returned, overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, void* buffer, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle handle, int kind, void* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandle(SafeFileHandle handle, void* information);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileTime(SafeFileHandle handle, long* created, long* accessed, long* written);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle handle, uint code, byte* input, int inputSize,
        byte* output, int outputSize, out int returned, nint overlapped);
}
