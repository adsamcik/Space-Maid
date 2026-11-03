using System.IO;
using System.Diagnostics;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Optimization;

namespace SpaceMaid.App;

internal sealed record CleanupSchedule(DayOfWeek Day, TimeOnly Time, string TimeZoneId, bool Automatic)
{
    internal CleanupSchedule Validate()
    {
        if (!Enum.IsDefined(Day) || string.IsNullOrWhiteSpace(TimeZoneId) || TimeZoneId.Length > 128)
            throw new ArgumentException("Choose a valid weekly day, time and time zone.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ArgumentException("The saved cleanup time zone is unavailable. Choose a current time zone.", error); }
        return this;
    }
}

internal sealed record CleanupRunLimits
{
    public long MaxBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public int MaxTargets { get; init; } = 500;
    public int MaxMinutes { get; init; } = 30;
    internal void Validate()
    {
        if (MaxBytes is < 1 or > 1024L * 1024 * 1024 * 1024 || MaxTargets is < 1 or > 20000 || MaxMinutes is < 1 or > 240)
            throw new ArgumentException("Choose bounded automatic cleanup limits.");
    }
}

// A saved definition authorizes rules and scope. An individual approval always
// captures a separate manifest; changing this record cannot change that manifest.
internal sealed record CleanupDefinition
{
    internal const int MaximumScopes = 20000;
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Revision { get; init; } = 1;
    public string Name { get; init; } = "Build cleanup";
    public string DiscoveryRootPath { get; init; } = "";
    public string[] ScopePaths { get; init; } = [];
    public FolderDeletionIdentity? RootIdentity { get; init; }
    public FolderDeletionIdentity[] ScopeIdentities { get; init; } = [];
    public bool Build { get; init; } = true;
    public bool Cache { get; init; }
    public bool Worktrees { get; init; }
    public string[] ManualRuleKeys { get; init; } = [];
    public int InactiveDays { get; init; } = 30;
    public BuildCleanupAction BuildAction { get; init; }
    public WorktreeCleanupPolicy WorktreePolicy { get; init; } = new();
    public string[] ExcludedPaths { get; init; } = [];
    public string[] AutomaticRuleConsents { get; init; } = [];
    public CleanupSchedule? Schedule { get; init; }
    public bool Enabled { get; init; }
    public string? ScheduleError { get; init; }
    public DateTimeOffset? ScheduleActivatedUtc { get; init; }
    public int? RegisteredRevision { get; init; }
    public CleanupRunLimits Limits { get; init; } = new();
    public bool SupportsAutomatic => Build && !Cache && !Worktrees && ManualRuleKeys is { Length: 0 };

    internal CleanupDefinition Validate()
    {
        if (ManualRuleKeys is null || Id == Guid.Empty || Revision < 1 || string.IsNullOrWhiteSpace(Name) || Name.Length > 80
            || !Build && !Cache && !Worktrees && ManualRuleKeys.Length == 0 || InactiveDays is < 1 or > 365 || !Enum.IsDefined(BuildAction)
            || ScopePaths is null || ScopePaths.Length is < 1 or > MaximumScopes || ExcludedPaths is null || ExcludedPaths.Length > 1000
            || AutomaticRuleConsents is null || AutomaticRuleConsents.Length > 256 || ScopeIdentities is null || ScopeIdentities.Length > MaximumScopes
            || AutomaticRuleConsents.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 512)
            || ScopeIdentities.Any(identity => identity is null)
            || ManualRuleKeys.Length > 256
            || ManualRuleKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 512 || key.IndexOf('/') <= 0 || key.EndsWith('/'))
            || WorktreePolicy is null || Limits is null)
            throw new ArgumentException("Choose a name, scope and valid cleanup rules.");
        string root = OptimizationPolicy.NormalizePath(DiscoveryRootPath);
        string[] paths = ScopePaths.Select(OptimizationPolicy.NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var scopeSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ScopeIdentities.Any(identity => !scopeSet.Contains(identity.Path))
            || ScopeIdentities.Select(identity => identity.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ScopeIdentities.Length)
            throw new ArgumentException("The captured cleanup locations do not match its scope.");
        if (paths.Any(path => !OptimizationPolicy.Covers(root, path))) throw new ArgumentException("Cleanup scope must stay inside its discovery location.");
        string[] exclusions = ExcludedPaths.Select(OptimizationPolicy.NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (exclusions.Any(path => !OptimizationPolicy.Covers(root, path))) throw new ArgumentException("Folder exclusions must stay inside the cleanup location.");
        if (RootIdentity is not null && !string.Equals(root, RootIdentity.Path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The captured cleanup location does not match its scope.");
        Schedule?.Validate(); Limits.Validate();
        if (Schedule?.Automatic == true && !SupportsAutomatic)
            throw new ArgumentException("Caches, worktrees and other manual rules require Ask me first. Automatic folder cleanup supports verified builds.");
        if (Enabled && Schedule is null) throw new ArgumentException("Choose a schedule before enabling it.");
        return this with { Name = Name.Trim(), DiscoveryRootPath = root, ScopePaths = paths, ExcludedPaths = exclusions,
            ManualRuleKeys = ManualRuleKeys.Distinct(StringComparer.Ordinal).ToArray() };
    }

    internal bool InScope(string path) => ScopePaths.Any(root => OptimizationPolicy.Covers(root, path));
    internal static bool InScopes(string path, IReadOnlySet<string> scopes)
    {
        for (string? ancestor = Path.TrimEndingDirectorySeparator(path); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if (scopes.Contains(ancestor)) return true;
        return false;
    }
    // A containing folder is kept too; otherwise recycling it could bypass a
    // nested exclusion. Exclusions never turn into partial recursive deletion.
    internal bool Excludes(string path) => ExcludedPaths.Any(root => OptimizationPolicy.Covers(root, path) || OptimizationPolicy.Covers(path, root));
    internal bool Includes(Opportunity item) => InScope(item.Path) && !Excludes(item.Path)
        && MatchesRule(item);
    internal bool MatchesRule(Opportunity item) => item.IsBuildOutput ? Build : item.IsWorktree ? Worktrees
        : Cache && item.Appearance == OpportunityAppearance.Cache || ManualRuleKeys.Contains(item.ProviderId + "/" + item.RuleId, StringComparer.Ordinal);
}

internal enum CleanupRunState { Review, Queued, Running, Completed, Stopped, NeedsAttention, Missed, Dismissed, Superseded }
internal sealed record CleanupRunTarget(Opportunity Location, FolderDeletionIdentity? Identity, string Decision, string Reason)
{
    public BuildCleanupAssessment? Assessment { get; init; }
}
internal sealed record CleanupTargetReceipt(string Path, string ProviderId, string RuleId, CleanupOutcome Outcome,
    string Message, bool Attempted, DateTimeOffset RecordedUtc)
{
    public string? RecycledPath { get; init; }
}

internal sealed record CleanupRunRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int SchemaVersion { get; init; } = 1;
    public CleanupDefinition Definition { get; init; } = new();
    public string ScanRootPath { get; init; } = "";
    public FolderDeletionIdentity? RootIdentity { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ScannedAtUtc { get; init; }
    public bool? UsesLogicalSize { get; init; }
    public CleanupRunState State { get; init; } = CleanupRunState.Review;
    public string Source { get; init; } = "Manual";
    public string? OccurrenceId { get; init; }
    public DateTimeOffset? ScheduledForUtc { get; init; }
    public DateTimeOffset? ApprovedUtc { get; init; }
    public Guid? ApprovalClaimId { get; init; }
    public int OwnerProcessId { get; init; } = Environment.ProcessId;
    public DateTimeOffset? OwnerStartedUtc { get; init; } = CleanupRunOwnership.CurrentStartedUtc;
    public CleanupRunTarget[] Targets { get; init; } = [];
    public CleanupTargetReceipt[] Receipts { get; init; } = [];
    public string? Message { get; init; }
    public bool HasUncertainOutcome => Receipts.Any(item => item.Outcome is CleanupOutcome.Partial or CleanupOutcome.Uncertain
        || item.Attempted && item.Outcome is CleanupOutcome.Failed or CleanupOutcome.Cancelled);
}

internal sealed record CleanupPreparedRun(CleanupDefinition Definition, CleanupPlan Plan, CleanupRunRecord Record);

internal sealed record CleanupRunHeader(Guid Id, Guid DefinitionId, int DefinitionRevision, string Name,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, CleanupRunState State, string Source, string? Message,
    string? OccurrenceId, bool HasUncertainOutcome, int TargetCount, int ReadyCount, int KeptCount,
    int RecycledCount, int DeletedCount, int SkippedCount, int NotStartedCount, int NeedsCheckingCount);

internal static class CleanupRunOwnership
{
    internal static DateTimeOffset CurrentStartedUtc { get; } = StartTime(Environment.ProcessId)
        ?? throw new InvalidOperationException("The cleanup process identity could not be established.");
    internal static bool IsAlive(CleanupRunRecord record) => record.OwnerStartedUtc is { } expected
        && StartTime(record.OwnerProcessId) is { } current && current == expected;
    internal static bool IsDefinitelyGone(CleanupRunRecord record)
    {
        if (record.OwnerProcessId <= 0) return true;
        try
        {
            using var process = Process.GetProcessById(record.OwnerProcessId);
            if (process.HasExited) return true;
            return record.OwnerStartedUtc is { } expected && process.StartTime.ToUniversalTime() != expected;
        }
        catch (ArgumentException) { return true; } // No process with that PID.
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; } // Inaccessible is not proof that the owner exited.
    }
    private static DateTimeOffset? StartTime(int id)
    {
        if (id <= 0) return null;
        try { using var process = Process.GetProcessById(id); return process.HasExited ? null : process.StartTime.ToUniversalTime(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}
