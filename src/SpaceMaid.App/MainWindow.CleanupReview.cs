using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private async void Review_Click(object sender, RoutedEventArgs e) => await ReviewSelectionAsync();

    internal async Task ReviewSelectionAsync()
    {
        if (_snapshot is not { } scan || _work is not null) return;
        bool selectedScope = _rows.Any(row => row.Selected);
        var selected = _rows.Where(r => !selectedScope || r.Selected).Select(r => r.Item).ToArray();
        var checkedWorktrees = SelectedCheckedWorktrees(selected);
        if (selected.Length == 0) return;
        var buildPolicy = CurrentBuildPolicy;
        using var work = new CancellationTokenSource();
        using var log = StartActionLog("validate-review", scan.RootPath);
        _work = work;
        SetBusy(true);
        StatusText.Text = $"Preparing cleanup rules · {selected.Length:N0} locations…";
        var timer = Stopwatch.StartNew();
        try
        {
            log.Record("review-validation-start", new { Selected = selected.Length, OutOfDate = _scanStale, WorktreePolicies = checkedWorktrees });
            // Only capture candidate root identities here. Recursive reads and
            // live tool checks belong to the approved subset at execution.
            var basis = await Operations.PrepareCleanupRuleCandidatesAsync(scan, selected, work.Token,
                new Dictionary<string, string>(), DiscoverOpportunities);
            var plan = await Operations.ReviewCleanupRulesAsync(basis, buildPolicy, BuildCleanupAction.Recycle, DiscoverOpportunities, work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (_closing || !ReferenceEquals(_snapshot, scan)) return;
            string note = $"Estimates from the scan at {scan.ScannedAtUtc.ToLocalTime():HH:mm:ss}. "
                + "Included folders are checked again before cleanup. Changed, unavailable or unverified folders are kept; other folders continue.";
            if (checkedWorktrees.Count > 0) note += " Worktree rules for this run: "
                + string.Join("; ", checkedWorktrees.Values.Distinct().Select(DescribeWorktreePolicy))
                + ". Ignored contents are included; recycling preserves branches and leaves Git registration in place.";
            StatusText.Text = note;
            log.Record("review-validation-complete", new { ElapsedMs = timer.Elapsed.TotalMilliseconds,
                Candidates = basis.Preview.Locations.Count, plan.Preview.EstimatedBytes, Blocked = plan.BlockedLocations.Count });
            if (work.IsCancellationRequested)
            {
                StatusText.Text = "Review cancelled. No preview was opened.";
                log.Record("review-cancelled", new { Phase = "before-preview" });
                return;
            }
            if (plan.Preview.Locations.Count + plan.BlockedLocations.Count > 0)
            {
                var displayed = _snapshot!;
                var review = new ReviewWindow(plan, note, async (chosenPlan, progress, token) =>
                {
                    bool saved = StoreBuildPolicy(chosenPlan.BuildPolicy);
                    var cleanup = ExecuteCleanupAsync(chosenPlan, displayed, progress, token, work, log, checkedWorktrees);
                    _cleanupTask = cleanup;
                    var result = await cleanup;
                    return saved ? result : result with { RefreshNote = "The rules were used for this run but could not be saved. " + result.RefreshNote };
                }, _cleanupGroups, Operations) { Owner = this };
                review.EnableRuleReview(basis, DiscoverOpportunities, selectedScope);
                review.ShowDialog();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or Win32Exception
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            log.Record("review-validation-failed", new { ElapsedMs = timer.Elapsed.TotalMilliseconds, Error = ex.ToString() });
            if (!_closing) MarkScanStale(ex is OperationCanceledException
                ? "Selection check cancelled. Review again to check the selected locations."
                : $"Selected locations could not be checked: {ex.Message}", selected.Select(location => new ScanRecheckRequest(location.Path)));
        }
        finally { _cleanupTask = null; _work = null; if (!_closing) { ShowActionLogError(log); SetBusy(false); } }
    }
}
