using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

public sealed record CleanupBlockedLocation(Opportunity Opportunity, string Reason);
public enum BuildCleanupAction { Recycle, PermanentlyDelete }

public sealed record CleanupPlan
{
    public ScanSnapshot Snapshot { get; }
    public CleanupPreview Preview { get; }
    public IReadOnlyList<FolderDeletionTarget> Targets { get; }
    public IReadOnlyList<CleanupBlockedLocation> BlockedLocations { get; }
    public BuildCleanupPolicy BuildPolicy { get; }
    public IReadOnlyDictionary<int, BuildCleanupAssessment> BuildAssessments { get; }
    public BuildCleanupAction BuildAction { get; init; } = BuildCleanupAction.Recycle;
    public bool RequiresVerifiedBuilds { get; init; }
    public IReadOnlyList<Opportunity> CapturedBuilds { get; init; } = [];
    public bool CanExecute => Targets.Count > 0;

    internal CleanupPlan(ScanSnapshot snapshot, CleanupPreview preview,
        IEnumerable<FolderDeletionTarget> targets, IEnumerable<CleanupBlockedLocation> blockedLocations,
        BuildCleanupPolicy policy, IReadOnlyDictionary<int, BuildCleanupAssessment> assessments)
    {
        Snapshot = snapshot; Preview = preview;
        Targets = Array.AsReadOnly(targets.ToArray());
        BlockedLocations = Array.AsReadOnly(blockedLocations.ToArray());
        BuildPolicy = policy; BuildAssessments = assessments;
    }

    // Runtime callers may retain captured targets across persistence or add
    // visible keep decisions, but may not enlarge the selected preview.
    public CleanupPlan WithCapturedTargets(IReadOnlyList<FolderDeletionTarget> targets)
    {
        if (targets.Count != Preview.Locations.Count || targets.Where((target, index) =>
            !string.Equals(target.Path, Preview.Locations[index].Path, StringComparison.OrdinalIgnoreCase)).Any())
            throw new ArgumentException("Captured targets must match the selected cleanup preview.", nameof(targets));
        return new(Snapshot, Preview, targets, BlockedLocations, BuildPolicy, BuildAssessments)
        { BuildAction = BuildAction, RequiresVerifiedBuilds = RequiresVerifiedBuilds, CapturedBuilds = CapturedBuilds };
    }

    public CleanupPlan WithAdditionalBlockedLocations(IEnumerable<CleanupBlockedLocation> blocked)
        => new(Snapshot, Preview, Targets, BlockedLocations.Concat(blocked)
            .DistinctBy(item => (item.Opportunity.ProviderId, item.Opportunity.RuleId, item.Opportunity.Path)), BuildPolicy, BuildAssessments)
        { BuildAction = BuildAction, RequiresVerifiedBuilds = RequiresVerifiedBuilds, CapturedBuilds = CapturedBuilds };
}
