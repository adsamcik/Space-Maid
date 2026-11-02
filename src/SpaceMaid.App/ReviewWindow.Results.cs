using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class ReviewWindow
{
    private CleanupPlan? _executingPlan;
    private bool _remainingEstimateUncertain;
    private HashSet<(string Provider, string Rule, string Path)> _includedAtApproval = [];

    private void ShowLocationResult(CleanupLocationResult result)
    {
        var key = (result.Location.ProviderId, result.Location.RuleId, result.Location.Path);
        foreach (var row in _locations)
        {
            if (row.Identity == key) row.SetResult(result);
            else if (_includedAtApproval.Contains(row.Identity) && GitWorktreeGuard.Covers(result.Location.Path, row.Path))
                row.SetResult(CoveredResult(row, result));
        }
        UpdateRuleSummary();
        UpdateResultGroupSizes();
        ScheduleRemainingEstimate();
    }

    private void UpdateResultGroupSizes()
    {
        long largest = _groups.Skip(1).Select(group => group.Bytes).DefaultIfEmpty().Max();
        foreach (var group in _groups) group.SetMatches(group.Matches, largest);
    }

    private void ShowCompletedResults(CleanupBatchResult result)
    {
        var outcomes = result.Locations.GroupBy(item => (item.Location.ProviderId, item.Location.RuleId, item.Location.Path))
            .ToDictionary(group => group.Key, group => group.Last());
        var findings = (result.RefreshedFindings ?? result.Validation?.Findings ?? []).DistinctBy(item => (item.ProviderId, item.RuleId, item.Path))
            .ToDictionary(item => (item.ProviderId, item.RuleId, item.Path));
        foreach (var row in _locations)
        {
            var outcome = outcomes.GetValueOrDefault(row.Identity);
            if (outcome is null && _includedAtApproval.Contains(row.Identity))
            {
                var parent = result.Locations.FirstOrDefault(item => GitWorktreeGuard.Covers(item.Location.Path, row.Path));
                if (parent is not null) outcome = CoveredResult(row, parent);
            }
            row.SetResult(outcome);
            if (findings.TryGetValue(row.Identity, out var current)) row.UpdateEstimate(current);
        }
        _remainingEstimateUncertain = (result.RefreshedSnapshot is null || result.RefreshedSnapshot.IncompleteRecheckScopes.Count > 0)
            && result.Locations.Any(item => item.Attempted && item.Outcome is not (CleanupOutcome.Recycled or CleanupOutcome.PermanentlyDeleted));
        UpdateRuleSummary();
    }

    private static CleanupLocationResult CoveredResult(CleanupReviewRow row, CleanupLocationResult parent)
        => new(row.Location, parent.Outcome, $"{parent.Message} Included through {parent.Location.Path}", parent.Attempted);

    private void ScheduleRemainingEstimate()
    {
        _estimateCancellation?.Cancel(); _selectionVersion++;
        EstimateText.Text = "…";
        _estimateTimer.Stop(); _estimateTimer.Start();
    }

    private async Task UpdateRemainingEstimateAsync()
    {
        if (_executingPlan is not { } chosen) return;
        using var cancellation = new CancellationTokenSource();
        _estimateCancellation = cancellation;
        int version = _selectionVersion;
        var moved = _locations.Where(row => row.IsRemoved).Select(row => row.Identity).ToHashSet();
        var remaining = chosen.Preview.Locations.Where(item => !moved.Contains((item.ProviderId, item.RuleId, item.Path))).ToArray();
        var scan = chosen.Snapshot;
        var currentFindings = Result?.RefreshedFindings ?? Result?.Validation?.Findings;
        if (currentFindings is not null)
        {
            // Prefer the post-cleanup inventory, then the confirmation inventory.
            // Missing or changed findings cannot contribute an old estimate.
            scan = Result!.RefreshedSnapshot ?? Result.Validation!.Snapshot;
            var keys = remaining.Select(item => (item.ProviderId, item.RuleId, item.Path)).ToHashSet();
            remaining = currentFindings.Where(item => keys.Contains((item.ProviderId, item.RuleId, item.Path))).ToArray();
        }
        var calculation = Task.Run(() => _operations.EstimateCleanup(scan, remaining, cancellation.Token), cancellation.Token);
        SelectionCalculation = calculation;
        try
        {
            var preview = await calculation;
            if (_closed || cancellation.IsCancellationRequested || version != _selectionVersion) return;
            SetPreview(preview);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        {
            if (!_closed && version == _selectionVersion)
            { EstimateText.Text = "—"; ResultDetailText.Text = "Could not update the remaining estimate. " + error.Message; }
        }
        finally { if (ReferenceEquals(_estimateCancellation, cancellation)) _estimateCancellation = null; }
    }
}
