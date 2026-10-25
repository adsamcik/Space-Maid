using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SpaceMaid.Extensions.Containers;

internal sealed record VirtualDiskTarget(string Path, ulong Volume, ulong IdLow, ulong IdHigh, long Length, long LastWriteFileTime);
internal sealed record VirtualDiskRequest(VirtualDiskTarget Target, bool ShutdownWsl);
internal sealed record VirtualDiskResult(bool Succeeded, string Message, long? BeforeBytes = null, long? AfterBytes = null);

// Shared with the fixed-function elevated helper. No command text, mounts,
// deletion, disk creation or arbitrary native operation enters this protocol.
internal static class VirtualDiskCleanup
{
    internal const int MaximumFrameBytes = 16 * 1024;
    internal static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !System.IO.Path.IsPathFullyQualified(path)
            || path.StartsWith("\\", StringComparison.Ordinal) || path.Length < 4 || path[1] != ':' || path[2] != '\\'
            || !path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a .vhdx file on a local drive.");
        foreach (string part in path[3..].Split('\\'))
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("The disk path contains an unsupported file name.");
        return System.IO.Path.GetFullPath(path);
    }

    internal static VirtualDiskTarget Capture(string path)
    {
        using var pins = Pin(path);
        return ReadTarget(pins[0], ValidatePath(path));
    }

    internal static void RequireUnchanged(VirtualDiskTarget captured, VirtualDiskTarget current)
    {
        if (captured != current) throw new InvalidOperationException("This disk changed after preview. Inspect it again before compaction.");
    }

    internal static VirtualDiskResult Compact(VirtualDiskRequest request)
    {
        if (request.Target is null) throw new InvalidDataException("A captured disk target is required.");
        string path = ValidatePath(request.Target.Path);
        // Shutdown is a separately disclosed part of the confirmed action.
        // Validate the target before interrupting any WSL sessions.
        RequireUnchanged(request.Target, Capture(path));
        if (request.ShutdownWsl) ShutdownWsl();
        using var pins = Pin(path);
        RequireUnchanged(request.Target, ReadTarget(pins[0], path));
        var storage = new StorageType { DeviceId = 3, VendorId = new Guid("EC984AEC-A0F9-47e9-901F-71415A66345B") };
        var open = new OpenParameters { Version = 2 };
        // Microsoft's VHDX compaction sample uses a V2 writable handle with
        // ACCESS_NONE (V2 derives access from the parameters). NO_PARENTS keeps
        // this operation from opening a differencing chain before rejecting it.
        uint status = OpenVirtualDisk(ref storage, path, 0, 1, ref open, out var disk);
        if (status != 0) throw new Win32Exception((int)status, "The disk could not be opened for compaction. Close Docker Desktop and virtual machines, then retry.");
        using (disk)
        {
            uint subtype = Info(disk, 7);
            if (subtype != 3) throw new InvalidOperationException("Only standalone dynamically expanding VHDX disks can be compacted. Fixed and differencing disks are kept.");
            if (Info(disk, 13) != 0) throw new InvalidOperationException("This disk is attached or in use. Close its owner before compaction.");
            // Confirm the pathname still resolves to the same pinned file before
            // the final OS operation. Ancestors and target cannot be renamed.
            RequireUnchanged(request.Target, Capture(path));
            var compact = new CompactParameters { Version = 1 };
            status = CompactVirtualDisk(disk, 0, ref compact, 0);
            if (status != 0) throw new Win32Exception((int)status, "Windows could not finish compaction. The disk may be busy or unsupported; no file was deleted.");
        }
        var after = ReadTarget(pins[0], path);
        if (after.Volume != request.Target.Volume || after.IdLow != request.Target.IdLow || after.IdHigh != request.Target.IdHigh)
            throw new IOException("The disk identity could not be verified after compaction. Inspect its state before further work.");
        return new(true, "Windows completed zero-block compaction. Guest files and virtual capacity were retained; actual host allocation can differ from file length.", request.Target.Length, after.Length);
    }

    private static void ShutdownWsl()
    {
        string executable = System.IO.Path.Join(Environment.SystemDirectory, "wsl.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--shutdown");
        using var process = Process.Start(start) ?? throw new IOException("WSL shutdown did not start.");
        if (!process.WaitForExit(60000)) throw new IOException("WSL shutdown did not complete in time. Compaction was not started.");
        if (process.ExitCode != 0) throw new IOException("WSL shutdown failed. Compaction was not started.");
    }

    private static uint Info(SafeFileHandle disk, int version)
    {
        byte[] info = new byte[40]; BinaryPrimitives.WriteInt32LittleEndian(info, version); uint size = (uint)info.Length;
        uint status = GetVirtualDiskInformation(disk, ref size, info, 0);
        if (status != 0) throw new Win32Exception((int)status, "Windows could not verify the disk type and attachment state.");
        // The union starts at offset eight (ULONGLONG alignment).
        return BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(8));
    }

    private static DiskPins Pin(string path)
    {
        path = ValidatePath(path); var handles = new List<SafeFileHandle>();
        try
        {
            for (string? part = path; part is not null; part = System.IO.Path.GetDirectoryName(part))
            {
                bool file = part == path;
                var handle = CreateFileW(part, 0x80, 3, 0, 3, 0x00200000 | (file ? 0u : 0x02000000u), 0);
                if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var metadata)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var attributes = (FileAttributes)metadata.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0 || file == attributes.HasFlag(FileAttributes.Directory))
                    throw new IOException("The disk and its ancestors must be regular local files and directories, without links.");
                if ((attributes & (FileAttributes.SparseFile | FileAttributes.Compressed | FileAttributes.Encrypted)) != 0)
                    throw new IOException("Sparse, compressed or encrypted disks and ancestor folders are not supported for compaction here.");
                if (file && metadata.Links != 1) throw new IOException("A disk with multiple hard links cannot be compacted here.");
            }
            return new(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    private static VirtualDiskTarget ReadTarget(SafeFileHandle file, string path)
    {
        if (!GetFileInformationByHandle(file, out var metadata)) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[] identity = new byte[24];
        if (!GetFileInformationByHandleEx(file, 18, identity, 24)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(path, BinaryPrimitives.ReadUInt64LittleEndian(identity), BinaryPrimitives.ReadUInt64LittleEndian(identity.AsSpan(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(identity.AsSpan(16)), ((long)metadata.SizeHigh << 32) | metadata.SizeLow,
            ((long)metadata.WriteHigh << 32) | metadata.WriteLow);
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
        if (json.Length > MaximumFrameBytes) throw new InvalidDataException("The compaction message is too large.");
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, json.Length);
        await stream.WriteAsync(length, token); await stream.WriteAsync(json, token); await stream.FlushAsync(token);
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumFrameBytes) throw new InvalidDataException("Invalid compaction message size.");
        byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, token);
        return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { MaxDepth = 12 }) ?? throw new InvalidDataException("Invalid compaction message.");
    }

    private sealed class DiskPins(List<SafeFileHandle> handles) : IDisposable
    {
        internal SafeFileHandle this[int index] => handles[index];
        public void Dispose() { foreach (var handle in handles) handle.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct StorageType { internal uint DeviceId; internal Guid VendorId; }
    [StructLayout(LayoutKind.Sequential)] private struct OpenParameters { internal uint Version; internal int GetInfoOnly, ReadOnly; internal Guid ResiliencyGuid; }
    [StructLayout(LayoutKind.Sequential)] private struct CompactParameters { internal uint Version, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct FileMetadata
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume,
            SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileMetadata metadata);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, byte[] information, uint size);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)] private static extern uint OpenVirtualDisk(ref StorageType storage, string path, uint access, uint flags, ref OpenParameters parameters, out SafeFileHandle disk);
    [DllImport("virtdisk.dll")] private static extern uint GetVirtualDiskInformation(SafeFileHandle disk, ref uint size, byte[] information, nint used);
    [DllImport("virtdisk.dll")] private static extern uint CompactVirtualDisk(SafeFileHandle disk, uint flags, ref CompactParameters parameters, nint overlapped);
}
