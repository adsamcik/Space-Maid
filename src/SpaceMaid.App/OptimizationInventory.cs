using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

// Display state only. A usable inventory permits a new preview; execution still
// requires that preview's exact approval and the host's existing live checks.
internal sealed record OptimizationInventory(ScanSnapshot Scan, IReadOnlyList<Opportunity> Findings,
    bool IsCurrent, bool RequiresFullRescan);
