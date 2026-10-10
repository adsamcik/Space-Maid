using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

internal enum CleanupOutcome { Recycled, Skipped, Failed, Cancelled, NotProcessed }
internal sealed record CleanupLocationResult(Opportunity Location, CleanupOutcome Outcome,
    string Message, bool Attempted = false, RecycleResult? Recycle = null);
internal sealed record CleanupProgress(int Completed, int Total, string Path, string Message);
internal sealed record CleanupBatchResult(IReadOnlyList<CleanupLocationResult> Locations,
    OpportunityReviewValidation? Validation, string? ValidationError = null, string? RefreshNote = null)
{
    public int RecycledCount => Locations.Count(item => item.Outcome == CleanupOutcome.Recycled);
    public bool Cancelled => Locations.Any(item => item.Outcome is CleanupOutcome.Cancelled or CleanupOutcome.NotProcessed);
    public string Summary => $"{RecycledCount:N0} moved · {Locations.Count(item => item.Outcome == CleanupOutcome.Skipped):N0} skipped"
        + $" · {Locations.Count(item => item.Outcome == CleanupOutcome.Failed):N0} failed"
        + (Cancelled ? " · Cancelled; some contents may already have moved." : ".")
        + " Space stays occupied until the Recycle Bin is emptied.";
}

// Confirmation authorizes only these captured directory identities. Fresh
// classification and the recycler's live identity checks do not widen consent.
internal static class CleanupBatch
{
    internal static async Task<CleanupBatchResult> ExecuteAsync(CleanupPlan plan, nint owner,
        IProgress<CleanupProgress> progress, CancellationToken token, Action<string, object> diagnostic,
        IReadOnlyDictionary<string, WorktreeCleanupPolicy>? checkedWorktrees = null)
    {
        var results = plan.BlockedLocations.Select(item => new CleanupLocationResult(item.Opportunity,
            CleanupOutcome.Skipped, item.Reason)).ToList();
        OpportunityReviewValidation? validation;
        try
        {
            progress.Report(new(0, plan.Targets.Count, "", "Checking confirmed folders again…"));
            validation = await OpportunityReviewValidator.ValidateAsync(plan.Snapshot, plan.Preview.Locations, token, diagnostic);
        }
        catch (Exception error) when (IsExpected(error))
        {
            var outcome = error is OperationCanceledException ? CleanupOutcome.NotProcessed : CleanupOutcome.Skipped;
            results.AddRange(plan.Preview.Locations.Select(location => new CleanupLocationResult(location, outcome,
                "No cleanup started: " + error.Message)));
            diagnostic("cleanup-validation-failed", new { Error = error.ToString() });
            return new(results.AsReadOnly(), null, error.Message);
        }
        var current = validation.ValidatedLocations.Select(location => location.Path).ToHashSet(StringComparer.Ordinal);
        int completed = 0;
        foreach (var target in plan.Targets)
        {
            var location = plan.Preview.Locations[completed];
            CleanupLocationResult result;
            if (token.IsCancellationRequested)
                result = new(location, CleanupOutcome.NotProcessed, "Not started because cleanup was cancelled.");
            else if (!current.Contains(target.Path))
                result = new(location, CleanupOutcome.Skipped, "Folder is missing, replaced or no longer matches its original detection rule.");
            else
            {
                bool attempted = false;
                try
                {
                    // Retain the pre-confirmation native identity even for a
                    // logical-only scan; never capture a replacement's identity.
                    await Task.Run(target.ValidateCurrent, token);
                    if (checkedWorktrees?.Keys.Any(path => GitWorktreeGuard.Covers(target.Path, path)) == true)
                        progress.Report(new(completed, plan.Targets.Count, target.Path, "Checking worktree Git state again…"));
                    var gitBlocks = await GitWorktreeGuard.CheckAsync(validation.Snapshot, [location],
                        checkedWorktrees ?? new Dictionary<string, WorktreeCleanupPolicy>(), token, diagnostic);
                    if (gitBlocks.TryGetValue(location.Path, out string? reason)) throw new InvalidOperationException(reason);
                    token.ThrowIfCancellationRequested();
                    progress.Report(new(completed, plan.Targets.Count, target.Path, "Moving to Recycle Bin…"));
                    attempted = true;
                    var recycled = await FolderRecycler.RecycleAsync(target, owner, token);
                    result = new(location, recycled.Cancelled ? CleanupOutcome.Cancelled : CleanupOutcome.Recycled,
                        recycled.Cancelled ? "Cancelled; some contents may already have moved." : "Moved to Recycle Bin.", true, recycled);
                    if (recycled.Cancelled)
                    {
                        results.Add(result); completed++;
                        diagnostic("cleanup-location-result", result);
                        progress.Report(new(completed, plan.Targets.Count, target.Path, result.Message));
                        results.AddRange(plan.Preview.Locations.Skip(completed).Select(remaining => new CleanupLocationResult(remaining,
                            CleanupOutcome.NotProcessed, "Not started because cleanup was cancelled.")));
                        break;
                    }
                }
                catch (Exception error) when (IsExpected(error))
                {
                    result = new(location, error is OperationCanceledException ? CleanupOutcome.Cancelled
                        : attempted ? CleanupOutcome.Failed : CleanupOutcome.Skipped,
                        error.Message + (attempted ? " Some contents may already have moved." : ""), attempted);
                }
            }
            results.Add(result); completed++;
            diagnostic("cleanup-location-result", result);
            progress.Report(new(completed, plan.Targets.Count, target.Path, result.Message));
        }
        return new(results.AsReadOnly(), validation);
    }

    internal static bool IsExpected(Exception error) => error is IOException or InvalidDataException or OverflowException or Win32Exception
        or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException
        or COMException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException;
}
