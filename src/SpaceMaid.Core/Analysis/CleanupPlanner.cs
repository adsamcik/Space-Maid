using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

public sealed record CleanupPreview(
    IReadOnlyList<Opportunity> Locations, long EstimatedBytes, long ExcludedSharedBytes,
    int FileCount, bool UsesLogicalSize, IReadOnlyList<string> Cautions)
{
    public bool CanExecute => false;
}

public static class CleanupPlanner
{
    public const bool IsReadOnly = true;

    public static CleanupPreview Create(ScanSnapshot scan, IEnumerable<Opportunity> selected,
        CancellationToken token = default)
    {
        var roots = selected.DistinctBy(o => o.DirectoryIndex).ToArray();
        var rootSet = roots.Select(o => o.DirectoryIndex).ToHashSet();
        foreach (var root in roots)
            if (root.DirectoryIndex < 0 || root.DirectoryIndex >= scan.FileCount || !scan.Records[root.DirectoryIndex].IsDirectory
                || !string.Equals(scan.PathForFile(root.DirectoryIndex), root.Path, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A selected location does not belong to this scan.", nameof(selected));
        // Nested selections are already covered by their selected ancestor.
        roots = roots.Where(root => !HasSelectedAncestor(scan, root.DirectoryIndex, rootSet)).ToArray();
        bool[] includedDirectories = new bool[scan.FileCount];
        var pending = new Stack<int>(roots.Select(o => o.DirectoryIndex));
        while (pending.TryPop(out int directory))
        {
            token.ThrowIfCancellationRequested();
            if (includedDirectories[directory]) continue;
            includedDirectories[directory] = true;
            foreach (int link in scan.Children(directory))
            {
                int file = scan.Links[link].FileIndex;
                if (scan.Records[file].IsDirectory) pending.Push(file);
            }
        }
        int[] totalLinks = new int[scan.FileCount], selectedLinks = new int[scan.FileCount];
        for (int i = 0; i < scan.Links.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var link = scan.Links[i];
            totalLinks[link.FileIndex]++;
            if (link.ParentIndex >= 0 && includedDirectories[link.ParentIndex]) selectedLinks[link.FileIndex]++;
        }
        long estimate = 0, shared = 0;
        int count = 0;
        bool specialStorage = false;
        for (int i = 0; i < scan.FileCount; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var file = scan.Records[i];
            if (file.IsDirectory ? !includedDirectories[i] : selectedLinks[i] == 0) continue;
            long bytes = scan.HasAllocationData ? file.AllocatedBytes : file.LogicalBytes;
            if (!file.IsDirectory && selectedLinks[i] < totalLinks[i]) shared = checked(shared + bytes);
            else estimate = checked(estimate + bytes);
            if (!file.IsDirectory) count++;
            specialStorage |= (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0;
        }
        var cautions = new List<string> { "Read-only preview. No files will be deleted, moved, cleared, or modified." };
        if (!scan.HasAllocationData) cautions.Add("Logical size is shown; physical space that could be freed is unknown.");
        if (!scan.HasCompleteVolumeLinks) cautions.Add("Hard links outside the scanned scope may reduce the estimate. This is not a guaranteed saving.");
        if (!scan.IncludesAllDataStreams) cautions.Add("Standard scanning does not include all alternate data streams.");
        if (shared > 0) cautions.Add("Allocation shared with unselected names has been excluded from the estimate.");
        if (specialStorage) cautions.Add("Cloud/provider-backed files need separate review; their backing storage may be shared or managed elsewhere.");
        if (scan.Warnings.Count > 0) cautions.Add("The scan has omissions. Review its coverage warnings.");
        cautions.Add("This is a live scan, not a snapshot. Sizes and files can change after scanning.");
        return new(roots, estimate, shared, count, !scan.HasAllocationData, cautions);
    }

    public static void Execute(CleanupPreview preview) =>
        throw new NotSupportedException("Cleanup is disabled in this read-only release.");

    private static bool HasSelectedAncestor(ScanSnapshot scan, int directory, HashSet<int> selected)
    {
        int remaining = scan.FileCount;
        while (directory != scan.RootIndex && remaining-- > 0)
        {
            int link = scan.FirstLinks[directory];
            if (link < 0) return false;
            directory = scan.Links[link].ParentIndex;
            if (directory < 0) return false;
            if (selected.Contains(directory)) return true;
        }
        return false;
    }
}
