using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.IO;

public sealed partial class DiskAccess
{
    // Pin a whole confirmed subtree before mutating it. There is no recursive
    // pathname deletion: only these exact handles can be marked for removal.
    // The bound prevents an unusually large root exhausting process handles;
    // the caller keeps that root and can continue with independent roots.
    internal const int MaximumPermanentDeleteEntries = 100_000;
    private const FileAttributes PermanentDeleteUnsafeAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline
        | (FileAttributes)(0x40000 | 0x400000); // RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS

    internal void DeleteConfirmedDirectoryTree(string path, FileIdentity rootIdentity, ulong volume,
        ScanSnapshot current, CancellationToken token, Action? mutationStarted)
    {
        token.ThrowIfCancellationRequested();
        int? rootLink = current.FindEntry(path);
        if (!current.HasAllocationData || rootLink is null || !current.Entry(rootLink.Value).IsDirectory
            || current.Entry(rootLink.Value).IsIncomplete
            || current.Records[current.Entry(rootLink.Value).FileIndex].Identity != rootIdentity)
            throw new InvalidOperationException("A complete native check of the confirmed folder is required before permanent deletion.");

        var ancestors = new Stack<string>();
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent)) ancestors.Push(parent);
        var parentLeases = new List<SafeFileHandle>();
        var entries = new List<PermanentDeleteEntry>();
        try
        {
            foreach (string ancestor in ancestors)
            {
                token.ThrowIfCancellationRequested();
                using var metadata = OpenPermanentDeleteHandle(ancestor, 0x80, 7);
                var before = InspectPermanentDeleteHandle(metadata);
                CheckPermanentDeleteAttributes(before.Attributes, directory: true);
                var lease = OpenPermanentDeleteHandle(ancestor, 0x81, 3); // LIST_DIRECTORY | READ_ATTRIBUTES, no rename.
                parentLeases.Add(lease);
                var after = InspectPermanentDeleteHandle(lease);
                CheckPermanentDeleteAttributes(after.Attributes, directory: true);
                if (before.Identity != after.Identity || before.Volume != after.Volume)
                    throw new IOException("An ancestor changed while its cleanup lease was acquired; folder kept.");
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(ancestor));
                if (ProtectedPermanentDeleteName(name)) throw new IOException("Protected filesystem storage is kept.");
            }

            var pending = new Stack<(int Link, string Path)>();
            pending.Push((rootLink.Value, path));
            var observed = new HashSet<int>();
            while (pending.TryPop(out var next))
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= MaximumPermanentDeleteEntries)
                    throw new IOException($"This folder contains more than {MaximumPermanentDeleteEntries:N0} entries. It was kept because its complete cleanup lease is too large.");
                var entry = current.Entry(next.Link);
                var record = current.Records[entry.FileIndex];
                if (!observed.Add(entry.FileIndex) || entry.IsIncomplete)
                    throw new IOException("Shared or incompletely checked contents are kept.");
                CheckPermanentDeleteAttributes(record.Attributes, record.IsDirectory);
                if (ProtectedPermanentDeleteName(Path.GetFileName(next.Path))) throw new IOException("Protected filesystem storage is kept.");

                // Attribute-only metadata is checked before any readable/list
                // acquisition, so provider placeholders cannot be recalled.
                using var metadata = OpenPermanentDeleteHandle(next.Path, 0x80, 7);
                var before = InspectPermanentDeleteHandle(metadata);
                VerifyPermanentDeleteEntry(before, record, volume);
                // GENERIC_READ on files creates an effective exclusive lease
                // against existing readers/writers; attribute access alone does not.
                var handle = OpenPermanentDeleteHandle(next.Path, record.IsDirectory ? 0x10081u : 0x80010080u,
                    record.IsDirectory ? 3u : 0u);
                var captured = new PermanentDeleteEntry(next.Path, entry.FileIndex, record, handle);
                entries.Add(captured);
                var after = InspectPermanentDeleteHandle(handle);
                VerifyPermanentDeleteEntry(after, record, volume);
                if (before != after) throw new IOException("Contents changed while their cleanup lease was acquired; folder kept.");
                VerifyPermanentDeleteStreams(handle, record.IsDirectory);
                if (!record.IsDirectory)
                {
                    VerifyPermanentDeleteFile(handle, record);
                    continue;
                }
                foreach (int child in current.Children(entry.FileIndex))
                {
                    string name = new(current.Name(child));
                    if (name is "." or ".." || name.IndexOfAny(['\\', '/']) >= 0)
                        throw new IOException("An invalid child name prevents permanent deletion.");
                    pending.Push((child, Path.Join(next.Path, name)));
                }
            }

            // New/missing names are detected before the first mutation. A later
            // addition is never visited and prevents removing its parent.
            foreach (var entry in entries.Where(entry => entry.Record.IsDirectory))
            {
                token.ThrowIfCancellationRequested();
                var expectedNames = current.Children(entry.FileIndex).Select(child => new string(current.Name(child)))
                    .ToHashSet(StringComparer.Ordinal);
                foreach (string child in EnumerateFileSystemEntries(entry.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (!expectedNames.Remove(Path.GetFileName(child)))
                        throw new IOException("Folder contents changed after checking; folder kept.");
                }
                if (expectedNames.Count != 0) throw new IOException("Folder contents changed after checking; folder kept.");
            }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                VerifyPermanentDeleteEntry(InspectPermanentDeleteHandle(entry.Handle), entry.Record, volume);
                VerifyPermanentDeleteStreams(entry.Handle, entry.Record.IsDirectory);
            }
            for (int index = entries.Count - 1; index >= 0; index--)
            {
                token.ThrowIfCancellationRequested();
                if (index == entries.Count - 1) mutationStarted?.Invoke();
                token.ThrowIfCancellationRequested();
                MarkPermanentDelete(entries[index].Handle);
                entries[index].Handle.Dispose();
            }
        }
        finally
        {
            for (int index = entries.Count - 1; index >= 0; index--) entries[index].Handle.Dispose();
            for (int index = parentLeases.Count - 1; index >= 0; index--) parentLeases[index].Dispose();
        }
    }

    private SafeFileHandle OpenPermanentDeleteHandle(string path, uint access, uint sharing)
    {
        var handle = OpenHandle(NativeMethods.ExtendedPath(path), access, sharing, 0, 3,
            NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint | 0x00100000, 0); // OPEN_NO_RECALL
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error);
    }

    private unsafe PermanentDeleteMetadata InspectPermanentDeleteHandle(SafeFileHandle handle)
    {
        byte* basic = stackalloc byte[40]; ReadPermanentDeleteInformation(handle, 0, basic, 40);
        byte* identity = stackalloc byte[24]; ReadPermanentDeleteInformation(handle, 18, identity, 24);
        var b = new ReadOnlySpan<byte>(basic, 40); var i = new ReadOnlySpan<byte>(identity, 24);
        return new(new(LayoutParser.U64(i, 8), LayoutParser.U64(i, 16)), LayoutParser.U64(i, 0),
            (FileAttributes)LayoutParser.U32(b, 32), LayoutParser.I64(b, 0), LayoutParser.I64(b, 16));
    }

    private static void VerifyPermanentDeleteEntry(PermanentDeleteMetadata actual, FileRecord expected, ulong volume)
    {
        CheckPermanentDeleteAttributes(actual.Attributes, expected.IsDirectory);
        if (actual.Identity == default || actual.Identity != expected.Identity || actual.Volume != volume
            || actual.Created != expected.CreationFileTime || actual.Written != expected.LastWriteFileTime)
            throw new IOException("Folder contents changed after checking; folder kept.");
    }

    private static void CheckPermanentDeleteAttributes(FileAttributes attributes, bool directory)
    {
        if (((attributes & FileAttributes.Directory) != 0) != directory
            || (attributes & PermanentDeleteUnsafeAttributes) != 0
            || !directory && (attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
            throw new IOException("Linked, offline, protected or replaced contents are kept.");
    }

    private static bool ProtectedPermanentDeleteName(string name) => name.StartsWith('$')
        || name.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);

    private unsafe void VerifyPermanentDeleteFile(SafeFileHandle handle, FileRecord expected)
    {
        byte* standard = stackalloc byte[24]; ReadPermanentDeleteInformation(handle, 1, standard, 24);
        var data = new ReadOnlySpan<byte>(standard, 24);
        if (LayoutParser.U32(data, 16) != 1 || standard[20] != 0 || LayoutParser.I64(data, 8) != expected.LogicalBytes)
            throw new IOException("Changed, shared or unavailable files are kept.");
    }

    private unsafe void VerifyPermanentDeleteStreams(SafeFileHandle handle, bool directory)
    {
        // Named streams on either files or directories can hold data outside
        // the rebuildable-content contract. Directories have no default data
        // stream, and Windows reports ERROR_HANDLE_EOF when none are present.
        byte* streams = stackalloc byte[1024];
        if (!ReadHandleInformation(handle, 7, streams, 1024))
        {
            int error = Marshal.GetLastPInvokeError();
            if (directory && error == NativeMethods.HandleEof) return;
            throw new Win32Exception(error);
        }
        if (directory) throw new IOException("Folders with additional data streams are kept.");
        var stream = new ReadOnlySpan<byte>(streams, 1024);
        if (LayoutParser.U32(stream, 0) != 0 || LayoutParser.U32(stream, 4) != 14
            || !new ReadOnlySpan<char>(streams + 24, 7).SequenceEqual("::$DATA"))
            throw new IOException("Files with additional data streams are kept.");
    }

    private unsafe void ReadPermanentDeleteInformation(SafeFileHandle handle, int kind, byte* buffer, uint length)
    { if (!ReadHandleInformation(handle, kind, buffer, length)) throw new Win32Exception(Marshal.GetLastPInvokeError()); }

    private unsafe void MarkPermanentDelete(SafeFileHandle handle)
    {
        byte delete = 1;
        if (!WriteHandleInformation(handle, 4, &delete, 1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private sealed record PermanentDeleteEntry(string Path, int FileIndex, FileRecord Record, SafeFileHandle Handle);
    private readonly record struct PermanentDeleteMetadata(FileIdentity Identity, ulong Volume,
        FileAttributes Attributes, long Created, long Written);
}
