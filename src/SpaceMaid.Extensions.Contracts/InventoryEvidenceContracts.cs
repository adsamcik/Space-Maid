namespace SpaceMaid.Extensions.Contracts;

// Optional API v1 capability for trusted views. The view chooses the user's
// scope. Never expose this factory (or its paths) as a model tool. Sessions read
// the existing metadata snapshot only; they do not scan or open file contents.
public interface IInventoryEvidence
{
    Task<IInventoryEvidenceSession> OpenEvidenceSessionAsync(string root, IReadOnlyList<string> excludedPaths,
        CancellationToken token);
}

public readonly record struct EvidenceId(Guid Value);
public enum EvidenceReviewDisposition { ManualInvestigation, Blocked }
public sealed record EvidenceReviewPolicy(EvidenceReviewDisposition Disposition, string RuleId, int RuleVersion,
    IReadOnlyList<string> Reasons);
public sealed record EvidenceIdentity(ulong Low, ulong High);
public sealed record EvidenceEntry(EvidenceId Id, string Name, string Path, bool IsDirectory,
    long? LogicalBytes, long? AllocatedBytes, uint Attributes, long LastWriteFileTime,
    EvidenceIdentity? Identity, bool HasSharedNames, EvidenceReviewPolicy ReviewPolicy);
public sealed record EvidenceScope(Guid Revision, EvidenceEntry Root, IReadOnlyList<string> ExcludedPaths,
    EvidenceCoverage Coverage);
public sealed record EvidenceCoverage(string Backend, DateTime ScannedAtUtc, DateTime? LastRecheckUtc,
    bool RetainsEarlierMetadata, bool Complete, bool HasAllocationData, bool IncludesAllStreams,
    bool CompleteVolumeLinks, int WarningCount, IReadOnlyList<string> Warnings);
public sealed record EvidencePage(IReadOnlyList<EvidenceEntry> Items, Guid? Continuation, bool Complete);
public sealed record EvidenceFileTypeCount(string FilenameTypeHint, long FileNames);
public sealed record EvidenceTotals(long FileNames, long Directories, long? UniquePhysicalFiles,
    long LogicalBytes, long? AllocatedBytes, long? SharedObjectLogicalBytes, bool TraversalComplete,
    long UnknownWriteDates, long? OldestWriteFileTime, long? NewestWriteFileTime);
public sealed record EvidenceSummary(Guid Revision, IReadOnlyList<EvidenceId> Roots, string RuleId, int RuleVersion,
    EvidenceCoverage Coverage, EvidenceTotals Totals, IReadOnlyList<EvidenceFileTypeCount> FileTypes,
    IReadOnlyList<string> Cautions, string Text);

// These are investigation suggestions, never executable cleanup targets.
// Model text remains labelled and separate from host observations and policy.
public sealed record EvidenceReviewItem(EvidenceEntry Entry, string UntrustedExplanation);
public sealed record EvidenceProposalAdmission(bool Accepted, IReadOnlyList<EvidenceReviewItem> Items,
    EvidenceSummary? Summary, IReadOnlyList<string> Errors);

public interface IInventoryEvidenceSession : IDisposable
{
    EvidenceScope Scope { get; }
    Task<EvidenceEntry> DetailsAsync(EvidenceId id, CancellationToken token);
    Task<EvidencePage> ChildrenAsync(EvidenceId directory, int limit, Guid? continuation, CancellationToken token);
    Task<EvidencePage> SearchAsync(EvidenceId directory, string nameContains, int limit, Guid? continuation, CancellationToken token);
    Task<EvidenceSummary> SummarizeAsync(IReadOnlyList<EvidenceId> roots, CancellationToken token);
    // Strict schema: {"schemaVersion":1,"items":[{"id":"GUID","action":"investigate","explanation":"..."}]}.
    // Reject the whole proposal on unknown fields/IDs/actions, stale data or
    // blocked policy. Paths and byte claims cannot be supplied by the model.
    Task<EvidenceProposalAdmission> ValidateProposalAsync(string json, CancellationToken token);
}
