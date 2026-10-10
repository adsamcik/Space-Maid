namespace SpaceMaid.Core.Scanning;

internal readonly record struct FileIdentity(ulong Low, ulong High = 0);

internal readonly record struct FileRecord(
    FileIdentity Identity, FileAttributes Attributes, long LogicalBytes,
    long AllocatedBytes, long LastWriteFileTime)
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

public readonly record struct ScanEntry(int LinkIndex, int FileIndex, int ParentIndex,
    string Name, bool IsDirectory, bool IsPrimaryName, bool IsShared, bool IsIncomplete,
    long LogicalBytes, long AllocatedBytes)
{
    // A second hard-link name is visible, but contributes no additional bytes.
    public long AttributedBytes(bool hasAllocation) => IsPrimaryName
        ? hasAllocation ? AllocatedBytes : LogicalBytes : 0;
}

public sealed class ScanSnapshot
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
            (_entryFlags[file] & 2) != 0, DirectoryLogical[file], DirectoryAllocated[file]);
    }

    public string? EntryPath(int linkIndex) => linkIndex == -1 ? RootPath : PathForLink(linkIndex);
    public int PrimaryLink(int fileIndex) => fileIndex == RootIndex ? -1 : FirstLinks[fileIndex];

    public int[] SortedChildren(int directoryIndex, CancellationToken token = default)
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
            int size = AttributedBytes(b).CompareTo(AttributedBytes(a));
            return size != 0 ? size : Name(a).CompareTo(Name(b), StringComparison.OrdinalIgnoreCase);
        });
        token.ThrowIfCancellationRequested();
        return sorted;
    }

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
