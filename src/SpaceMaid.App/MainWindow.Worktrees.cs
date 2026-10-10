using System.Windows;
using System.Diagnostics;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private Task? _worktreeCheckTask;
    private readonly HashSet<string> _worktreesRequiringCheck = new(StringComparer.Ordinal);

    private async void CheckWorktrees_Click(object sender, RoutedEventArgs e)
    {
        var task = CheckWorktreesAsync(); _worktreeCheckTask = task;
        try { await task; }
        finally { _worktreeCheckTask = null; }
    }

    internal async Task CheckWorktreesAsync()
    {
        if (_snapshot is not { } scan || _work is not null) return;
        var locations = _rows.Where(row => row.Category == "Git worktrees").Select(row => row.Item).ToArray();
        if (locations.Length == 0) return;
        using var work = new CancellationTokenSource();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        using var log = StartActionLog("check-worktrees", scan.RootPath);
        _work = work; SetBusy(true);
        if (locations.All(item => _worktreeAssessments.ContainsKey(item.Path))) _worktreeAssessments.Clear();
        var remaining = locations.Where(item => !_worktreeAssessments.ContainsKey(item.Path)).ToArray();
        var timer = Stopwatch.StartNew();
        log.Record("worktree-check-start", new { Total = locations.Length, Remaining = remaining.Length, PerPathSeconds = 10, JobSeconds = 120 });
        try
        {
            for (int i = 0; i < remaining.Length; i++)
            {
                StatusText.Text = $"Checking worktrees · {_worktreeAssessments.Count + 1:N0} of {locations.Length:N0} · {remaining[i].Path}";
                var result = await GitWorktreeAnalyzer.AssessAsync(scan, remaining[i], budget.Token);
                log.Record("worktree-assessed", result);
                if (_closing || !ReferenceEquals(_snapshot, scan)) return;
                _worktreeAssessments[result.Path] = result;
            }
            StatusText.Text = $"Worktree check complete · {_worktreeAssessments.Values.Count(item => item.Recommended):N0} old, clean, integrated checkouts found.";
        }
        catch (OperationCanceledException)
        {
            if (!_closing) StatusText.Text = work.IsCancellationRequested
                ? "Worktree check cancelled. Completed checks retained; check remaining worktrees to continue."
                : "Worktree check paused at its two-minute limit. Completed checks retained; check remaining worktrees to continue.";
        }
        finally
        {
            log.Record("worktree-check-complete", new { Checked = _worktreeAssessments.Count, Recommended = _worktreeAssessments.Values.Count(item => item.Recommended),
                ElapsedMs = timer.Elapsed.TotalMilliseconds, Cancelled = work.IsCancellationRequested, LimitReached = budget.IsCancellationRequested && !work.IsCancellationRequested });
            _work = null;
            if (!_closing && ReferenceEquals(_snapshot, scan))
            {
                _cleanupGroups = CleanupGrouping.Build(scan, _rows.Select(row => row.Item).ToArray(),
                    _worktreeAssessments.Values.Where(item => item.Recommended).Select(item => item.Path).ToHashSet(StringComparer.Ordinal));
                WorktreeCheckSummary.Visibility = Visibility.Visible;
                WorktreeCheckSummary.Text = $"{_worktreeAssessments.Count:N0} of {locations.Length:N0} checked · {_worktreeAssessments.Values.Count(item => item.Recommended):N0} old, clean and integrated. "
                    + "30-day activity threshold · Cached remote refs; no fetch · Rechecked before cleanup. Recycling keeps branches and Git registration.";
                ApplyOpportunityFilters(); ShowActionLogError(log); SetBusy(false);
            }
        }
    }

    private string[] SelectedCheckedWorktrees(IReadOnlyList<Opportunity> selected)
    {
        _worktreesRequiringCheck.UnionWith(_worktreeAssessments.Values.Where(item => item.Recommended
            && selected.Any(location => GitWorktreeGuard.Covers(location.Path, item.Path))).Select(item => item.Path));
        return _worktreesRequiringCheck.Where(path => selected.Any(location => GitWorktreeGuard.Covers(location.Path, path))).ToArray();
    }
}
