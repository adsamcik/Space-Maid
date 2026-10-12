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
    private WorktreeCleanupPolicy CurrentWorktreePolicy => new((WorktreeCleanupMode)WorktreeRulePicker.SelectedIndex,
        (int)Math.Round(WorktreeAgeSlider.Value));

    private static string DescribeWorktreePolicy(WorktreeCleanupPolicy policy) => policy.Mode switch
    {
        WorktreeCleanupMode.Old => $"clean with no observed activity for at least {policy.InactiveDays} days (unmerged branches kept)",
        WorktreeCleanupMode.Integrated => "clean and already in cached upstream, any age",
        _ => $"clean, already in cached upstream and no observed activity for at least {policy.InactiveDays} days"
    };

    private void WorktreeRule_Changed(object sender, SelectionChangedEventArgs e) => WorktreeOptionsChanged();
    private void WorktreeAge_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => WorktreeOptionsChanged();

    private void SelectWorktreeMatches_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null) return;
        var members = _cleanupGroups.Where(group => group.Role == OpportunityGroupRole.MatchedWorktrees).SelectMany(group => group.Locations)
            .Select(item => (item.ProviderId, item.DirectoryIndex, item.RuleId)).ToHashSet();
        if (members.Count == 0) return;
        _changingOpportunitySelection = true;
        try { foreach (var row in _rows) if (members.Contains(row.Identity)) row.Selected = true; }
        finally { _changingOpportunitySelection = false; }
        UpdateSelection();
    }

    private void WorktreeOptionsChanged()
    {
        if (!_opportunityBrowserReady || _syncOpportunityBrowser || _work is not null) return;
        var policy = CurrentWorktreePolicy;
        // Changing filters never adds a target. Checked worktrees outside the
        // new rule are unchecked; retained targets adopt the displayed rule.
        _changingOpportunitySelection = true;
        try
        {
            foreach (var row in _rows.Where(row => row.Selected && row.Item.IsWorktree))
            {
                if (!_worktreeAssessments.TryGetValue(row.Path, out var assessment) || !policy.Matches(assessment, DateTime.UtcNow))
                    row.Selected = false;
            }
            foreach (string path in _worktreesRequiringCheck.Keys.ToArray()) _worktreesRequiringCheck[path] = policy;
        }
        finally { _changingOpportunitySelection = false; }
        RebuildWorktreeGroups(); ApplyOpportunityFilters();
    }

    private void RebuildWorktreeGroups()
    {
        var policy = CurrentWorktreePolicy;
        var matching = _worktreeAssessments.Values.Where(item => policy.Matches(item, DateTime.UtcNow))
            .Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        _cleanupGroups = _snapshot is { } scan && _extensions is not null ? OpportunityExtensions.BuildCleanupGroups(scan,
            _rows.Select(row => row.Item).ToArray(), _opportunityGroups, new OpportunityGroupContext(matching, policy)) : [];
        int total = _rows.Count(row => row.Item.IsWorktree);
        WorktreeOptionsToggle.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        WorktreeOptionsToggle.Content = $"Worktrees · {total:N0}";
        if (total == 0) WorktreeOptionsToggle.IsChecked = false;
        bool ageApplies = policy.Mode != WorktreeCleanupMode.Integrated;
        WorktreeAgeSlider.IsEnabled = WorktreeAgeLabel.IsEnabled = ageApplies;
        WorktreeAgeText.Text = ageApplies ? $"{policy.InactiveDays} {(policy.InactiveDays == 1 ? "day" : "days")}" : "Any age";
        WorktreeRuleHint.ToolTip = policy.Mode switch
        {
            WorktreeCleanupMode.Old => "Age uses file changes and Git activity, not access. Unmerged commits stay in their branches. Local changes are protected.",
            WorktreeCleanupMode.Integrated => "Age is ignored. Uses locally cached upstream/default refs; no fetch. Local changes are protected; branches are kept.",
            _ => "Age uses file changes and Git activity, not access. Uses cached upstream refs. Local changes are protected; branches are kept."
        };
        WorktreeRuleHint.Text = policy.Mode == WorktreeCleanupMode.Old
            ? "Local changes protected · Branches kept"
            : "Cached upstream refs · Local changes protected · Branches kept";
        var matchedGroups = _cleanupGroups.Where(item => item.Role == OpportunityGroupRole.MatchedWorktrees).ToArray();
        SelectWorktreeMatchesButton.IsEnabled = matchedGroups.Any(group => group.Locations.Count > 0);
        WorktreeCheckSummary.Text = _worktreeAssessments.Count == 0
            ? $"{total:N0} worktrees · Not checked"
            : $"{matching.Count:N0} {(matching.Count == 1 ? "worktree matches" : "worktrees match")} · {SizeText.Format(matchedGroups.Sum(group => group.ObservedBytes))} observed · {_worktreeAssessments.Count:N0} of {total:N0} checked";
    }

    private async void CheckWorktrees_Click(object sender, RoutedEventArgs e)
    {
        var task = CheckWorktreesAsync(); _worktreeCheckTask = task;
        try { await task; }
        finally { _worktreeCheckTask = null; }
    }

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
                var result = await GitWorktreeAnalyzer.AssessAsync(scan, remaining[i], budget.Token);
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
                RebuildWorktreeGroups();
                ApplyOpportunityFilters(); ShowActionLogError(log); SetBusy(false);
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
