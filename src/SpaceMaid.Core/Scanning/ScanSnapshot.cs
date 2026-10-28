using SpaceMaid.Core.Analysis;

namespace SpaceMaid.Core.Scanning;

public readonly record struct FileIdentity(ulong Low, ulong High = 0);

internal readonly record struct FileRecord(
    FileIdentity Identity, FileAttributes Attributes, long LogicalBytes,
    long AllocatedBytes, long LastWriteFileTime, long CreationFileTime = 0)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

internal readonly record struct NameLink(
    int FileIndex, FileIdentity ParentIdentity, int NameOffset, int NameLength,
    int ParentIndex = -1);

public sealed record PreliminaryFile(string Name, string? Path, long LogicalBytes, long AllocatedBytes);
public sealed record ScanPreliminary(long LogicalBytes, long AllocatedBytes, bool HasAllocationData,
    IReadOnlyList<PreliminaryFile> LargestFiles);
public sealed record ScanProgress(string Phase, int Files, TimeSpan Elapsed, string Backend = "",
    string? FallbackReason = null, ScanPreliminary? Preliminary = null);
public sealed record ScanMetrics(TimeSpan Acquisition, TimeSpan Aggregation, long ManagedAllocatedBytes,
    long PeakProcessWorkingSetBytes = 0);

public enum ScanSortColumn { Name, Allocated, Logical, Share, Modified, Created, Type, Extension, Attributes }
public readonly record struct ScanEntryMetadata(uint Attributes, long LastWriteFileTime,
    ulong? NativeIdLow, ulong? NativeIdHigh, long CreationFileTime = 0);

public readonly record struct ScanEntry(int LinkIndex, int FileIndex, int ParentIndex,
    string Name, bool IsDirectory, bool IsPrimaryName, bool IsShared, bool IsIncomplete,
    long LogicalBytes, long AllocatedBytes)
{
    // A second hard-link name is visible, but contributes no additional bytes.
    public long AttributedBytes(bool hasAllocation) => IsPrimaryName
        ? hasAllocation ? AllocatedBytes : LogicalBytes : 0;
}

public sealed class ScanSnapshot : IOpportunityInventory
{
    internal FileRecord[] Records { get; }
    internal NameLink[] Links { get; }
    internal int[] FirstLinks { get; }
    internal int[] ChildHeads { get; }
    internal int[] NextLinks { get; }
    internal long[] DirectoryLogical { get; }
    internal long[] DirectoryAllocated { get; }
    private readonly char[] _names;
    private readonly byte[] _entryFlags;
    internal char[] NameArena => _names;
    internal byte[] EntryFlags => _entryFlags;
    public bool RetainsEarlierNtfsMetadata { get; init; }
    // Assigned only while constructing a snapshot, before publishing it.
    internal IReadOnlyList<NtfsJournalScope> JournalScopes { get; set; } = [];

    public string RootPath { get; }
    public string Backend { get; }
    public bool IncludesAllDataStreams { get; }
    public bool HasAllocationData { get; }
    public bool HasCompleteVolumeLinks { get; }
    public int FileCount => Records.Length;
    public int NameCount => Links.Length;
    public int RootIndex { get; }
    public IReadOnlyList<string> Warnings { get; }
    public int WarningCount { get; }
    public int DirectoryCount { get; }
    public int RegularFileCount => FileCount - DirectoryCount;
    public string? FallbackReason { get; }
    public ScanMetrics Metrics { get; }
    public DateTime ScannedAtUtc { get; internal init; } = DateTime.UtcNow;
    public DateTime? LastRecheckUtc { get; internal init; }
    public IReadOnlyList<string> RecheckedPaths { get; internal init; } = [];
    public long LogicalBytes => DirectoryLogical[RootIndex];
    public long AllocatedBytes => DirectoryAllocated[RootIndex];

    internal ScanSnapshot(string rootPath, string backend, FileRecord[] records,
        NameLink[] links, char[] names, int rootIndex, int[] firstLinks,
        int[] childHeads, int[] nextLinks, long[] logical, long[] allocated,
        bool allStreams, bool allocationData, bool completeLinks,
        IReadOnlyList<string> warnings, ScanMetrics metrics, byte[] entryFlags,
        int warningCount, int directoryCount, string? fallbackReason)
    {
        RootPath = rootPath;
        Backend = backend;
        Records = records;
        Links = links;
        _names = names;
        RootIndex = rootIndex;
        FirstLinks = firstLinks;
        ChildHeads = childHeads;
        NextLinks = nextLinks;
        DirectoryLogical = logical;
        DirectoryAllocated = allocated;
        IncludesAllDataStreams = allStreams;
        HasAllocationData = allocationData;
        HasCompleteVolumeLinks = completeLinks;
        Warnings = warnings;
        Metrics = metrics;
        _entryFlags = entryFlags;
        WarningCount = warningCount;
        DirectoryCount = directoryCount;
        FallbackReason = fallbackReason;
    }

    public ScanEntry RootEntry => Entry(-1);

    public ScanEntry Entry(int linkIndex)
    {
        if (linkIndex < -1 || linkIndex >= Links.Length) throw new ArgumentOutOfRangeException(nameof(linkIndex));
        int file = linkIndex == -1 ? RootIndex : Links[linkIndex].FileIndex;
        return new(linkIndex, file, linkIndex == -1 ? -1 : Links[linkIndex].ParentIndex,
            linkIndex == -1 ? RootPath : new string(Name(linkIndex)), Records[file].IsDirectory,
            linkIndex == -1 || FirstLinks[file] == linkIndex, (_entryFlags[file] & 1) != 0,
            (_entryFlags[file] & 6) != 0, DirectoryLogical[file], DirectoryAllocated[file]);
    }

    public string? EntryPath(int linkIndex) => linkIndex == -1 ? RootPath : PathForLink(linkIndex);
    public int? FindEntry(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return -1;
        string prefix = root.EndsWith('\\') ? root : root + "\\";
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        int current = RootIndex, result = -1;
        foreach (string part in full[prefix.Length..].Split('\\'))
        {
            result = -1;
            int insensitive = -1; bool ambiguous = false;
            foreach (int child in Children(current))
            {
                if (Name(child).Equals(part, StringComparison.Ordinal)) { result = child; break; }
                if (Name(child).Equals(part, StringComparison.OrdinalIgnoreCase))
                { if (insensitive >= 0) ambiguous = true; insensitive = child; }
            }
            if (result < 0 && !ambiguous) result = insensitive;
            if (result < 0) return null;
            current = Links[result].FileIndex;
        }
        return result;
    }
    public int PrimaryLink(int fileIndex) => fileIndex == RootIndex ? -1 : FirstLinks[fileIndex];
    public bool ContainsSharedFileNames(int directoryIndex)
    {
        if ((uint)directoryIndex >= (uint)Records.Length || !Records[directoryIndex].IsDirectory)
            throw new ArgumentOutOfRangeException(nameof(directoryIndex));
        return (_entryFlags[directoryIndex] & 16) != 0;
    }
    public bool IsUnmeasuredDirectory(int fileIndex)
    {
        if ((uint)fileIndex >= (uint)Records.Length) throw new ArgumentOutOfRangeException(nameof(fileIndex));
        return Records[fileIndex].IsDirectory && (_entryFlags[fileIndex] & 8) != 0;
    }

    public ScanEntryMetadata GetMetadata(int linkIndex)
    {
        if (linkIndex < -1 || linkIndex >= Links.Length) throw new ArgumentOutOfRangeException(nameof(linkIndex));
        var file = Records[linkIndex == -1 ? RootIndex : Links[linkIndex].FileIndex];
        bool nativeIdentity = HasAllocationData;
        return new((uint)file.Attributes, file.LastWriteFileTime,
            nativeIdentity ? file.Identity.Low : null, nativeIdentity ? file.Identity.High : null, file.CreationFileTime);
    }

    // Streaming traversal avoids sorting or copying a large directory for extensions.
    public IEnumerable<int> EnumerateChildren(int directoryIndex)
    {
        if ((uint)directoryIndex >= (uint)Records.Length || !Records[directoryIndex].IsDirectory)
            throw new ArgumentOutOfRangeException(nameof(directoryIndex));
        return Children(directoryIndex);
    }

    public int[] SortedChildren(int directoryIndex, CancellationToken token = default) =>
        SortedChildren(directoryIndex, ScanSortColumn.Share, descending: true, token);

    public int[] SortedChildren(int directoryIndex, ScanSortColumn column, bool descending, CancellationToken token = default)
    {
        if ((uint)directoryIndex >= (uint)Records.Length || !Records[directoryIndex].IsDirectory)
            throw new ArgumentOutOfRangeException(nameof(directoryIndex));
        var children = new List<int>();
        foreach (int link in Children(directoryIndex))
        {
            if ((children.Count & 4095) == 0) token.ThrowIfCancellationRequested();
            children.Add(link);
        }
        int[] sorted = children.ToArray();
        // Compare pooled names directly; sorting a large folder creates no path/name strings.
        Array.Sort(sorted, (a, b) =>
        {
            var fileA = Records[Links[a].FileIndex];
            var fileB = Records[Links[b].FileIndex];
            if (column is ScanSortColumn.Modified or ScanSortColumn.Created)
            {
                long timeA = column == ScanSortColumn.Modified ? fileA.LastWriteFileTime : fileA.CreationFileTime;
                long timeB = column == ScanSortColumn.Modified ? fileB.LastWriteFileTime : fileB.CreationFileTime;
                bool knownA = IsValidFileTime(timeA), knownB = IsValidFileTime(timeB);
                // Unknown dates stay at the bottom in either direction.
                if (knownA != knownB) return knownA ? -1 : 1;
            }
            int comparison = column switch
            {
                ScanSortColumn.Name => Name(a).CompareTo(Name(b), StringComparison.OrdinalIgnoreCase),
                ScanSortColumn.Allocated => HasAllocationData ? AttributedBytes(a).CompareTo(AttributedBytes(b)) : 0,
                ScanSortColumn.Logical => DirectoryLogical[Links[a].FileIndex].CompareTo(DirectoryLogical[Links[b].FileIndex]),
                ScanSortColumn.Modified => ValidTime(fileA.LastWriteFileTime).CompareTo(ValidTime(fileB.LastWriteFileTime)),
                ScanSortColumn.Created => ValidTime(fileA.CreationFileTime).CompareTo(ValidTime(fileB.CreationFileTime)),
                ScanSortColumn.Type => TypeLabel(a, fileA).CompareTo(TypeLabel(b, fileB), StringComparison.OrdinalIgnoreCase),
                ScanSortColumn.Extension => Extension(a, fileA).CompareTo(Extension(b, fileB), StringComparison.OrdinalIgnoreCase),
                ScanSortColumn.Attributes => ((uint)fileA.Attributes).CompareTo((uint)fileB.Attributes),
                _ => AttributedBytes(a).CompareTo(AttributedBytes(b))
            };
            if (comparison != 0) return descending ? -comparison : comparison;
            int name = Name(a).CompareTo(Name(b), StringComparison.OrdinalIgnoreCase);
            return name != 0 ? name : a.CompareTo(b);
        });
        token.ThrowIfCancellationRequested();
        return sorted;
    }

    public static bool IsValidFileTime(long value) => value > 0 && value <= 2650467743999999999L;
    private static long ValidTime(long value) => IsValidFileTime(value) ? value : 0;
    private ReadOnlySpan<char> TypeLabel(int link, FileRecord file) => file.IsDirectory ? "Folder"
        : FileTypeClassifier.Label(FileTypeClassifier.Classify(Name(link)));
    private ReadOnlySpan<char> Extension(int link, FileRecord file) => file.IsDirectory ? [] : Path.GetExtension(Name(link));

    public long AttributedBytes(int linkIndex)
    {
        int file = linkIndex == -1 ? RootIndex : Links[linkIndex].FileIndex;
        return linkIndex != -1 && FirstLinks[file] != linkIndex ? 0
            : HasAllocationData ? DirectoryAllocated[file] : DirectoryLogical[file];
    }

    internal ReadOnlySpan<char> Name(int linkIndex) =>
        _names.AsSpan(Links[linkIndex].NameOffset, Links[linkIndex].NameLength);

    internal IEnumerable<int> Children(int directoryIndex)
    {
        for (int link = ChildHeads[directoryIndex]; link >= 0; link = NextLinks[link])
            yield return link;
    }

    bool IOpportunityInventory.IsDirectory(int index) => Records[index].IsDirectory;
    int IOpportunityInventory.LinkObject(int index) => Links[index].FileIndex;
    int IOpportunityInventory.LinkParent(int index) => Links[index].ParentIndex;
    ReadOnlySpan<char> IOpportunityInventory.Name(int index) => Name(index);
    IEnumerable<int> IOpportunityInventory.Children(int index) => EnumerateChildren(index);
    string? IOpportunityInventory.PathForFile(int index) => PathForFile(index);
    long IOpportunityInventory.LogicalSize(int index) => DirectoryLogical[index];
    long IOpportunityInventory.AllocatedSize(int index) => DirectoryAllocated[index];

    internal string? PathForFile(int fileIndex) => fileIndex == RootIndex
        ? RootPath : FirstLinks[fileIndex] >= 0 ? PathForLink(FirstLinks[fileIndex]) : null;

    internal string? PathForLink(int linkIndex)
    {
        var parts = new List<string>();
        int remaining = Records.Length;
        while (remaining-- > 0)
        {
            var link = Links[linkIndex];
            parts.Add(new string(Name(linkIndex)));
            if (link.ParentIndex == RootIndex)
            {
                parts.Reverse();
                return Path.Join(RootPath, Path.Combine(parts.ToArray()));
            }
            if (link.ParentIndex < 0 || FirstLinks[link.ParentIndex] < 0)
                return null;
            linkIndex = FirstLinks[link.ParentIndex];
        }
        return null; // A live scan can contain an unresolved or cyclic namespace.
    }
}
