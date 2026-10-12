using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

public sealed record CleanupBlockedLocation(Opportunity Opportunity, string Reason);

public sealed record CleanupPlan
{
    public ScanSnapshot Snapshot { get; }
    public CleanupPreview Preview { get; }
    public IReadOnlyList<FolderDeletionTarget> Targets { get; }
    public IReadOnlyList<CleanupBlockedLocation> BlockedLocations { get; }
    public BuildCleanupPolicy BuildPolicy { get; }
    public IReadOnlyDictionary<int, BuildCleanupAssessment> BuildAssessments { get; }
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
}
