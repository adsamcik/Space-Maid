using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceMaid.App.Extensions;

// ExifTool accepts paths rather than our handles. Inspect metadata first, then
// keep validated read handles denying replacement/writes open for the request
// before the external process is allowed to open their content.
internal sealed partial class ToolInputLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    internal ToolInputLease(IReadOnlyList<string> paths)
    {
        try
        {
            var parents = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                ExifToolBroker.ValidateInput(path);
                string? parent = Path.GetDirectoryName(path);
                while (parent is not null)
                {
                    if (parents.Add(parent)) Hold(parent, directory: true);
                    parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(parent));
                }
                Hold(path, directory: false);
            }
        }
        catch { Dispose(); throw; }
    }
    private unsafe void Hold(string path, bool directory)
    {
        string extended = path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path : "\\\\?\\" + path;
        const uint flags = 0x02000000 | 0x00200000 | 0x00100000; // backup semantics, open reparse point, no recall
        var handle = CreateFile(extended, 0x80, 1, 0, 3, flags, 0);
        if (handle.IsInvalid) { int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new IOException("A selected tool input could not be held stable.", new Win32Exception(error)); }
        _handles.Add(handle);
        byte[] basic = ReadInformation(handle, 0, 40);
        uint attributes = ReadAttributes(handle, basic);
        if ((attributes & (0x400u | 0x1000u | 0x40000u | 0x400000u)) != 0 || ((attributes & 0x10u) != 0) != directory)
            throw new IOException("Cloud-only, reparse, or replaced tool inputs remain metadata-only.");
        byte[] identity = ReadInformation(handle, 18, 24);
        // READ_ATTRIBUTES alone does not participate in Windows data sharing.
        // After metadata eligibility, hold a data-read handle without reading
        // bytes. OPEN_REPARSE_POINT/NO_RECALL preserve the metadata-only cloud
        // boundary even if the path changed before this second open.
        var data = CreateFile(extended, directory ? 0x81u : 0x80000080u, 1, 0, 3, flags, 0);
        if (data.IsInvalid) { int error = Marshal.GetLastPInvokeError(); data.Dispose(); throw new IOException("A selected tool input could not be held for stable reading.", new Win32Exception(error)); }
        _handles.Add(data);
        byte[] heldBasic = ReadInformation(data, 0, 40);
        uint heldAttributes = ReadAttributes(data, heldBasic);
        if (!identity.AsSpan().SequenceEqual(ReadInformation(data, 18, 24))
            || BitConverter.ToInt64(basic, 16) != BitConverter.ToInt64(heldBasic, 16)
            || (heldAttributes & (0x400u | 0x1000u | 0x40000u | 0x400000u)) != 0
            || ((heldAttributes & 0x10u) != 0) != directory)
            throw new IOException("A selected tool input changed while its stable read was being established.");
    }
    private static uint ReadAttributes(SafeFileHandle handle, byte[] basic)
    {
        byte[] tag = ReadInformation(handle, 9, 8);
        uint attributes = BitConverter.ToUInt32(basic, 32) | BitConverter.ToUInt32(tag, 0);
        if (BitConverter.ToUInt32(tag, 4) != 0) attributes |= 0x400;
        // Fully resident provider placeholders can hide their reparse flag in
        // basic/tag metadata. Probe the held object without reading content.
        try
        {
            byte[] placeholder = new byte[4200];
            unsafe
            {
                fixed (byte* info = placeholder)
                {
                    int result = CfGetPlaceholderInfo(handle, 1, info, (uint)placeholder.Length, out _);
                    if (result >= 0 || result == unchecked((int)0x800700EA)) attributes |= 0x400;
                }
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return attributes;
    }
    private static unsafe byte[] ReadInformation(SafeFileHandle handle, int infoClass, int length)
    {
        byte[] result = new byte[length];
        fixed (byte* info = result)
            if (!GetFileInformationByHandleEx(handle, infoClass, info, (uint)length))
                throw new IOException("A selected tool input's metadata could not be validated.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return result;
    }
    public void Dispose() { foreach (var handle in _handles) handle.Dispose(); _handles.Clear(); }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, void* info, uint size);
    [LibraryImport("cldapi.dll")]
    private static unsafe partial int CfGetPlaceholderInfo(SafeFileHandle handle, int information, byte* buffer, uint length, out uint returned);
}
