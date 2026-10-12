namespace SpaceMaid.Core.Analysis;

// A borrowed, immutable view of one scan. Indexed metadata and pooled names
// avoid copying a drive-sized inventory for each cleanup extension.
public interface IOpportunityInventory
{
    string RootPath { get; }
    int RootIndex { get; }
    int FileCount { get; }
    bool HasAllocationData { get; }
    bool IsDirectory(int objectIndex);
    int PrimaryLink(int objectIndex);
    int LinkObject(int linkIndex);
    int LinkParent(int linkIndex);
    ReadOnlySpan<char> Name(int linkIndex);
    IEnumerable<int> Children(int directoryIndex);
    string? PathForFile(int objectIndex);
    long LogicalSize(int objectIndex);
    long AllocatedSize(int objectIndex);
}
