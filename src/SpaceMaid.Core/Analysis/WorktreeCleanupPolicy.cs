namespace SpaceMaid.Core.Analysis;

public enum WorktreeCleanupMode { OldAndIntegrated, Old, Integrated }

// A captured cleanup condition. Changing the UI filter must not change a
// condition already accepted for review or confirmation.
public sealed record WorktreeCleanupPolicy
{
    public static WorktreeCleanupPolicy Default { get; } = new();
    public WorktreeCleanupMode Mode { get; }
    public int InactiveDays { get; }
    public string? IntegrationReference { get; }

    public WorktreeCleanupPolicy(WorktreeCleanupMode Mode = WorktreeCleanupMode.OldAndIntegrated, int InactiveDays = 30, string? IntegrationReference = null)
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (InactiveDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(InactiveDays), "Choose 1 through 365 days.");
        this.Mode = Mode; this.InactiveDays = InactiveDays;
        if (IntegrationReference is not null && !GitWorktreeAnalyzer.ValidIntegrationReference(IntegrationReference))
            throw new ArgumentException("Choose a full local branch or remote-tracking reference.", nameof(IntegrationReference));
        this.IntegrationReference = IntegrationReference;
    }

    public bool Matches(GitWorktreeAssessment assessment, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC comparison time is required.", nameof(utcNow));
        if (!assessment.VerifiedClean || assessment.HasIgnoredFiles || assessment.OperationInProgress) return false;
        if (IntegrationReference is not null && assessment.IntegrationTarget != IntegrationReference) return false;
        // A detached checkout has no branch retaining its unique commits.
        // Even the age-only rule must not admit unintegrated detached work.
        if (assessment.IsDetached && !assessment.Integrated) return false;
        bool old = assessment.LastActivityUtc is { } activity && activity <= utcNow.AddDays(-InactiveDays);
        return Mode switch
        {
            WorktreeCleanupMode.OldAndIntegrated => old && assessment.Integrated,
            WorktreeCleanupMode.Old => old,
            WorktreeCleanupMode.Integrated => assessment.Integrated,
            _ => false
        };
    }
}
