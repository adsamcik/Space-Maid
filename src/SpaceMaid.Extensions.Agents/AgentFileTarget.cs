using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceMaid.Extensions.Agents;

internal sealed record AgentFileIdentity(ulong Volume, ulong Low, ulong High, long Length, long LastWriteFileTime);

internal static class AgentFileTarget
{
    internal static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || path.StartsWith('\\')
            || path.Length < 4 || path[1] != ':' || path[2] is not ('\\' or '/'))
            throw new InvalidOperationException("Choose an agent home on a local drive.");
        var parts = path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
            || part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            || parts[0].StartsWith('$') || parts[0].Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This path cannot be safely passed to Windows recycling.");
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    }

    internal static void ValidateAncestors(string path, bool includeSelf = false)
    {
        for (string? current = includeSelf ? path : System.IO.Path.GetDirectoryName(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || !attributes.HasFlag(FileAttributes.Directory))
                throw new IOException("Linked, redirected or unavailable agent paths are kept.");
        }
    }

    internal static AgentFileIdentity Capture(string path, bool directory = false)
    {
        path = ValidatePath(path); ValidateAncestors(path);
        using var handle = Open(path, 7, directory);
        return Read(handle);
    }

    // Deny new writers/readers while allowing only the Shell's rename/delete.
    // Parent leases prevent ancestor substitution. The exact file identity is
    // checked in PreDeleteItem and again at the reported Recycle Bin location.
    internal static IDisposable Lease(AgentHistoryItem item)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            string path = ValidatePath(item.Path);
            for (string? current = System.IO.Path.GetDirectoryName(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
                handles.Add(Open(current, 3, directory: true, lockData: true));
            var file = Open(path, 4, directory: false, lockData: true); handles.Add(file);
            if (Read(file) != item.Identity) throw new IOException("This history file changed after confirmation. Inspect again.");
            return new Leases(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    internal static void ValidateRecycled(AgentHistoryItem item, string path)
    {
        string root = System.IO.Path.GetPathRoot(item.Path)!;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Windows returned an unexpected Recycle Bin destination.");
        string[] parts = path[root.Length..].Split('\\');
        if (parts.Length != 3 || !parts[0].Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            || !parts[1].StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) || !parts[2].StartsWith("$R", StringComparison.OrdinalIgnoreCase)
            || parts.Any(part => part is "." or ".." || part.Contains('/') || part.EndsWith('.') || part.EndsWith(' ')))
            throw new IOException("Windows returned an unexpected Recycle Bin destination.");
        ValidateAncestors(path);
        using var handle = Open(path, 7, directory: false);
        if (Read(handle) != item.Identity) throw new IOException("Windows did not recycle the confirmed file. Inspect its current state before retrying.");
        try { File.GetAttributes(item.Path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return; }
        throw new IOException("The original file is still present. Inspect its current state before retrying.");
    }

    private static SafeFileHandle Open(string path, uint share, bool directory, bool lockData = false)
    {
        var handle = CreateFileW(path, 0x80, share, 0, 3, 0x00300000 | (directory ? 0x02000000u : 0u), 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "The history file is unavailable or in use. Close all agent clients before cleanup."); }
        try
        {
            var data = Metadata(handle);
            const FileAttributes cloud = (FileAttributes)(0x1000 | 0x40000 | 0x400000);
            var attributes = (FileAttributes)data.Attributes;
            if ((attributes & (FileAttributes.ReparsePoint | cloud)) != 0 || attributes.HasFlag(FileAttributes.Directory) != directory
                || !directory && (data.Links != 1 || attributes.HasFlag(FileAttributes.ReadOnly)))
                throw new IOException("Linked, cloud-only, read-only or multiply linked history is kept.");
            if (!lockData) return handle;
            var before = Read(handle);
            // FILE_READ_ATTRIBUTES alone does not enforce data-write sharing.
            // Ask for read/list access after the no-recall provider check, but
            // do not read a single byte of transcript content.
            var stable = CreateFileW(path, directory ? 0x81u : 0x80000080u, share, 0, 3,
                0x00300000 | (directory ? 0x02000000u : 0u), 0);
            if (stable.IsInvalid) { stable.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "The history file is in use. Close all agent clients before cleanup."); }
            try
            {
                if (Read(stable) != before) throw new IOException("An agent path changed before its stable lease could be acquired.");
                handle.Dispose(); return stable;
            }
            catch { stable.Dispose(); throw; }
        }
        catch { handle.Dispose(); throw; }
    }
    private static FileMetadata Metadata(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var metadata)) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[] provider = new byte[4200];
        if (RecognizesProvider(CfGetPlaceholderInfo(handle, 1, provider, (uint)provider.Length, out _))
            || RecognizesProvider(CfGetSyncRootInfoByHandle(handle, 2, provider, 1028, out _)))
            throw new IOException("Provider-managed agent paths are kept; cloud contents were not requested.");
        return metadata;
    }
    private static bool RecognizesProvider(int result) => result >= 0 || result == unchecked((int)0x800700EA);
    private static AgentFileIdentity Read(SafeFileHandle handle)
    {
        var data = Metadata(handle); byte[] identity = new byte[24];
        if (!GetFileInformationByHandleEx(handle, 18, identity, 24)) throw new Win32Exception(Marshal.GetLastWin32Error());
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(identity.AsSpan(8)), high = BinaryPrimitives.ReadUInt64LittleEndian(identity.AsSpan(16));
        if (low == 0 && high == 0) throw new IOException("Windows did not provide a usable file identity.");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(identity), low, high,
            ((long)data.SizeHigh << 32) | data.SizeLow, ((long)data.WriteHigh << 32) | data.WriteLow);
    }
    private sealed class Leases(List<SafeFileHandle> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles) handle.Dispose(); } }
    [StructLayout(LayoutKind.Sequential)] private struct FileMetadata
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume,
            SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileMetadata metadata);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, byte[] data, uint size);
    [DllImport("cldapi.dll")] private static extern int CfGetPlaceholderInfo(SafeFileHandle file, int info, byte[] data, uint size, out uint returned);
    [DllImport("cldapi.dll")] private static extern int CfGetSyncRootInfoByHandle(SafeFileHandle file, int info, byte[] data, uint size, out uint returned);
}
