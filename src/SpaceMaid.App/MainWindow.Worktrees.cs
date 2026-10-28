using System.Windows;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private Task? _worktreeCheckTask;
    private readonly Dictionary<string, WorktreeCleanupPolicy> _worktreesRequiringCheck = new(StringComparer.Ordinal);
    private WorktreeCleanupPolicy CurrentWorktreePolicy => OpportunityBrowser?.WorktreePolicy ?? WorktreeCleanupPolicy.Default;

    private static string DescribeWorktreePolicy(WorktreeCleanupPolicy policy) => policy.Mode switch
    {
        WorktreeCleanupMode.Old => $"clean with no observed activity for at least {policy.InactiveDays} days (branches kept; detached commits must be integrated)",
        WorktreeCleanupMode.Integrated => "clean and already in a local integration branch, any age",
        _ => $"clean, already in a local integration branch and no observed activity for at least {policy.InactiveDays} days"
    };

    internal async Task CheckWorktreesAsync()
    {
        if (_snapshot is not { } scan || _work is not null) return;
        var locations = _rows.Where(row => row.Item.IsWorktree).Select(row => row.Item).DistinctBy(item => item.Path).ToArray();
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
                var result = await Operations.AssessWorktreeAsync(scan, remaining[i], budget.Token);
                log.Record("worktree-assessed", result);
                if (_closing || !ReferenceEquals(_snapshot, scan)) return;
                _worktreeAssessments[result.Path] = result;
            }
            StatusText.Text = $"Worktree check complete · {_worktreeAssessments.Values.Count(item => CurrentWorktreePolicy.Matches(item, DateTime.UtcNow)):N0} worktrees match the cleanup rule.";
        }
        catch (OperationCanceledException)
        {
            if (!_closing) StatusText.Text = work.IsCancellationRequested
                ? "Worktree check cancelled. Completed checks retained; check remaining worktrees to continue."
                : "Worktree check paused at its two-minute limit. Completed checks retained; check remaining worktrees to continue.";
        }
        finally
        {
            log.Record("worktree-check-complete", new { Checked = _worktreeAssessments.Count, Matching = _worktreeAssessments.Values.Count(item => CurrentWorktreePolicy.Matches(item, DateTime.UtcNow)), Policy = CurrentWorktreePolicy,
                ElapsedMs = timer.Elapsed.TotalMilliseconds, Cancelled = work.IsCancellationRequested, LimitReached = budget.IsCancellationRequested && !work.IsCancellationRequested });
            _work = null;
            if (!_closing && ReferenceEquals(_snapshot, scan))
            {
                OpportunityBrowser?.RefreshGroups(); ShowActionLogError(log); SetBusy(false);
            }
        }
    }

    private IReadOnlyDictionary<string, WorktreeCleanupPolicy> SelectedCheckedWorktrees(IReadOnlyList<Opportunity> selected)
    {
        foreach (var row in _rows.Where(row => row.Item.IsWorktree))
            if (selected.Any(location => GitWorktreeGuard.Covers(location.Path, row.Path)))
                _worktreesRequiringCheck.TryAdd(row.Path, CurrentWorktreePolicy);
        var captured = _worktreesRequiringCheck.Where(item => selected.Any(location => GitWorktreeGuard.Covers(location.Path, item.Key)))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, WorktreeCleanupPolicy>(captured);
    }
}
