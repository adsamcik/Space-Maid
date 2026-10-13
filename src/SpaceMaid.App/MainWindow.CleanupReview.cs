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
        var selected = _rows.Where(r => r.Selected).Select(r => r.Item).ToArray();
        var checkedWorktrees = SelectedCheckedWorktrees(selected);
        if (selected.Length == 0) return;
        if (_cleanupSettingsUnavailable)
        {
            ViewOptionsToggle.IsChecked = true;
            StatusText.Text = "Set build retention in View options before reviewing.";
            return;
        }
        if (!SaveBuildRetention()) return;
        var buildPolicy = CurrentBuildPolicy;
        using var work = new CancellationTokenSource();
        using var log = StartActionLog("validate-review", scan.RootPath);
        _work = work;
        SetBusy(true);
        StatusText.Text = $"Checking {selected.Length:N0} selected locations…";
        var timer = Stopwatch.StartNew();
        try
        {
            log.Record("review-validation-start", new { Selected = selected.Length, OutOfDate = _scanStale, WorktreePolicies = checkedWorktrees });
            var validation = await OpportunityReviewValidator.ValidateAsync(scan, selected, work.Token, log.Record, DiscoverOpportunities,
                new Progress<OpportunityValidationProgress>(update =>
                {
                    if (_closing || !ReferenceEquals(_work, work)) return;
                    StatusText.Text = $"Checking folders · {update.Completed:N0} / {update.Total:N0}";
                    ScanProgressBar.IsIndeterminate = false;
                    ScanProgressBar.Maximum = Math.Max(1, update.Total);
                    ScanProgressBar.Value = update.Completed;
                }), FastMetadataSource());
            var gitCheck = GitWorktreeGuard.CheckAsync(validation.Snapshot, validation.ValidatedLocations, checkedWorktrees, work.Token, log.Record,
                new Progress<string>(path => { if (!_closing && ReferenceEquals(_work, work)) StatusText.Text = "Checking worktree… " + path; }));
            _worktreeCheckTask = gitCheck;
            IReadOnlyDictionary<string, string> gitBlocks;
            try { gitBlocks = await gitCheck; }
            finally { if (ReferenceEquals(_worktreeCheckTask, gitCheck)) _worktreeCheckTask = null; }
            var plan = await Task.Run(() => CleanupPlanner.Prepare(validation.Snapshot, validation.ValidatedLocations, work.Token, gitBlocks, DiscoverOpportunities, buildPolicy), work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (_closing || !ReferenceEquals(_snapshot, scan)) return;
            if (validation.CanMergeInventory)
            {
                _staleScopes.UnionWith(validation.UncheckedScopes);
                await RefreshAffectedAsync(scan, validation.RecheckedScopes, "Selected locations checked.", work.Token, log,
                    validation.Snapshot, validation.Findings);
                if (_closing || !ReferenceEquals(_snapshot, validation.Snapshot)) return;
            }
            else
                MarkScanStale("Selected locations checked for review. The full inventory retains earlier logical-size data.", [new(scan.RootPath)]);
            // A replacement at the same path is a new target, never a retained
            // checkbox selection. Missing/no-longer-matching rows are excluded too.
            var excluded = validation.ExcludedPaths.ToHashSet(StringComparer.Ordinal);
            var retained = validation.ValidatedLocations.Select(location => (location.ProviderId, location.Path, location.RuleId)).ToHashSet();
            _changingOpportunitySelection = true;
            try { foreach (var row in _rows) if (row.Selected && !retained.Contains(row.SelectionIdentity)) row.Selected = false; }
            finally { _changingOpportunitySelection = false; }
            UpdateSelection();
            string note = $"Selected locations checked at {DateTime.Now:HH:mm:ss}. "
                + (excluded.Count == 1 ? "1 location was excluded because it is missing, changed or no longer matches. "
                    : excluded.Count > 1 ? $"{excluded.Count:N0} locations were excluded because they are missing, changed or no longer match. " : "")
                + "Other folders retain earlier scan data.";
            if (checkedWorktrees.Count > 0) note += " Worktree rules checked: "
                + string.Join("; ", checkedWorktrees.Values.Distinct().Select(DescribeWorktreePolicy))
                + ". Ignored contents are included; recycling preserves branches and leaves Git registration in place.";
            StatusText.Text = plan.Preview.Locations.Count + plan.BlockedLocations.Count == 0 ? "None of the selected locations still match. Selection updated; scan again to discover new opportunities." : note;
            log.Record("review-validation-complete", new { ElapsedMs = timer.Elapsed.TotalMilliseconds,
                Validated = validation.ValidatedLocations.Count, validation.ExcludedPaths, validation.CanMergeInventory, plan.Preview.EstimatedBytes, Blocked = plan.BlockedLocations.Count });
            if (work.IsCancellationRequested)
            {
                StatusText.Text = "Review cancelled. No preview was opened.";
                log.Record("review-cancelled", new { Phase = "before-preview", validation.CanMergeInventory });
                return;
            }
            if (plan.Preview.Locations.Count + plan.BlockedLocations.Count > 0)
            {
                var displayed = _snapshot!;
                new ReviewWindow(plan, note, (chosenPlan, progress, token) =>
                {
                    var cleanup = ExecuteCleanupAsync(chosenPlan, displayed, progress, token, work, log, checkedWorktrees);
                    _cleanupTask = cleanup; return cleanup;
                }, _cleanupGroups) { Owner = this }.ShowDialog();
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
