using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Deduplication;

public sealed record StorageFileRevision(ulong Volume, FileIdentity Identity, long Length, long AllocatedBytes,
    long Created, long Accessed, long Written, long Changed, FileAttributes Attributes, uint Links, bool HasAlternateStreams)
{
    public string ObjectKey => $"{Volume:X16}:{Identity.Low:X16}:{Identity.High:X16}";
    public bool SameContentRevision(StorageFileRevision other) => Volume == other.Volume && Identity == other.Identity
        && Length == other.Length && Created == other.Created && Written == other.Written && Changed == other.Changed;
}

// Attribute inspection never authorizes a content read. Effective directory-list
// and data handles follow the no-recall inspection, then identity is rechecked.
internal sealed partial class StorageFile : IDisposable
{
    private const uint Flags = 0x02300000; // BACKUP_SEMANTICS | OPEN_REPARSE_POINT | OPEN_NO_RECALL.
    internal const FileAttributes Unavailable = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Encrypted
        | FileAttributes.System | (FileAttributes)(0x40000 | 0x400000);
    private const FileAttributes UnavailableAncestor = Unavailable & ~FileAttributes.System;
    private readonly List<SafeFileHandle> _guards = [];
    internal SafeFileHandle Handle { get; private set; } = null!;
    internal StorageFileRevision Revision { get; private set; } = null!;
    internal string Path { get; private set; } = "";

    internal static StorageFile Open(string path, bool writable = false, bool metadataOnly = false)
    {
        var lease = new StorageFile { Path = OptimizationPolicy.NormalizePath(path) };
        try
        {
            var parents = new Stack<string>();
            for (string? parent = System.IO.Path.GetDirectoryName(lease.Path); parent is not null; parent = System.IO.Path.GetDirectoryName(parent))
            {
                if (parents.Count >= 128) throw new IOException("The path exceeds the bounded 128-directory inspection depth.");
                parents.Push(parent);
            }
            foreach (string parent in parents)
            {
                using var metadata = OpenHandle(parent, 0x80, 7);
                var before = Inspect(metadata, streams: false);
                // Drive roots normally carry System. Their descendants are
                // still ordinary files; protected namespaces are checked below.
                if ((before.Attributes & UnavailableAncestor) != 0 || (before.Attributes & FileAttributes.Directory) == 0)
                    throw new IOException("Linked, encrypted, system and offline locations are not inspected.");
                string name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(parent));
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.StartsWith('$')
                    || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Filesystem and repository metadata are excluded.");
                var guard = OpenHandle(parent, 0x81, 3); lease._guards.Add(guard);
                var after = Inspect(guard, streams: false);
                if (before.ObjectKey != after.ObjectKey || (after.Attributes & UnavailableAncestor) != 0)
                    throw new IOException("An ancestor changed before inspection.");
            }
            using var fileMetadata = OpenHandle(lease.Path, 0x80, 7);
            var captured = Inspect(fileMetadata);
            if ((captured.Attributes & (Unavailable | FileAttributes.Directory)) != 0)
                throw new IOException("Only directly accessible local files can be inspected.");
            if (writable && (captured.Attributes & FileAttributes.ReadOnly) != 0)
                throw new IOException("Read-only files are kept unchanged.");
            lease.Handle = OpenHandle(lease.Path, metadataOnly ? 0x80u : writable ? 0xC0000180u : 0x80000080u,
                metadataOnly ? 7u : writable ? 0u : 1u);
            lease.Revision = Inspect(lease.Handle);
            if (!captured.SameContentRevision(lease.Revision) || (lease.Revision.Attributes & Unavailable) != 0)
                throw new IOException("The file changed before its stable handle could be acquired.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal void Verify(StorageFileRevision expected)
    {
        var current = Inspect(Handle);
        if (!expected.SameContentRevision(current) || expected.HasAlternateStreams != current.HasAlternateStreams
            || expected.Links != current.Links || (current.Attributes & Unavailable) != 0)
            throw new IOException("The file changed. Refresh the comparison before trying again.");
    }

    internal static unsafe StorageFileRevision Inspect(SafeFileHandle handle, bool streams = true)
    {
        byte* basic = stackalloc byte[40]; Read(handle, 0, basic, 40);
        byte* standard = stackalloc byte[24]; Read(handle, 1, standard, 24);
        byte* identity = stackalloc byte[24]; Read(handle, 18, identity, 24);
        var b = new ReadOnlySpan<byte>(basic, 40); var s = new ReadOnlySpan<byte>(standard, 24); var id = new ReadOnlySpan<byte>(identity, 24);
        bool extraStreams = false;
        if (streams && (BinaryPrimitives.ReadUInt32LittleEndian(b[32..]) & 0x10) == 0)
        {
            byte* names = stackalloc byte[4096]; Read(handle, 7, names, 4096);
            var stream = new ReadOnlySpan<byte>(names, 4096);
            extraStreams = BinaryPrimitives.ReadUInt32LittleEndian(stream) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(stream[4..]) != 14
                || !new ReadOnlySpan<char>(names + 24, 7).SequenceEqual("::$DATA");
        }
        var nativeId = new FileIdentity(BinaryPrimitives.ReadUInt64LittleEndian(id[8..]), BinaryPrimitives.ReadUInt64LittleEndian(id[16..]));
        if (nativeId == default) throw new IOException("The filesystem did not provide a stable file identity.");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(id), nativeId, BinaryPrimitives.ReadInt64LittleEndian(s[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(s), BinaryPrimitives.ReadInt64LittleEndian(b), BinaryPrimitives.ReadInt64LittleEndian(b[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[16..]), BinaryPrimitives.ReadInt64LittleEndian(b[24..]),
            (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(b[32..]), BinaryPrimitives.ReadUInt32LittleEndian(s[16..]), extraStreams);
    }

    internal static unsafe void RestoreTimes(SafeFileHandle handle, StorageFileRevision original)
    {
        byte* buffer = stackalloc byte[40]; new Span<byte>(buffer, 40).Clear();
        var data = new Span<byte>(buffer, 40);
        BinaryPrimitives.WriteInt64LittleEndian(data, original.Created);
        BinaryPrimitives.WriteInt64LittleEndian(data[8..], original.Accessed);
        BinaryPrimitives.WriteInt64LittleEndian(data[16..], original.Written);
        if (!SetFileInformationByHandle(handle, 0, buffer, 40)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static SafeFileHandle OpenHandle(string path, uint access, uint sharing)
    {
        var handle = NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), access, sharing, 0, 3, Flags, 0);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error);
    }
    private static unsafe void Read(SafeFileHandle handle, int kind, byte* buffer, uint length)
    { if (!NativeMethods.GetFileInformationByHandleEx(handle, kind, buffer, length)) throw new Win32Exception(Marshal.GetLastPInvokeError()); }

    public void Dispose()
    {
        Handle?.Dispose();
        for (int i = _guards.Count - 1; i >= 0; i--) _guards[i].Dispose();
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte* buffer, uint length);
}
