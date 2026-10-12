using SpaceMaid.Core.Analysis;

namespace SpaceMaid.Extensions.Contracts;

public sealed record OpportunityAnalysis(IReadOnlyList<Opportunity> Findings, OpportunityGroupSet Groups);
public sealed record OpportunityGroupContext(IReadOnlySet<string> MatchedWorktreePaths, WorktreeCleanupPolicy WorktreePolicy);

// Optional capability: metadata-only cleanup groups in the shared Opportunities
// view. The host owns all validation, confirmation and file operations.
public interface IOpportunityExtension
{
    OpportunityAnalysis Analyze(IOpportunityInventory inventory, CancellationToken token);
    IReadOnlyList<OpportunityGroup> BuildCleanupGroups(IOpportunityInventory inventory,
        OpportunityAnalysis analysis, OpportunityGroupContext context, CancellationToken token)
        => analysis.Groups.Cleanup ?? [];
}
