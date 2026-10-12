namespace SpaceMaid.Core.Analysis;

public delegate IReadOnlyList<Opportunity> OpportunityDiscovery(SpaceMaid.Core.Scanning.ScanSnapshot scan, CancellationToken token);

public sealed record Opportunity(
    int DirectoryIndex, string Title, string Category, string Path,
    long LogicalBytes, long AllocatedBytes, string Reason, string Caution,
    string SuggestedAction, bool RequiresManualReview)
{
    public string ProviderId { get; init; } = "";
    public string RuleId { get; init; } = "";
    public OpportunityAppearance Appearance { get; init; } = OpportunityAppearance.Other;
    public bool IsWorktree { get; init; }
    public long DisplayBytes(bool hasAllocationData) => hasAllocationData ? AllocatedBytes : LogicalBytes;
}

public enum OpportunityAppearance { Other, Cache, Agent, Project }
