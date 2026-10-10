namespace SpaceMaid.Core.Analysis;

public sealed record Opportunity(
    int DirectoryIndex, string Title, string Category, string Path,
    long LogicalBytes, long AllocatedBytes, string Reason, string Caution,
    string SuggestedAction, bool RequiresManualReview)
{
    public long DisplayBytes(bool hasAllocationData) => hasAllocationData ? AllocatedBytes : LogicalBytes;
}
