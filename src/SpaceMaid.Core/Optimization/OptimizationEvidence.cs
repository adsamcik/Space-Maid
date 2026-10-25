namespace SpaceMaid.Core.Optimization;

public enum OptimizationAction { EvictGeneratedFile, DiscoverDuplicates, CompressFile, ShareFileBlocks, MaintainToolCache, RetireWorktree }
public enum OptimizationDisposition { Eligible, Review, Protected, Unknown }
public enum RestorationRequirement { Recompute, Rebuild, Download, Unavailable, ContentsPreserved }

// Evidence describes one action on one resource. Missing preservation facts
// cannot be averaged away by a confidence/savings score.
public sealed record OptimizationEvidence(OptimizationAction Action, OptimizationDisposition Disposition,
    string Owner, string Resource, string Reason, RestorationRequirement Restoration, DateTime ObservedAtUtc)
{
    public string Producer { get; init; } = "";
    public string Variant { get; init; } = "";
    public int ContractVersion { get; init; } = 1;
    public bool CoverageComplete { get; init; }
    public IReadOnlyList<string> RequiredInputs { get; init; } = [];
}

public enum GeneratedResourceKind { Unverified, PythonBytecode, DotNetCompilerCache, MypyIncremental, OwnedCache }
