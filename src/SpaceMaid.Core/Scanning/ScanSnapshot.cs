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

public sealed record ScanProgress(string Phase, int Files, TimeSpan Elapsed);
public sealed record ScanMetrics(TimeSpan Acquisition, TimeSpan Aggregation, long ManagedAllocatedBytes);

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

    public string RootPath { get; }
    public string Backend { get; }
    public bool IncludesAllDataStreams { get; }
    public bool HasAllocationData { get; }
    public bool HasCompleteVolumeLinks { get; }
    public int FileCount => Records.Length;
    public int NameCount => Links.Length;
    public int RootIndex { get; }
    public IReadOnlyList<string> Warnings { get; }
    public ScanMetrics Metrics { get; }
    public long LogicalBytes => DirectoryLogical[RootIndex];
    public long AllocatedBytes => DirectoryAllocated[RootIndex];

    internal ScanSnapshot(string rootPath, string backend, FileRecord[] records,
        NameLink[] links, char[] names, int rootIndex, int[] firstLinks,
        int[] childHeads, int[] nextLinks, long[] logical, long[] allocated,
        bool allStreams, bool allocationData, bool completeLinks,
        IReadOnlyList<string> warnings, ScanMetrics metrics)
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
