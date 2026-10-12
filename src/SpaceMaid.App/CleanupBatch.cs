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
        IReadOnlyDictionary<string, WorktreeCleanupPolicy>? checkedWorktrees = null, OpportunityDiscovery? discover = null)
    {
        ArgumentNullException.ThrowIfNull(discover);
        var results = plan.BlockedLocations.Select(item => new CleanupLocationResult(item.Opportunity,
            CleanupOutcome.Skipped, item.Reason)).ToList();
        OpportunityReviewValidation? validation;
        Dictionary<(string ProviderId, string Path, string RuleId), Opportunity> current;
        IReadOnlyDictionary<int, BuildCleanupAssessment> currentAssessments;
        var buildsByTarget = new Dictionary<string, List<Opportunity>>(StringComparer.Ordinal);
        try
        {
            progress.Report(new(0, plan.Targets.Count, "", "Checking confirmed folders again…"));
            validation = await OpportunityReviewValidator.ValidateAsync(plan.Snapshot, plan.Preview.Locations, token, diagnostic, discover,
                new InlineValidationProgress(update => progress.Report(new(update.Completed, update.Total, "", "Checking confirmed folders…"))));
            current = validation.ValidatedLocations.ToDictionary(location => (location.ProviderId, location.Path, location.RuleId));
            currentAssessments = await Task.Run(() => BuildCleanupProtection.Assess(validation.Snapshot, validation.Findings, plan.BuildPolicy, token), token);
            var selectedDirectories = validation.ValidatedLocations.DistinctBy(location => location.DirectoryIndex)
                .ToDictionary(location => location.DirectoryIndex, location => location.Path);
            IOpportunityInventory inventory = validation.Snapshot;
            foreach (var build in validation.Findings.Where(location => location.IsBuildOutput))
            {
                int directory = build.DirectoryIndex, remaining = inventory.FileCount;
                while (directory >= 0 && remaining-- > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (selectedDirectories.TryGetValue(directory, out string? path))
                    {
                        if (!buildsByTarget.TryGetValue(path, out var builds)) buildsByTarget.Add(path, builds = []);
                        if (!builds.Any(item => item.DirectoryIndex == build.DirectoryIndex)) builds.Add(build);
                    }
                    int link = inventory.PrimaryLink(directory);
                    if (link < 0) break;
                    directory = inventory.LinkParent(link);
                }
            }
        }
        catch (Exception error) when (IsExpected(error))
        {
            var outcome = error is OperationCanceledException ? CleanupOutcome.NotProcessed : CleanupOutcome.Skipped;
            results.AddRange(plan.Preview.Locations.Select(location => new CleanupLocationResult(location, outcome,
                "No cleanup started: " + error.Message)));
            diagnostic("cleanup-validation-failed", new { Error = error.ToString() });
            return new(results.AsReadOnly(), null, error.Message);
        }
        int completed = 0;
        foreach (var target in plan.Targets)
        {
            var location = plan.Preview.Locations[completed];
            CleanupLocationResult result;
            if (token.IsCancellationRequested)
                result = new(location, CleanupOutcome.NotProcessed, "Not started because cleanup was cancelled.");
            else if (!current.ContainsKey((location.ProviderId, location.Path, location.RuleId)))
                result = new(location, CleanupOutcome.Skipped, "Folder is missing, replaced or no longer matches its original detection rule.");
            else
            {
                bool attempted = false;
                try
                {
                    // Retain the pre-confirmation native identity even for a
                    // logical-only scan; never capture a replacement's identity.
                    await Task.Run(target.ValidateCurrent, token);
                    var freshLocation = current[(location.ProviderId, location.Path, location.RuleId)];
                    var freshAssessment = currentAssessments[freshLocation.DirectoryIndex];
                    if (freshAssessment.State == CleanupReadiness.Protected
                        || freshAssessment.State == CleanupReadiness.NeedsReview
                        && plan.BuildAssessments[location.DirectoryIndex].State == CleanupReadiness.Ready)
                        throw new InvalidOperationException(freshAssessment.Reason);
                    if (checkedWorktrees?.Keys.Any(path => GitWorktreeGuard.Covers(target.Path, path)) == true)
                        progress.Report(new(completed, plan.Targets.Count, target.Path, "Checking worktree Git state again…"));
                    var gitBlocks = await GitWorktreeGuard.CheckAsync(validation.Snapshot, [location],
                        checkedWorktrees ?? new Dictionary<string, WorktreeCleanupPolicy>(), token, diagnostic);
                    if (gitBlocks.TryGetValue(location.Path, out string? reason)) throw new InvalidOperationException(reason);
                    if (buildsByTarget.TryGetValue(location.Path, out var builds))
                    {
                        progress.Report(new(completed, plan.Targets.Count, target.Path, "Checking build activity…"));
                        foreach (var build in builds)
                        {
                            var assessment = currentAssessments[build.DirectoryIndex];
                            if (assessment.State == CleanupReadiness.Protected) throw new InvalidOperationException(assessment.Reason);
                            var live = await BuildCleanupProtection.CheckCurrentAsync(build, plan.BuildPolicy, token);
                            if (live.State == CleanupReadiness.Protected
                                || live.State == CleanupReadiness.NeedsReview && assessment.State == CleanupReadiness.Ready)
                                throw new InvalidOperationException(live.Reason);
                        }
                    }
                    await Task.Run(target.ValidateCurrent, token);
                    token.ThrowIfCancellationRequested();
                    progress.Report(new(completed, plan.Targets.Count, target.Path, "Moving to Recycle Bin…"));
                    attempted = true;
                    var recycled = await FolderRecycler.RecycleAsync(target, owner, token, diagnostic);
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
    private sealed class InlineValidationProgress(Action<OpportunityValidationProgress> report) : IProgress<OpportunityValidationProgress>
    { public void Report(OpportunityValidationProgress value) => report(value); }
}
