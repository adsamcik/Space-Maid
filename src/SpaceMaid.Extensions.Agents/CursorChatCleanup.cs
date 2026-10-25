using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.Extensions.Agents;

internal sealed record CursorChatMember(string RelativePath, bool IsDirectory, AgentFileIdentity Identity);
internal sealed record CursorChatSnapshot(string Path, IReadOnlyList<CursorChatMember> Members, FolderDeletionTarget Target)
{
    internal AgentFileIdentity RootIdentity => Members[0].Identity;
    internal long LogicalBytes => Members.Where(member => !member.IsDirectory).Sum(member => member.Identity.Length);
    internal long LatestWriteFileTime => Members.Max(member => member.Identity.LastWriteFileTime);
}

// Cursor CLI gives each chat its own store.db. Its complete directory is the
// recovery unit: never remove a WAL or shared IDE state.vscdb independently.
internal static class CursorChatCleanup
{
    internal const int MaximumEntries = 1000;

    internal static bool IsChatFolder(AgentHome home, string path)
    {
        if (home.Tool != AgentTool.Cursor) return false;
        string root = AgentFileTarget.ValidatePath(home.Path);
        path = AgentFileTarget.ValidatePath(path);
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        string[] parts = path[(root.Length + 1)..].Split('\\');
        return parts.Length == 3 && parts[0] == "chats" && parts[1].Length == 32
            && parts[1].All(character => char.IsAsciiHexDigit(character))
            && Guid.TryParseExact(parts[2], "D", out _);
    }

    internal static CursorChatSnapshot Capture(AgentHome home, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsChatFolder(home, path)) throw new InvalidOperationException("This is not a supported Cursor CLI chat folder.");
        path = AgentFileTarget.ValidatePath(path);
        AgentFileTarget.ValidateAncestors(path, includeSelf: true);
        var members = new List<CursorChatMember>();
        Walk(path, "", true);
        if (!members.Any(member => member.RelativePath == "store.db" && !member.IsDirectory))
            throw new InvalidOperationException("The Cursor CLI chat has no supported store.db. The folder was kept.");
        long bytes = members.Where(member => !member.IsDirectory).Sum(member => member.Identity.Length);
        var target = FolderDeletionTarget.CaptureCurrent(path, bytes);
        if (ReadPath(path, directory: true) != members[0].Identity)
            throw new IOException("The Cursor chat folder changed during inspection. Inspect it again.");
        return new(path, Array.AsReadOnly(members.OrderBy(member => member.RelativePath, StringComparer.Ordinal).ToArray()), target);

        void Walk(string current, string relative, bool directory)
        {
            token.ThrowIfCancellationRequested();
            if (members.Count >= MaximumEntries) throw new IOException($"The Cursor chat exceeds the {MaximumEntries:N0}-entry inspection limit. It was kept.");
            var identity = ReadPath(current, directory);
            members.Add(new(relative, directory, identity));
            if (!directory) return;
            foreach (string child in Directory.EnumerateFileSystemEntries(current))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths inside a Cursor chat are kept.");
                string childRelative = relative.Length == 0 ? System.IO.Path.GetFileName(child) : System.IO.Path.Join(relative, System.IO.Path.GetFileName(child));
                Walk(child, childRelative, attributes.HasFlag(FileAttributes.Directory));
            }
        }
    }

    internal static void Validate(AgentHistoryItem item, int olderThanDays, CancellationToken token)
    {
        if (olderThanDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(olderThanDays));
        var captured = item.Folder ?? throw new InvalidOperationException("The Cursor chat has no captured folder preview.");
        if (item.Path != captured.Path || item.Identity != captured.RootIdentity)
            throw new InvalidOperationException("This Cursor chat does not match the confirmed folder.");
        if (captured.LatestWriteFileTime <= 0 || DateTime.FromFileTimeUtc(captured.LatestWriteFileTime) > DateTime.UtcNow.AddDays(-olderThanDays))
            throw new InvalidOperationException("Recent Cursor chat work is kept. Inspect again with the intended age filter.");
        captured.Target.ValidateCurrent();
        var fresh = Capture(item.Home, item.Path, token);
        if (!captured.Members.SequenceEqual(fresh.Members))
            throw new InvalidOperationException("The Cursor chat or one of its files changed after inspection. Inspect it again.");
    }

    internal static async Task<bool> RecycleAsync(AgentHistoryItem item, nint owner, CancellationToken token,
        int olderThanDays = 1, Action<string, object>? diagnostic = null)
    {
        var snapshot = item.Folder ?? throw new InvalidOperationException("The Cursor chat has no captured folder preview.");
        // Windows cannot recycle the whole folder while its members' data
        // leases are held. Recheck under leases at the native root callback,
        // release them, then use the host's exact-root Shell safeguards.
        // A separately restarted process/new name remains a live-system race.
        var result = await FolderRecycler.RecycleAsync(snapshot.Target, owner, token, diagnostic, () =>
        {
            AgentOwnerProtection.RequireStopped(AgentTool.Cursor);
            using var lease = Lease(item, token);
            Validate(item, olderThanDays, token);
        }).ConfigureAwait(false);
        return !result.Cancelled;
    }

    internal static IDisposable Lease(AgentHistoryItem item, CancellationToken token)
    {
        var snapshot = item.Folder ?? throw new InvalidOperationException("The Cursor chat has no captured folder preview.");
        var handles = new List<SafeFileHandle>();
        try
        {
            AgentFileTarget.ValidateAncestors(item.Path, includeSelf: true);
            for (string? ancestor = System.IO.Path.GetDirectoryName(item.Path); ancestor is not null; ancestor = System.IO.Path.GetDirectoryName(ancestor))
            {
                token.ThrowIfCancellationRequested(); handles.Add(OpenStable(ancestor, directory: true, share: 3));
            }
            foreach (var member in snapshot.Members)
            {
                token.ThrowIfCancellationRequested();
                string path = member.RelativePath.Length == 0 ? item.Path : System.IO.Path.Join(item.Path, member.RelativePath);
                var handle = OpenStable(path, member.IsDirectory, member.IsDirectory ? 5u : 4u); handles.Add(handle);
                if (Read(handle, member.IsDirectory) != member.Identity)
                    throw new IOException("A Cursor chat file changed after confirmation. Inspect again.");
            }
            // Verify complete membership after existing paths are pinned.
            // As in the host folder workflow, a separate external process can
            // still introduce new names; closing all clients remains required.
            var fresh = Capture(item.Home, item.Path, token);
            if (!snapshot.Members.SequenceEqual(fresh.Members)) throw new IOException("The Cursor chat membership changed after confirmation. Inspect again.");
            return new Leases(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    private static AgentFileIdentity ReadPath(string path, bool directory)
    {
        using var handle = Open(path, directory, 0x80, 7);
        return Read(handle, directory);
    }
    private static SafeFileHandle OpenStable(string path, bool directory, uint share)
    {
        // Provider checks precede read/list access; no database content is read.
        using var metadata = Open(path, directory, 0x80, 7);
        var before = Read(metadata, directory);
        var stable = Open(path, directory, directory ? 0x81u : 0x80000080u, share);
        try
        {
            if (Read(stable, directory) != before) throw new IOException("A Cursor path changed before its stable lease was acquired.");
            return stable;
        }
        catch { stable.Dispose(); throw; }
    }
    private static SafeFileHandle Open(string path, bool directory, uint access, uint share)
    {
        var handle = CreateFileW(path, access, share, 0, 3, 0x00300000 | (directory ? 0x02000000u : 0u), 0);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw new Win32Exception(Marshal.GetLastWin32Error(), "A Cursor chat path is unavailable or in use. Stop Cursor and its background agents before cleanup.");
    }
    private static AgentFileIdentity Read(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var data)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var attributes = (FileAttributes)data.Attributes;
        const FileAttributes cloud = (FileAttributes)(0x1000 | 0x40000 | 0x400000);
        if ((attributes & (FileAttributes.ReparsePoint | cloud)) != 0 || attributes.HasFlag(FileAttributes.Directory) != directory
            || !directory && (data.Links != 1 || attributes.HasFlag(FileAttributes.ReadOnly)))
            throw new IOException("Linked, cloud-only, read-only or multiply linked Cursor chat data is kept.");
        if (data.WriteHigh == 0 && data.WriteLow == 0)
            throw new IOException("A Cursor chat path has no usable modification time. It was kept.");
        byte[] provider = new byte[4200];
        if (RecognizesProvider(CfGetPlaceholderInfo(handle, 1, provider, (uint)provider.Length, out _))
            || RecognizesProvider(CfGetSyncRootInfoByHandle(handle, 2, provider, 1028, out _)))
            throw new IOException("Provider-managed Cursor paths are kept; cloud contents were not requested.");
        byte[] identity = new byte[24];
        if (!GetFileInformationByHandleEx(handle, 18, identity, 24)) throw new Win32Exception(Marshal.GetLastWin32Error());
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(identity.AsSpan(8)), high = BinaryPrimitives.ReadUInt64LittleEndian(identity.AsSpan(16));
        if (low == 0 && high == 0) throw new IOException("Windows did not provide a usable Cursor path identity.");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(identity), low, high,
            directory ? 0 : ((long)data.SizeHigh << 32) | data.SizeLow, ((long)data.WriteHigh << 32) | data.WriteLow);
    }
    private static bool RecognizesProvider(int result) => result >= 0 || result == unchecked((int)0x800700EA);
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
