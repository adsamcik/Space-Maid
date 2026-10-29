using SpaceMaid.Core.IO;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceMaid.Extensions.Photos;

internal sealed record MediaRevision(string Identity, long Length, long Written, long Changed, uint Attributes, uint Links)
{
    public string Key => FormattableString.Invariant($"{Identity}:{Length}:{Written}:{Changed}");
    public bool ContentAvailable => (Attributes & (0x400U | 0x1000U | 0x40000U | 0x400000U)) == 0;
}

// Metadata discovery uses attribute-only handles. Content reads additionally
// hold directory-list/data handles denying deletion/renames, with NO_RECALL.
// Reparse ancestors, placeholders and offline files never reach a data read.
internal sealed class NativeMediaFile : IDisposable
{
    private readonly List<SafeFileHandle> _guards;
    private readonly DiskAccess _files;
    public FileStream? Stream { get; private set; }
    public MediaRevision Revision { get; }
    private NativeMediaFile(List<SafeFileHandle> guards, MediaRevision revision, DiskAccess files)
    { _guards = guards; Revision = revision; _files = files; }

    public static NativeMediaFile Open(string path, bool readContent)
        => Open(path, readContent, DiskAccess.Default);

    public static NativeMediaFile Open(string path, bool readContent, DiskAccess files)
    {
        var full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(full) || full.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Only local, directly accessible media is supported.");
        var guards = new List<SafeFileHandle>();
        try
        {
            var ancestors = new Stack<string>();
            for (var parent = Path.GetDirectoryName(full); parent is not null; parent = Path.GetDirectoryName(parent))
                ancestors.Push(parent);
            while (ancestors.TryPop(out var ancestor))
            {
                var handle = OpenHandle(ancestor, 0x80, 3, 0x02300000, files);
                guards.Add(handle);
                var attributes = ReadBasic(handle, files).Attributes;
                if ((attributes & (0x400U | 0x1000U | 0x40000U | 0x400000U)) != 0)
                    throw new IOException("Reparse or offline ancestors remain metadata-only.");
                if (readContent)
                {
                    var identity = Inspect(handle, files).Identity;
                    var directory = OpenHandle(ancestor, 0x81, 3, 0x02300000, files);
                    guards.Add(directory);
                    var observed = Inspect(directory, files);
                    if (observed.Identity != identity || !observed.ContentAvailable)
                        throw new IOException("A folder changed or became metadata-only before content inspection.");
                }
            }
            var metadata = OpenHandle(full, 0x80, 1, 0x02300000, files);
            guards.Add(metadata);
            var revision = Inspect(metadata, files);
            if ((revision.Attributes & 0x10) != 0) throw new IOException("The selected location is a folder.");
            var lease = new NativeMediaFile(guards, revision, files);
            if (readContent)
            {
                if (!revision.ContentAvailable) throw new IOException("Cloud-only, offline and reparse files remain metadata-only.");
                var data = OpenHandle(full, 0x80000080, 1, 0x0a300000, files);
                try
                {
                    var observed = Inspect(data, files);
                    if (observed.Key != revision.Key || !observed.ContentAvailable) throw new IOException("The file changed before it could be read.");
                    lease.Stream = files.Open(data, FileAccess.Read, 64 * 1024, false);
                }
                catch { data.Dispose(); throw; }
            }
            return lease;
        }
        catch { foreach (var handle in guards) handle.Dispose(); throw; }
    }

    // Discovery retains only the current ancestor chain, not a volume-sized
    // handle cache. The guards prevent path replacement while every photo still
    // gets fresh ancestor attributes, native identity and Cloud Files checks.
    internal sealed class DiscoveryReader : IDisposable
    {
        private readonly DiskAccess _files;
        private readonly List<(string Path, SafeFileHandle Handle)> _parents = [];
        private readonly MetadataBuffers _buffers = new();
        private string? _parent;
        private string? _uncachedParent;
        private bool _disposed;
        public long ParentHandleOpens { get; private set; }
        public long ParentChecks { get; private set; }
        public long FilesInspected { get; private set; }
        public long UncachedFiles { get; private set; }
        public int PeakParentHandles { get; private set; }

        public DiscoveryReader() : this(DiskAccess.Default) { }
        public DiscoveryReader(DiskAccess files) => _files = files;

        public MediaRevision Read(string path)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var full = Path.GetFullPath(path);
            if (!Path.IsPathFullyQualified(full) || full.StartsWith("\\\\", StringComparison.Ordinal))
                throw new IOException("Only local, directly accessible media is supported.");
            var parent = Path.GetDirectoryName(full);
            if (string.Equals(parent, _uncachedParent, StringComparison.Ordinal) && _uncachedParent is not null) return ReadUncached(full);
            if (!string.Equals(parent, _parent, StringComparison.Ordinal))
            {
                _uncachedParent = null;
                var ancestors = new Stack<string>();
                for (var current = parent; current is not null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
                var paths = ancestors.ToArray();
                var common = 0;
                while (common < paths.Length && common < _parents.Count && string.Equals(paths[common], _parents[common].Path, StringComparison.Ordinal)) common++;
                for (var i = _parents.Count - 1; i >= common; i--) { _parents[i].Handle.Dispose(); _parents.RemoveAt(i); }
                _parent = null;
                try
                {
                    for (var i = 0; i < common; i++) CheckParent(_parents[i].Handle);
                    for (var i = common; i < paths.Length; i++)
                    {
                        // Attribute-only access is exempt from sharing checks.
                        // LIST_DIRECTORY makes the no-delete guard effective;
                        // this handle is never used to enumerate or read data.
                        var handle = OpenHandle(paths[i], 0x81, 3, 0x02300000, _files);
                        _parents.Add((paths[i], handle)); ParentHandleOpens++;
                        PeakParentHandles = Math.Max(PeakParentHandles, _parents.Count);
                        // Check before opening the next descendant, including
                        // newly encountered reparse/offline ancestor paths.
                        CheckParent(handle);
                    }
                    _parent = parent;
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 5 or 32)
                {
                    // Traverse-only ACLs and concurrent directory leases must
                    // not lose metadata discovery. Use the original per-file
                    // attribute path without retaining replaceable handles.
                    ReleaseParents(); _uncachedParent = parent; return ReadUncached(full);
                }
                catch { ReleaseParents(); throw; }
            }
            foreach (var entry in _parents) CheckParent(entry.Handle);
            using var metadata = OpenHandle(full, 0x80, 1, 0x02300000, _files);
            var revision = Inspect(metadata, _buffers, _files);
            if ((revision.Attributes & 0x10) != 0) throw new IOException("The selected location is a folder.");
            FilesInspected++;
            return revision;
        }

        private MediaRevision ReadUncached(string full)
        {
            UncachedFiles++;
            var paths = new Stack<string>();
            for (var parent = Path.GetDirectoryName(full); parent is not null; parent = Path.GetDirectoryName(parent)) paths.Push(parent);
            var handles = new List<SafeFileHandle>(paths.Count);
            try
            {
                while (paths.TryPop(out var ancestor))
                {
                    var handle = OpenHandle(ancestor, 0x80, 3, 0x02300000, _files);
                    handles.Add(handle); ParentHandleOpens++; PeakParentHandles = Math.Max(PeakParentHandles, handles.Count); CheckParent(handle);
                }
                using var metadata = OpenHandle(full, 0x80, 1, 0x02300000, _files);
                var revision = Inspect(metadata, _buffers, _files);
                if ((revision.Attributes & 0x10) != 0) throw new IOException("The selected location is a folder.");
                FilesInspected++; return revision;
            }
            finally { foreach (var handle in handles) handle.Dispose(); }
        }

        private void CheckParent(SafeFileHandle handle)
        {
            ParentChecks++;
            var attributes = ReadBasic(handle, _buffers.Basic, _buffers.Tag, _files).Attributes;
            if ((attributes & (0x400U | 0x1000U | 0x40000U | 0x400000U)) != 0)
                throw new IOException("Reparse or offline ancestors remain metadata-only.");
        }
        private void ReleaseParents()
        {
            foreach (var entry in _parents) entry.Handle.Dispose();
            _parents.Clear(); _parent = null;
        }
        public void Dispose() { ReleaseParents(); _disposed = true; }
    }

    public void VerifyUnchanged()
    {
        var metadata = Inspect(_guards[^1], _files);
        if (metadata.Key != Revision.Key || (Stream is not null && (!metadata.ContentAvailable || Inspect(Stream.SafeFileHandle, _files).Key != Revision.Key)))
            throw new IOException("The file changed during analysis; its result was not retained.");
    }

    private sealed class MetadataBuffers
    {
        public byte[] Basic { get; } = new byte[40];
        public byte[] Tag { get; } = new byte[8];
        public byte[] Standard { get; } = new byte[24];
        public byte[] Identity { get; } = new byte[24];
        public byte[] Placeholder { get; } = new byte[4200];
    }

    internal static MediaRevision Inspect(SafeFileHandle handle) => Inspect(handle, DiskAccess.Default);
    internal static MediaRevision Inspect(SafeFileHandle handle, DiskAccess files) => Inspect(handle, new MetadataBuffers(), files);
    private static MediaRevision Inspect(SafeFileHandle handle, MetadataBuffers buffers, DiskAccess files)
    {
        var basic = ReadBasic(handle, buffers.Basic, buffers.Tag, files);
        var standard = buffers.Standard; ReadInfo(handle, 1, standard, files);
        var id = buffers.Identity; ReadInfo(handle, 18, id, files);
        var attributes = basic.Attributes;
        // Fully resident Cloud Files placeholders can omit the reparse/offline
        // flags from file-info queries. Treat recognized placeholders as
        // metadata-only rather than risk hydration through a decoder/tool.
        try
        {
            if (CfGetPlaceholderInfo(handle, 1, buffers.Placeholder, (uint)buffers.Placeholder.Length, out _) >= 0) attributes |= 0x400;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return new(Convert.ToHexString(id) + ":" + basic.Created.ToString(System.Globalization.CultureInfo.InvariantCulture), BinaryPrimitives.ReadInt64LittleEndian(standard.AsSpan(8)),
            basic.Written, basic.Changed, attributes, BinaryPrimitives.ReadUInt32LittleEndian(standard.AsSpan(16)));
    }
    private static (long Created, long Written, long Changed, uint Attributes) ReadBasic(SafeFileHandle handle, DiskAccess files) => ReadBasic(handle, new byte[40], new byte[8], files);
    private static (long Created, long Written, long Changed, uint Attributes) ReadBasic(SafeFileHandle handle, byte[] bytes, byte[] tag, DiskAccess files)
    {
        ReadInfo(handle, 0, bytes, files);
        ReadInfo(handle, 9, tag, files);
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)) | BinaryPrimitives.ReadUInt32LittleEndian(tag);
        if (BinaryPrimitives.ReadUInt32LittleEndian(tag.AsSpan(4)) != 0) attributes |= 0x400;
        return (BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)),
            attributes);
    }
    private static void ReadInfo(SafeFileHandle handle, int kind, byte[] bytes, DiskAccess files)
    {
        if (!files.ReadHandleInformation(handle, kind, bytes, bytes.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static SafeFileHandle OpenHandle(string path, uint access, uint share, uint flags, DiskAccess files)
    {
        var handle = files.OpenHandle(path, access, share, 0, 3, flags, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
    }
    public void Dispose()
    {
        Stream?.Dispose();
        foreach (var guard in _guards) guard.Dispose();
    }
    [DllImport("cldapi.dll")]
    private static extern int CfGetPlaceholderInfo(SafeFileHandle file, int information, [Out] byte[] bytes, uint length, out uint returned);
}
