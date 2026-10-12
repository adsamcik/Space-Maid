global using OpportunityDetector = SpaceMaid.Tests.DeveloperOpportunityFixture;
global using OpportunityGrouping = SpaceMaid.Extensions.Developer.Analysis.OpportunityGrouping;
global using CleanupGrouping = SpaceMaid.Extensions.Developer.Analysis.CleanupGrouping;

using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Tests;

// Matches the host's provenance stamping while retaining the detector's
// metadata-only behavior for cleanup and grouping fixtures.
internal static class DeveloperOpportunityFixture
{
    internal const string ProviderId = "space-maid.developer";

    internal static IReadOnlyList<Opportunity> Find(ScanSnapshot scan, CancellationToken token = default)
        => SpaceMaid.Extensions.Developer.Analysis.OpportunityDetector.Find(scan, token)
            .Select(finding => finding with { ProviderId = ProviderId }).ToArray();
}
