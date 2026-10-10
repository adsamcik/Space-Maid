namespace SpaceMaid.Core.Analysis;

public enum WorktreeCleanupMode { OldAndIntegrated, Old, Integrated }

// A captured cleanup condition. Changing the UI filter must not change a
// condition already accepted for review or confirmation.
public sealed record WorktreeCleanupPolicy
{
    public static WorktreeCleanupPolicy Default { get; } = new();
    public WorktreeCleanupMode Mode { get; }
    public int InactiveDays { get; }

    public WorktreeCleanupPolicy(WorktreeCleanupMode Mode = WorktreeCleanupMode.OldAndIntegrated, int InactiveDays = 30)
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (InactiveDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(InactiveDays), "Choose 1 through 365 days.");
        this.Mode = Mode; this.InactiveDays = InactiveDays;
    }

    public bool Matches(GitWorktreeAssessment assessment, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC comparison time is required.", nameof(utcNow));
        if (!assessment.VerifiedClean) return false;
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
