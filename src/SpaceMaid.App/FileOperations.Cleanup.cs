using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal sealed partial class FileOperations
{
    public Task<GitWorktreeAssessment> AssessWorktreeAsync(ScanSnapshot scan, Opportunity location,
        CancellationToken token = default) => GitWorktreeAnalyzer.AssessAsync(scan, location, token);

    public Task<IReadOnlyDictionary<string, string>> CheckWorktreesAsync(ScanSnapshot scan,
        IReadOnlyList<Opportunity> selected, IReadOnlyDictionary<string, WorktreeCleanupPolicy> policies,
        CancellationToken token = default, Action<string, object>? diagnostic = null, IProgress<string>? progress = null)
        => GitWorktreeGuard.CheckAsync(scan, selected, policies, token, diagnostic, progress);

    public Task<CleanupPlan> PrepareCleanupAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> selected,
        CancellationToken token = default, IReadOnlyDictionary<string, string>? blockedPaths = null,
        OpportunityDiscovery? discover = null, BuildCleanupPolicy? buildPolicy = null)
        => Task.Run(() => CleanupPlanner.Prepare(scan, selected, token, blockedPaths, discover, buildPolicy), token);

    public Task<CleanupPlan> RestrictCleanupAsync(CleanupPlan plan, IReadOnlyList<Opportunity> selected,
        CancellationToken token = default) => Task.Run(() => CleanupPlanner.Restrict(plan, selected, token), token);

    public CleanupPreview EstimateCleanup(ScanSnapshot scan, IReadOnlyList<Opportunity> locations,
        CancellationToken token = default) => CleanupPlanner.Create(scan, locations, token);

    public Task<IReadOnlyDictionary<int, BuildCleanupAssessment>> AssessBuildCleanupAsync(ScanSnapshot scan,
        IReadOnlyList<Opportunity> locations, BuildCleanupPolicy policy, CancellationToken token = default)
        => Task.Run(() => BuildCleanupProtection.Assess(scan, locations, policy, token), token);

    // Activity needs current directory timestamps, rather than data-stream
    // allocation. The existing directory-only reader avoids extra NTFS queries.
    public Task<BuildCleanupAssessment> CheckBuildActivityAsync(Opportunity location, BuildCleanupPolicy policy,
        CancellationToken token = default) => BuildCleanupProtection.CheckCurrentAsync(location, policy, token);
}
