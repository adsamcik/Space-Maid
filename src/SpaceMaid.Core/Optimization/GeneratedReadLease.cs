using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Optimization;

public sealed record GeneratedInputRevision(string Path, ulong Volume, FileIdentity Identity,
    long Length, long Written, long Created, long Changed);

// Metadata checks precede data/list access. Effective read/list leases deny
// writes and replacement; READ_ATTRIBUTES alone cannot provide that guarantee.
internal sealed partial class GeneratedReadLease : IDisposable
{
    private readonly List<SafeFileHandle> _parents = [];
    public FileStream Stream { get; private set; } = null!;
    public GeneratedInputRevision Revision { get; private set; } = null!;
    private GeneratedReadLease() { }

    public static GeneratedReadLease Open(string path, bool preserveAccessTime = false)
    {
        path = OptimizationPolicy.NormalizePath(path);
        var lease = new GeneratedReadLease();
        try
        {
            var parents = new Stack<string>();
            for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent)) parents.Push(parent);
            foreach (string parent in parents)
            {
                using var metadata = OpenHandle(parent, 0x80, 7);
                var before = Inspect(metadata, parent); CheckLocal(metadata, directory: true);
                var stable = OpenHandle(parent, 0x81, 3);
                lease._parents.Add(stable); CheckLocal(stable, directory: true);
                var after = Inspect(stable, parent);
                if (before.Volume != after.Volume || before.Identity != after.Identity) throw new IOException("An ancestor changed while acquiring its lease.");
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(parent));
                if (name.StartsWith('$') || name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) throw new IOException("Protected storage is kept.");
            }
            using var fileMetadata = OpenHandle(path, 0x80, 7); CheckLocal(fileMetadata, directory: false);
            var observed = Inspect(fileMetadata, path);
            var handle = OpenHandle(path, preserveAccessTime ? 0x80000180u : 0x80000080u, 1);
            try
            {
                if (preserveAccessTime) SuppressAccessUpdates(handle);
                CheckLocal(handle, directory: false);
                lease.Revision = Inspect(handle, path);
                if (observed != lease.Revision) throw new IOException("File changed while acquiring readable content.");
                lease.Stream = new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
            }
            catch { handle.Dispose(); throw; }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void Verify() { if (Inspect(Stream.SafeFileHandle, Revision.Path) != Revision) throw new IOException("Resource changed while its evidence was read."); }

    internal static SafeFileHandle OpenHandle(string path, uint access, uint share)
    {
        var handle = NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), access, share, 0, 3,
            NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint | 0x00100000, 0);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error);
    }

    // FILETIME(-1) suppresses access-time updates only for this handle; it does
    // not restore an old timestamp or suppress observations from other readers.
    internal static unsafe void SuppressAccessUpdates(SafeFileHandle handle)
    {
        long unchanged = -1;
        if (!SetFileTime(handle, null, &unchanged, null)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileTime(SafeFileHandle handle, long* created, long* accessed, long* written);

    internal static unsafe GeneratedInputRevision Inspect(SafeFileHandle handle, string path)
    {
        byte* basic = stackalloc byte[40]; Read(handle, 0, basic, 40);
        byte* standard = stackalloc byte[24]; Read(handle, 1, standard, 24);
        byte* id = stackalloc byte[24]; Read(handle, 18, id, 24);
        var b = new ReadOnlySpan<byte>(basic, 40); var s = new ReadOnlySpan<byte>(standard, 24); var i = new ReadOnlySpan<byte>(id, 24);
        var identity = new FileIdentity(LayoutParser.U64(i, 8), LayoutParser.U64(i, 16));
        if (identity == default) throw new IOException("A stable native identity is unavailable.");
        return new(path, LayoutParser.U64(i, 0), identity, LayoutParser.I64(s, 8), LayoutParser.I64(b, 16), LayoutParser.I64(b, 0), LayoutParser.I64(b, 24));
    }

    internal static unsafe void CheckLocal(SafeFileHandle handle, bool directory)
    {
        byte* basic = stackalloc byte[40]; Read(handle, 0, basic, 40);
        var attributes = (FileAttributes)LayoutParser.U32(new(basic, 40), 32);
        if (attributes.HasFlag(FileAttributes.Directory) != directory
            || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)(0x40000 | 0x400000))) != 0
            || !directory && (attributes & FileAttributes.System) != 0)
            throw new IOException("Linked, offline, provider-managed or non-local content is kept.");
        var cloud = new byte[1028];
        if (ProviderResult(CfGetPlaceholderInfo(handle, 0, cloud, (uint)cloud.Length, out _))
            || ProviderResult(CfGetSyncRootInfoByHandle(handle, 2, cloud, (uint)cloud.Length, out _)))
            throw new IOException("Provider-managed content remains metadata-only.");
    }

    private static bool ProviderResult(int result) => result >= 0 || result == unchecked((int)0x800700EA);
    [LibraryImport("cldapi.dll")]
    private static partial int CfGetPlaceholderInfo(SafeFileHandle handle, int info, [Out, MarshalUsing(CountElementName = nameof(length))] byte[] buffer, uint length, out uint returned);
    [LibraryImport("cldapi.dll")]
    private static partial int CfGetSyncRootInfoByHandle(SafeFileHandle handle, int info, [Out, MarshalUsing(CountElementName = nameof(length))] byte[] buffer, uint length, out uint returned);

    private static unsafe void Read(SafeFileHandle handle, int kind, byte* buffer, uint size)
    { if (!NativeMethods.GetFileInformationByHandleEx(handle, kind, buffer, size)) throw new Win32Exception(Marshal.GetLastPInvokeError()); }

    public void Dispose()
    {
        Stream?.Dispose();
        for (int i = _parents.Count - 1; i >= 0; i--) _parents[i].Dispose();
    }
}
