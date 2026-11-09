using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.IO;

public sealed partial class DiskAccess
{
    public RecycleLeftoverReview ReviewRecycleLeftovers(string drive, CancellationToken token)
    {
        if (drive.Length != 3 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':' || drive[2] != '\\'
            || GetDrive(drive).DriveType != DriveType.Fixed || !CanUseNtfs(drive))
            throw new IOException("Choose a local fixed NTFS drive.");
        using var account = WindowsIdentity.GetCurrent();
        string sid = account.User?.Value ?? throw new IOException("Windows account could not be identified.");
        return ReviewRecycleLeftoversAt(Path.Join(drive, "$Recycle.Bin", sid), DateTime.UtcNow.AddDays(-1), token);
    }

    // Separate fixture entry point. The public path is always the current SID's
    // directory on the selected fixed drive, never an arbitrary input folder.
    internal RecycleLeftoverReview ReviewRecycleLeftoversAt(string bin, DateTime changedBefore, CancellationToken token)
    {
        using var directories = PinLeftoverDirectories(bin, destination: false, token);
        var scope = InspectPermanentDeleteHandle(directories.Last);
        CheckBinNameLookup(directories.Last);
        int budget = 250_000;
        var names = ReadLeftoverNames(directories.Last, ref budget, token);
        var allNames = names.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<RecycleLeftover>();
        long cutoff = changedBefore.ToFileTimeUtc();
        foreach (var item in names)
        {
            token.ThrowIfCancellationRequested();
            if (!item.Name.StartsWith("$R", StringComparison.OrdinalIgnoreCase) || item.Name.Length <= 2
                || allNames.Contains(MateName(item.Name)) || !SafeLeftoverName(item.Name)) continue;

            var record = item.Record;
            long changed = item.Changed;
            ScanSnapshot? contents = null;
            var revisions = new Dictionary<FileIdentity, long>();
            string? blocked = null;
            bool canRecover = false;
            try
            {
                string path = Path.Join(bin, item.Name);
                using var handle = OpenPermanentDeleteHandle(path, record.IsDirectory ? 0x81u : 0x80u, 3);
                var live = InspectPermanentDeleteHandle(handle);
                CheckPermanentDeleteAttributes(live.Attributes, record.IsDirectory);
                if (live.Identity == default || live.Identity != record.Identity || live.Volume != scope.Volume)
                    throw new IOException("Entry changed during review.");
                changed = ReadLeftoverChangeTime(handle);
                if (changed <= 0 || changed > cutoff)
                    throw new IOException("Recently changed. Review again after 24 hours.");
                record = record with { Attributes = live.Attributes, CreationFileTime = live.Created, LastWriteFileTime = live.Written };
                canRecover = true;
                if (record.IsDirectory)
                {
                    var builder = new ScanBuilder { RootIndex = 0 };
                    builder.AddFile(record);
                    ReadLeftoverTree(path, handle, record.Identity, scope.Volume, builder, revisions, 0, ref budget, token);
                    contents = builder.Build(path, "Recycle Bin metadata", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), token);
                    if (contents.WarningCount > 0) blocked = "Linked, protected or shared contents. Recovery only.";
                    else if (contents.NameCount >= MaximumPermanentDeleteEntries) blocked = "Too many entries for one checked deletion. Recovery only.";
                }
            }
            catch (Exception error) when (IsLeftoverFailure(error)) { blocked = error.Message; }
            rows.Add(new(item.Name, record, changed, contents, revisions, canRecover, blocked));
        }
        return new(this, bin, scope.Identity, scope.Volume, rows.AsReadOnly());
    }

    public sealed class RecycleLeftover
    {
        internal RecycleLeftover(string name, FileRecord record, long changed, ScanSnapshot? contents,
            IReadOnlyDictionary<FileIdentity, long> revisions, bool canRecover, string? blocked)
        { Name = name; Record = record; Changed = changed; Contents = contents; Revisions = revisions; CanRecover = canRecover; Blocked = blocked; }

        internal FileRecord Record { get; }
        internal long Changed { get; }
        internal ScanSnapshot? Contents { get; }
        internal IReadOnlyDictionary<FileIdentity, long> Revisions { get; }
        public string Name { get; }
        public bool IsDirectory => Record.IsDirectory;
        public bool CanRecover { get; }
        public bool CanDelete => CanRecover && Blocked is null;
        public string? Blocked { get; }
        public long? AllocatedBytes => IsDirectory ? Contents?.AllocatedBytes : Record.AllocatedBytes;
        public long Written => Record.LastWriteFileTime;
        public int? ContentCount => Contents?.NameCount;

        public IReadOnlyList<string> ReviewContents(int offset, int count) => Contents is not { } tree || offset >= tree.NameCount
            ? [] : Enumerable.Range(offset, Math.Min(count, tree.NameCount - offset))
            .Select(link => tree.EntryPath(link)).OfType<string>().Select(path => path[(tree.RootPath.Length + 1)..]).ToArray();
    }

    public sealed record RecycleLeftoverResult(string Name, bool Completed, string? RecoveryPath, string? Issue);

    public sealed class RecycleLeftoverReview
    {
        private readonly DiskAccess _files;
        private readonly FileIdentity _identity;
        private readonly ulong _volume;

        internal RecycleLeftoverReview(DiskAccess files, string path, FileIdentity identity, ulong volume, IReadOnlyList<RecycleLeftover> entries)
        { _files = files; Path = path; _identity = identity; _volume = volume; Entries = entries; }

        public string Path { get; }
        public IReadOnlyList<RecycleLeftover> Entries { get; }

        public void Apply(IReadOnlyList<RecycleLeftover> approved, string destination, bool delete,
            IProgress<RecycleLeftoverResult> results, CancellationToken token)
        {
            var eligible = Entries.ToHashSet();
            if (approved.Count == 0 || approved.Distinct().Count() != approved.Count
                || approved.Any(row => !eligible.Contains(row) || (delete ? !row.CanDelete : !row.CanRecover)))
                throw new IOException("Review and select eligible entries again.");
            if (!string.Equals(System.IO.Path.GetPathRoot(destination), System.IO.Path.GetPathRoot(Path), StringComparison.OrdinalIgnoreCase)
                || destination.Equals(Path, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(Path + "\\", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a folder on the same drive, outside the Recycle Bin.");
            using var source = _files.PinLeftoverDirectories(Path, destination: false, token);
            var live = _files.InspectPermanentDeleteHandle(source.Last);
            if (live.Identity != _identity || live.Volume != _volume)
                throw new IOException("The account's Recycle Bin folder changed. Review again.");
            using var target = _files.PinLeftoverDirectories(destination, destination: true, token);
            if (_files.InspectPermanentDeleteHandle(target.Last).Volume != live.Volume)
                throw new IOException("Choose a recovery folder on the same drive.");
            // Check identities too, so an alternate NTFS name cannot point back into the bin.
            var sourceParent = System.IO.Path.GetDirectoryName(Path);
            var binIdentity = string.Equals(System.IO.Path.GetFileName(sourceParent), "$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                ? _files.InspectPermanentDeleteHandle(source.Handles[^2]).Identity : live.Identity;
            if (target.Handles.Any(handle => _files.InspectPermanentDeleteHandle(handle).Identity == binIdentity))
                throw new IOException("Choose a folder outside the Recycle Bin.");
            foreach (var item in approved)
            {
                token.ThrowIfCancellationRequested();
                string name = item.Name.Length <= 247 ? "Recovered-" + item.Name[2..] : "R-" + item.Name[2..];
                string staged = System.IO.Path.Join(destination, name);
                bool moved = false;
                try
                {
                    void CheckMate()
                    {
                        _files.CheckBinNameLookup(source.Last);
                        _files.RequireMissingMate(Path, item.Name);
                    }
                    CheckMate();
                    string sourcePath = System.IO.Path.Join(Path, item.Name);
                    long stagedRevision = 0;
                    using (var attributes = _files.OpenPermanentDeleteHandle(sourcePath, 0x80, 7))
                        VerifyPermanentDeleteEntry(_files.InspectPermanentDeleteHandle(attributes), item.Record, live.Volume);
                    using (var handle = _files.OpenPermanentDeleteHandle(sourcePath,
                        item.IsDirectory ? 0x10081u : 0x80010080u, item.IsDirectory ? 3u : 0u))
                    {
                        VerifyPermanentDeleteEntry(_files.InspectPermanentDeleteHandle(handle), item.Record, live.Volume);
                        if (_files.ReadLeftoverChangeTime(handle) != item.Changed)
                            throw new IOException("Entry changed after review.");
                        if (delete)
                        {
                            _files.VerifyPermanentDeleteStreams(handle, item.IsDirectory);
                            if (!item.IsDirectory) _files.VerifyPermanentDeleteFile(handle, item.Record);
                        }
                        CheckMate();
                        token.ThrowIfCancellationRequested();
                        _files.RenameLeftover(handle, target.Last, name);
                        moved = true;
                        CheckMate();
                        if (delete && !item.IsDirectory)
                        {
                            _files.VerifyPermanentDeleteFile(handle, item.Record);
                            _files.VerifyPermanentDeleteStreams(handle, directory: false);
                            CheckMate();
                            token.ThrowIfCancellationRequested();
                            _files.MarkPermanentDelete(handle);
                        }
                        else if (delete)
                            stagedRevision = _files.ReadLeftoverChangeTime(handle);
                    }
                    if (delete && item.Contents is { } tree)
                        _files.DeleteReviewedTree(staged, -1, live.Volume, tree, token, CheckMate, (handle, identity) =>
                        {
                            long expected = identity == item.Record.Identity ? stagedRevision : item.Revisions.GetValueOrDefault(identity);
                            if (expected <= 0 || _files.ReadLeftoverChangeTime(handle) != expected)
                                throw new IOException("Folder contents changed after review.");
                        });
                    results.Report(new(item.Name, true, delete ? null : staged, null));
                }
                catch (Exception error) when (IsLeftoverFailure(error) || error is OperationCanceledException)
                {
                    results.Report(new(item.Name, false, moved ? staged : null, error.Message));
                    token.ThrowIfCancellationRequested();
                }
            }
        }
    }

    private static bool IsLeftoverFailure(Exception error) => error is IOException or Win32Exception
        or UnauthorizedAccessException or InvalidDataException or OverflowException or ArgumentException;

    private static bool SafeLeftoverName(string name) => name.Length > 0 && name is not ("." or "..")
        && !name.EndsWith('.') && !name.EndsWith(' ') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string MateName(string name) => "$I" + name[2..];

    private void RequireMissingMate(string path, string name)
    {
        try { using var mate = OpenPermanentDeleteHandle(Path.Join(path, MateName(name)), 0x80, 7); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return; }
        throw new IOException("A Recycle Bin record now matches this entry. It was kept.");
    }

    private unsafe void CheckBinNameLookup(SafeFileHandle handle)
    {
        uint flags;
        ReadPermanentDeleteInformation(handle, 23, (byte*)&flags, sizeof(uint)); // FileCaseSensitiveInfo
        if (flags != 0) throw new IOException("Case-sensitive Recycle Bin folders are kept.");
    }

    private unsafe long ReadLeftoverChangeTime(SafeFileHandle handle)
    {
        byte* basic = stackalloc byte[40];
        ReadPermanentDeleteInformation(handle, 0, basic, 40);
        return LayoutParser.I64(new ReadOnlySpan<byte>(basic, 40), 24);
    }

    private unsafe void RenameLeftover(SafeFileHandle handle, SafeFileHandle destination, string name)
    {
        int nameOffset = 2 * IntPtr.Size + sizeof(uint);
        int structureSize = nameOffset + sizeof(uint); // FILE_RENAME_INFORMATION includes one WCHAR and alignment padding.
        byte[] info = new byte[structureSize + name.Length * sizeof(char)];
        nint directory = destination.DangerousGetHandle();
        MemoryMarshal.Write(info.AsSpan(IntPtr.Size), in directory);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(2 * IntPtr.Size), name.Length * sizeof(char));
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(info.AsSpan(nameOffset));
        nint* statusBlock = stackalloc nint[2];
        int status;
        fixed (byte* pointer = info)
            status = NtSetInformationFile(handle, statusBlock, pointer, (uint)info.Length, 10); // FileRenameInformation
        GC.KeepAlive(destination);
        if (status != 0)
            throw new IOException("Could not move the reviewed entry. " + new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))).Message);
    }

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtSetInformationFile(SafeFileHandle handle, nint* statusBlock, void* information, uint length, int kind);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    private LeftoverDirectoryLease PinLeftoverDirectories(string path, bool destination, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path) is not { Length: 3 } root
            || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\'
            || Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) != path
            || path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(name =>
                !SafeLeftoverName(name) || destination && (ProtectedPermanentDeleteName(name)
                    || name.Equals("Recycler", StringComparison.OrdinalIgnoreCase))))
            throw new IOException("Choose a normal local folder outside protected storage.");
        var ancestors = new Stack<string>();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
        var lease = new LeftoverDirectoryLease();
        try
        {
            foreach (string current in ancestors)
            {
                token.ThrowIfCancellationRequested();
                uint access = current != path || destination ? 0xA0u : 0x81u; // TRAVERSE | READ_ATTRIBUTES for relative rename target.
                var handle = OpenPermanentDeleteHandle(current, access, 3);
                lease.Handles.Add(handle);
                CheckPermanentDeleteAttributes(InspectPermanentDeleteHandle(handle).Attributes, directory: true);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private sealed class LeftoverDirectoryLease : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];
        internal SafeFileHandle Last => Handles[^1];
        public void Dispose()
        { for (int index = Handles.Count - 1; index >= 0; index--) Handles[index].Dispose(); }
    }

    private sealed record LeftoverName(string Name, FileRecord Record, long Changed);

    private List<LeftoverName> ReadLeftoverNames(SafeFileHandle directory, ref int budget, CancellationToken token)
    {
        var names = new List<LeftoverName>();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(DirectoryRecordBufferSize);
        try
        {
            for (int error = ReadDirectoryRecords(directory, buffer); error != NativeMethods.NoMoreFiles;
                error = ReadDirectoryRecords(directory, buffer))
            {
                if (error != 0) throw new Win32Exception(error);
                int offset = 0;
                while (offset >= 0)
                {
                    token.ThrowIfCancellationRequested();
                    var row = new DirectoryRecord(buffer, ref offset);
                    if (row.Name.SequenceEqual(".") || row.Name.SequenceEqual("..")) continue;
                    if (budget-- <= 0) throw new IOException("Review reached its 250,000-entry limit.");
                    if (row.LogicalBytes < 0 || row.AllocatedBytes < 0) throw new InvalidDataException("Invalid file sizes.");
                    names.Add(new(new string(row.Name), new(row.Identity, row.Attributes, row.LogicalBytes,
                        row.AllocatedBytes, row.LastWrite, row.Created), row.Changed));
                }
            }
            return names;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void ReadLeftoverTree(string path, SafeFileHandle directory, FileIdentity parent, ulong volume,
        ScanBuilder builder, Dictionary<FileIdentity, long> revisions, int depth, ref int budget, CancellationToken token)
    {
        if (depth >= 128) throw new IOException("Folder is too deeply nested to check. Recovery only.");
        foreach (var child in ReadLeftoverNames(directory, ref budget, token))
        {
            token.ThrowIfCancellationRequested();
            var record = child.Record;
            if (!SafeLeftoverName(child.Name) || ProtectedPermanentDeleteName(child.Name) || record.Identity == default
                || (record.Attributes & PermanentDeleteUnsafeAttributes) != 0)
            { builder.Warn("Protected entry skipped.", parent); continue; }
            if (!revisions.TryAdd(record.Identity, child.Changed))
            { builder.Warn("Shared entry skipped.", parent); continue; }
            if (record.IsDirectory)
            {
                string childPath = Path.Join(path, child.Name);
                using var handle = OpenPermanentDeleteHandle(childPath, 0x81, 3);
                var live = InspectPermanentDeleteHandle(handle);
                CheckPermanentDeleteAttributes(live.Attributes, directory: true);
                if (live.Identity != record.Identity || live.Volume != volume)
                    throw new IOException("Folder changed during review. Recovery only.");
                record = record with { CreationFileTime = live.Created, LastWriteFileTime = live.Written };
                revisions[record.Identity] = ReadLeftoverChangeTime(handle);
                builder.AddLink(builder.AddFile(record), parent, child.Name);
                ReadLeftoverTree(childPath, handle, record.Identity, volume, builder, revisions, depth + 1, ref budget, token);
            }
            else builder.AddLink(builder.AddFile(record), parent, child.Name);
        }
    }
}
