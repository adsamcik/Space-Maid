using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpaceMaid.Core.Scanning;

internal sealed record NtfsJournalScope(string Path, bool Recursive, NtfsJournalCheckpoint Checkpoint,
    bool DirectoryLinksChecked = true);

public sealed partial class VolumeScanner
{
    // Data writes can be coalesced in the journal. Always fetch current stream
    // metadata by ID; only reuse namespace that the journal can still validate.
    private const uint NamespaceChanges = 0x100 | 0x200 | 0x1000 | 0x2000;
    private const uint ReparseChange = 0x100000;
    private const uint HardLinkChange = 0x10000;
    private const uint KnownChanges = 0x81FFFF77;

    private NtfsJournalCheckpoint? CaptureJournal(string path, CancellationToken token, Action<string, object>? diagnostic)
    {
        if (scopedSource is null) return null;
        try { return scopedSource.ReadJournal(Path.GetPathRoot(Path.GetFullPath(path))!, null, token).Checkpoint; }
        catch (Exception error) when (IncrementalUnavailable(error))
        { diagnostic?.Invoke("ntfs-journal-unavailable", new { Path = path, Error = error.Message }); return null; }
    }

    internal static void AttachJournal(ScanSnapshot snapshot, bool recursive, NtfsJournalCheckpoint? checkpoint)
    {
        if (checkpoint is { } point && snapshot.HasAllocationData && snapshot.IncludesAllDataStreams && snapshot.WarningCount == 0)
            snapshot.JournalScopes = [new(snapshot.RootPath, recursive, point, !snapshot.HasCompleteVolumeLinks)];
    }

    internal static IReadOnlyList<NtfsJournalScope> MergeJournalScopes(ScanSnapshot original,
        IReadOnlyList<ScanRechecker.Replacement> replacements)
    {
        var scopes = original.JournalScopes.Where(scope => !replacements.Any(replacement =>
            replacement.Recursive && Within(replacement.Path, scope.Path)
            || !scope.Recursive && SamePath(replacement.Path, scope.Path))).ToList();
        foreach (var replacement in replacements) scopes.AddRange(replacement.Fresh.JournalScopes);
        return scopes;
    }

    private ScanSnapshot? TryReadIncremental(string path, bool recursive, ScanSnapshot previous,
        CancellationToken token, Action<string, object>? diagnostic)
    {
        if (scopedSource is null || !previous.HasAllocationData || !previous.IncludesAllDataStreams
            || previous.JournalScopes.Count == 0 || !SamePath(Path.GetPathRoot(path)!, Path.GetPathRoot(previous.RootPath)!)) return null;
        try
        {
            var timer = Stopwatch.StartNew();
            long allocatedBefore = GC.GetTotalAllocatedBytes(false);
            ScanRechecker.ValidateAncestors(path);
            var rootIdentity = ReadFolderIdentity(path);
            int? oldLink = previous.FindEntry(path);
            if (oldLink is { } known && previous.Records[previous.Entry(known).FileIndex].Identity != rootIdentity) return null;
            if (oldLink is null)
            {
                // A verified Shell move changes the root's path, not its NTFS
                // identity or its children's parent IDs. Reuse that tree too.
                for (int i = 0; i < previous.Records.Length; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    if (previous.Records[i].Identity == rootIdentity) { oldLink = previous.PrimaryLink(i); break; }
                }
            }
            if (oldLink is not { } rootLink || !previous.Entry(rootLink).IsDirectory || previous.Entry(rootLink).IsIncomplete
                || previous.Records[previous.Entry(rootLink).FileIndex].Identity != rootIdentity) return null;
            string? oldPath = previous.EntryPath(rootLink);
            if (oldPath is null) return null;
            // USNs from different journal generations cannot be ordered. A
            // newer specific scope supersedes the older covering inventory.
            var evidence = previous.JournalScopes.Select((scope, index) => (Scope: scope, Index: index)).Where(item =>
                item.Scope.Recursive && Within(item.Scope.Path, oldPath) || !recursive && SamePath(item.Scope.Path, oldPath))
                .OrderByDescending(item => item.Scope.Path.Length).ThenByDescending(item => item.Index)
                .Select(item => item.Scope).FirstOrDefault();
            if (evidence is null) return null;
            string volume = Path.GetPathRoot(path)!;
            var delta = scopedSource.ReadJournal(volume, evidence.Checkpoint, token);
            if (delta.Changes.Any(change => (change.Reason & (HardLinkChange | ~KnownChanges)) != 0))
                throw new IOException("Ambiguous namespace changes need a current directory check.");

            var records = new Dictionary<FileIdentity, FileRecord>();
            var children = new Dictionary<FileIdentity, List<(FileIdentity Identity, string Name)>>();
            Import(previous, rootLink, recursive);
            if (delta.Changes.Any(change => (change.Reason & ReparseChange) != 0
                && records.TryGetValue(change.Identity, out var record) && record.IsDirectory))
                throw new IOException("A directory's reparse behavior changed; its paths need a current check.");
            var originalIds = records.Keys.ToHashSet();
            var dirty = delta.Changes.Where(change => (change.Reason & NamespaceChanges) != 0)
                .Select(change => change.ParentIdentity).ToHashSet();
            var reached = new HashSet<FileIdentity> { rootIdentity };
            var links = new List<(FileIdentity Identity, FileIdentity Parent, string Name)>();
            var pending = new Queue<(FileIdentity Identity, string Path)>();
            pending.Enqueue((rootIdentity, path));
            int directoriesRead = 0;
            while (pending.TryDequeue(out var directory))
            {
                token.ThrowIfCancellationRequested();
                if (!evidence.DirectoryLinksChecked && (records[directory.Identity].Attributes & FileAttributes.ReparsePoint) != 0
                    && CloudFiles.HasUnsupportedLink(directory.Path))
                    throw new IOException("A linked folder needs a current coverage check.");
                if (!children.ContainsKey(directory.Identity) || dirty.Contains(directory.Identity))
                {
                    ScanRechecker.ValidateAncestors(directory.Path);
                    bool introduced = !children.ContainsKey(directory.Identity);
                    var fresh = ReadDirectoryCore(directory.Path, introduced && recursive, token, allStreams: false,
                        allowBasic: false, diagnostic: diagnostic);
                    if (fresh.WarningCount > 0 || fresh.Records[fresh.RootIndex].Identity != directory.Identity)
                        throw new IOException("A changed folder's namespace could not be validated.");
                    Import(fresh, -1, introduced && recursive);
                    if (introduced && recursive)
                        dirty.ExceptWith(fresh.Records.Where(record => record.IsDirectory).Select(record => record.Identity));
                    directoriesRead += introduced && recursive ? fresh.DirectoryCount : 1;
                    diagnostic?.Invoke("incremental-directory-read", new { directory.Path, Recursive = introduced && recursive });
                }
                foreach (var child in children[directory.Identity])
                {
                    links.Add((child.Identity, directory.Identity, child.Name));
                    bool first = reached.Add(child.Identity);
                    if (recursive && first && records[child.Identity].IsDirectory)
                        pending.Enqueue((child.Identity, Path.Join(directory.Path, child.Name)));
                }
            }

            var ids = reached.ToArray();
            var live = new Dictionary<FileIdentity, FileRecord>(ids.Length);
            for (int offset = 0; offset < ids.Length; offset += ScanPipeProtocol.MaximumFileIds)
            {
                token.ThrowIfCancellationRequested();
                var batch = ids.AsSpan(offset, Math.Min(ScanPipeProtocol.MaximumFileIds, ids.Length - offset)).ToArray();
                var expected = batch.ToHashSet();
                int received = 0;
                scopedSource.AcquireFiles(volume, batch, block =>
                {
                    token.ThrowIfCancellationRequested();
                    if (block.Data.IsEmpty) return;
                    var parsed = new ScanBuilder();
                    LayoutParser.Parse(block.Data.Span, parsed);
                    if (parsed.Count != LayoutParser.U32(block.Data.Span, 0)) throw new InvalidDataException("Duplicate NTFS metadata.");
                    foreach (var record in parsed.Files)
                    {
                        if (!expected.Contains(record.Identity) || !live.TryAdd(record.Identity, record)
                            || record.IsDirectory != records[record.Identity].IsDirectory
                            || record.IsDirectory && ((record.Attributes ^ records[record.Identity].Attributes) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("A file's identity or type changed during its metadata check.");
                        received++;
                    }
                }, token);
                if (received != batch.Length) throw new IOException("An NTFS file disappeared during its metadata check.");
            }
            var after = scopedSource.ReadJournal(volume, delta.Checkpoint, token);
            if (after.Changes.Any(change => (change.Reason & (HardLinkChange | ~KnownChanges)) != 0
                || (change.Reason & ReparseChange) != 0 && children.ContainsKey(change.Identity)
                || (change.Reason & NamespaceChanges) != 0
                    && (children.ContainsKey(change.ParentIdentity) || originalIds.Contains(change.Identity) || reached.Contains(change.Identity))))
                throw new IOException("The folder membership changed during its metadata check.");
            ScanRechecker.ValidateAncestors(path);
            if (ReadFolderIdentity(path) != rootIdentity) throw new IOException("The folder was replaced during its metadata check.");
            var builder = new ScanBuilder { AllStreams = true };
            builder.RootIndex = builder.AddFile(live[rootIdentity]);
            foreach (var link in links)
            {
                token.ThrowIfCancellationRequested();
                builder.AddLink(builder.AddFile(live[link.Identity]), link.Parent, link.Name);
            }
            var result = builder.Build(path, "Incremental NTFS metadata", timer.Elapsed, allocatedBefore, token);
            AttachJournal(result, recursive, delta.Checkpoint);
            diagnostic?.Invoke("incremental-ntfs-complete", new { Path = path, recursive, result.FileCount, DirectoriesRead = directoriesRead,
                Changes = delta.Changes.Count, ElapsedMs = timer.Elapsed.TotalMilliseconds });
            return result;

            void Import(ScanSnapshot snapshot, int selected, bool descendants)
            {
                var entry = snapshot.Entry(selected);
                var queue = new Queue<int>(); queue.Enqueue(entry.FileIndex);
                while (queue.TryDequeue(out int file))
                {
                    token.ThrowIfCancellationRequested();
                    var record = snapshot.Records[file]; records[record.Identity] = record;
                    var names = new List<(FileIdentity, string)>();
                    foreach (int child in snapshot.Children(file))
                    {
                        var name = snapshot.Entry(child); var metadata = snapshot.Records[name.FileIndex];
                        records[metadata.Identity] = metadata;
                        names.Add((metadata.Identity, name.Name));
                        if (descendants && metadata.IsDirectory) queue.Enqueue(name.FileIndex);
                    }
                    children[record.Identity] = names;
                }
            }
        }
        catch (Exception error) when (IncrementalUnavailable(error))
        {
            diagnostic?.Invoke("incremental-ntfs-fallback", new { Path = path, Error = error.Message });
            return null;
        }
    }

    private static unsafe FileIdentity ReadFolderIdentity(string path)
    {
        using var handle = NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), 0x80, 7, 0, 3,
            NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        byte* buffer = stackalloc byte[24];
        if (!NativeMethods.GetFileInformationByHandleEx(handle, 18, buffer, 24)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new(LayoutParser.U64(new ReadOnlySpan<byte>(buffer, 24), 8), LayoutParser.U64(new ReadOnlySpan<byte>(buffer, 24), 16));
    }

    private static bool IncrementalUnavailable(Exception error) => error is IOException or InvalidDataException
        or Win32Exception or NotSupportedException or UnauthorizedAccessException;
    private static bool SamePath(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
        .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.Ordinal);
    private static bool Within(string root, string path) => SamePath(root, path)
        || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
