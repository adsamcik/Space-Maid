using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceMaid.Core.Scanning;

internal static partial class NativeMethods
{
    public const uint QueryFileLayout = 0x00090273;
    public const uint BackupSemantics = 0x02000000;
    public const uint OpenReparsePoint = 0x00200000;
    public const int NoMoreFiles = 18;
    public const int HandleEof = 38;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share,
        nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool DeviceIoControl(SafeFileHandle handle, uint code,
        byte* input, int inputSize, byte* output, int outputSize, out int bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle handle,
        int infoClass, byte* buffer, uint size);

    public static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
}
