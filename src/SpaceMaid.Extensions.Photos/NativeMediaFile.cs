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
    public FileStream? Stream { get; private set; }
    public MediaRevision Revision { get; }
    private NativeMediaFile(List<SafeFileHandle> guards, MediaRevision revision)
    { _guards = guards; Revision = revision; }

    public static NativeMediaFile Open(string path, bool readContent)
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
                var handle = OpenHandle(ancestor, 0x80, 3, 0x02300000);
                guards.Add(handle);
                var attributes = ReadBasic(handle).Attributes;
                if ((attributes & (0x400U | 0x1000U | 0x40000U | 0x400000U)) != 0)
                    throw new IOException("Reparse or offline ancestors remain metadata-only.");
                if (readContent)
                {
                    var identity = Inspect(handle).Identity;
                    var directory = OpenHandle(ancestor, 0x81, 3, 0x02300000);
                    guards.Add(directory);
                    var observed = Inspect(directory);
                    if (observed.Identity != identity || !observed.ContentAvailable)
                        throw new IOException("A folder changed or became metadata-only before content inspection.");
                }
            }
            var metadata = OpenHandle(full, 0x80, 1, 0x02300000);
            guards.Add(metadata);
            var revision = Inspect(metadata);
            if ((revision.Attributes & 0x10) != 0) throw new IOException("The selected location is a folder.");
            var lease = new NativeMediaFile(guards, revision);
            if (readContent)
            {
                if (!revision.ContentAvailable) throw new IOException("Cloud-only, offline and reparse files remain metadata-only.");
                var data = OpenHandle(full, 0x80000080, 1, 0x0a300000);
                try
                {
                    var observed = Inspect(data);
                    if (observed.Key != revision.Key || !observed.ContentAvailable) throw new IOException("The file changed before it could be read.");
                    lease.Stream = new FileStream(data, FileAccess.Read, 64 * 1024, false);
                }
                catch { data.Dispose(); throw; }
            }
            return lease;
        }
        catch { foreach (var handle in guards) handle.Dispose(); throw; }
    }

    public void VerifyUnchanged()
    {
        var metadata = Inspect(_guards[^1]);
        if (metadata.Key != Revision.Key || (Stream is not null && (!metadata.ContentAvailable || Inspect(Stream.SafeFileHandle).Key != Revision.Key)))
            throw new IOException("The file changed during analysis; its result was not retained.");
    }

    internal static MediaRevision Inspect(SafeFileHandle handle)
    {
        var basic = ReadBasic(handle);
        var standard = ReadInfo(handle, 1, 24);
        var id = ReadInfo(handle, 18, 24);
        var attributes = basic.Attributes;
        // Fully resident Cloud Files placeholders can omit the reparse/offline
        // flags from file-info queries. Treat recognized placeholders as
        // metadata-only rather than risk hydration through a decoder/tool.
        try
        {
            if (CfGetPlaceholderInfo(handle, 1, new byte[4200], 4200, out _) >= 0) attributes |= 0x400;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return new(Convert.ToHexString(id) + ":" + basic.Created.ToString(System.Globalization.CultureInfo.InvariantCulture), BinaryPrimitives.ReadInt64LittleEndian(standard.AsSpan(8)),
            basic.Written, basic.Changed, attributes, BinaryPrimitives.ReadUInt32LittleEndian(standard.AsSpan(16)));
    }
    private static (long Created, long Written, long Changed, uint Attributes) ReadBasic(SafeFileHandle handle)
    {
        var bytes = ReadInfo(handle, 0, 40);
        var tag = ReadInfo(handle, 9, 8);
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)) | BinaryPrimitives.ReadUInt32LittleEndian(tag);
        if (BinaryPrimitives.ReadUInt32LittleEndian(tag.AsSpan(4)) != 0) attributes |= 0x400;
        return (BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)),
            attributes);
    }
    private static byte[] ReadInfo(SafeFileHandle handle, int kind, int size)
    {
        var bytes = new byte[size];
        if (!GetFileInformationByHandleEx(handle, kind, bytes, bytes.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return bytes;
    }
    private static SafeFileHandle OpenHandle(string path, uint access, uint share, uint flags)
    {
        var handle = CreateFileW(path, access, share, 0, 3, flags, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
    }
    public void Dispose()
    {
        Stream?.Dispose();
        foreach (var guard in _guards) guard.Dispose();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int information, [Out] byte[] bytes, int length);
    [DllImport("cldapi.dll")]
    private static extern int CfGetPlaceholderInfo(SafeFileHandle file, int information, [Out] byte[] bytes, uint length, out uint returned);
}
