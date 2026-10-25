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
    public bool IsBuildOutput { get; init; }
    public bool IsRegeneratable { get; init; }
    // Visible storage whose contents must be managed through its owning tool.
    // Fresh discovery enforces this at preparation and execution, including ancestors.
    public string CleanupBlockReason { get; init; } = "";
    public long DisplayBytes(bool hasAllocationData) => hasAllocationData ? AllocatedBytes : LogicalBytes;
}

public enum OpportunityAppearance { Other, Cache, Agent, Project }
