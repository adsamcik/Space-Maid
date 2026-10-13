using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private OpportunityGroupSet _opportunityGroups = new([], []);
    private IReadOnlyList<OpportunityGroup> _cleanupGroups = [];
    private readonly Dictionary<string, GitWorktreeAssessment> _worktreeAssessments = new(StringComparer.Ordinal);
    private bool _cleanupDrillDown;
    private IReadOnlyList<OpportunityGroupRow> _opportunityGroupRows = [];
    private IReadOnlyList<OpportunityRow> _matchingOpportunityRows = [], _visibleOpportunityRows = [];
    private string? _activeOpportunityGroup;
    private string? _cleanupDetailKey, _cleanupDetailName, _cleanupDetailParentName;
    private OpportunityGroup? _cleanupProjectScope;
    private bool _opportunityBrowserReady, _syncOpportunityBrowser, _changingOpportunitySelection;
    private readonly DispatcherTimer _opportunitySearchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private void InitializeOpportunityBrowser()
    {
        _opportunitySearchTimer.Tick += (_, _) => { _opportunitySearchTimer.Stop(); if (!_closing) ApplyOpportunityFilters(); };
        _opportunityBrowserReady = true;
        ResetOpportunityBrowser();
    }
    private void ResetOpportunityBrowser()
    {
        _opportunitySearchTimer.Stop();
        _syncOpportunityBrowser = true;
        try
        {
            _opportunityGroups = new([], []); _activeOpportunityGroup = null;
            _cleanupGroups = []; _worktreeAssessments.Clear(); _cleanupDrillDown = false;
            _cleanupDetailKey = _cleanupDetailName = null;
            _cleanupProjectScope = null;
            _worktreesRequiringCheck.Clear();
            OpportunitySearch.Text = "";
            OpportunityTypeFilter.ItemsSource = new[] { "All types" }; OpportunityTypeFilter.SelectedIndex = 0;
        }
        finally { _syncOpportunityBrowser = false; }
        RebuildWorktreeGroups();
        ApplyOpportunityFilters();
    }
    private void PublishOpportunities(ScanSnapshot scan, IReadOnlyList<Opportunity> locations, OpportunityGroupSet groups,
        IReadOnlySet<(string Provider, string Path, string Rule)>? selectedLocations = null, string? detailPath = null, bool preserveScope = false)
    {
        foreach (var row in _rows) row.PropertyChanged -= Row_Changed;
        bool wasMatchedWorktrees = _cleanupGroups.FirstOrDefault(group => group.Key == _activeOpportunityGroup)?.Role == OpportunityGroupRole.MatchedWorktrees;
        _rows.Clear(); _opportunityGroups = groups;
        if (!preserveScope) _worktreesRequiringCheck.Clear();
        else foreach (string path in _worktreesRequiringCheck.Keys.Where(path => selectedLocations is null || !selectedLocations.Any(selected => GitWorktreeGuard.Covers(selected.Path, path))).ToArray())
            _worktreesRequiringCheck.Remove(path);
        _worktreeAssessments.Clear();
        var owners = groups.Projects.SelectMany(group => group.Locations.Select(item => (Identity: (item.ProviderId, item.DirectoryIndex, item.RuleId), Group: group)))
            .DistinctBy(item => item.Identity).ToDictionary(item => item.Identity, item => item.Group);
        foreach (var location in locations)
        {
            var row = new OpportunityRow(location, scan.HasAllocationData, owners.GetValueOrDefault((location.ProviderId, location.DirectoryIndex, location.RuleId)), scan.IsUnmeasuredDirectory(location.DirectoryIndex))
                { Selected = selectedLocations?.Contains((location.ProviderId, location.Path, location.RuleId)) == true };
            row.PropertyChanged += Row_Changed; _rows.Add(row);
        }
        RebuildWorktreeGroups();
        string? previousType = OpportunityTypeFilter.SelectedItem as string;
        _syncOpportunityBrowser = true;
        try
        {
            OpportunityTypeFilter.ItemsSource = new[] { "All types" }.Concat(locations.Select(item => item.Category).Distinct().Order()).ToArray();
            OpportunityTypeFilter.SelectedItem = previousType;
            if (OpportunityTypeFilter.SelectedIndex < 0) OpportunityTypeFilter.SelectedIndex = 0;
        }
        finally { _syncOpportunityBrowser = false; }
        if (!preserveScope)
        {
            _cleanupDrillDown = false;
            _cleanupDetailKey = _cleanupDetailName = null;
            _cleanupProjectScope = null;
            _activeOpportunityGroup = OpportunityGroupingPicker.SelectedIndex == 0 ? null
                : (OpportunityGroupingPicker.SelectedIndex == 2 ? groups.Types : groups.Projects).FirstOrDefault()?.Key;
        }
        else if (wasMatchedWorktrees && !_cleanupGroups.Any(group => group.Key == _activeOpportunityGroup))
            _activeOpportunityGroup = _cleanupGroups.FirstOrDefault(group => group.Role == OpportunityGroupRole.Worktrees)?.Key;
        ApplyOpportunityFilters(detailPath);
    }
    private void OpportunitySearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_opportunityBrowserReady || _syncOpportunityBrowser) return;
        _cleanupProjectScope = null; // An explicit search may show other projects within the chosen breakdown.
        _opportunitySearchTimer.Stop(); _opportunitySearchTimer.Start();
    }
    private void OpportunityFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_opportunityBrowserReady || _syncOpportunityBrowser) return;
        if (ReferenceEquals(sender, OpportunityGroupingPicker))
        {
            _activeOpportunityGroup = null; _cleanupDrillDown = false;
            _cleanupDetailKey = _cleanupDetailName = null;
            _cleanupProjectScope = null;
            if (OpportunityGroupingPicker.SelectedIndex == 0) ClearOpportunityFilters();
            else _activeOpportunityGroup = (OpportunityGroupingPicker.SelectedIndex == 2 ? _opportunityGroups.Types : _opportunityGroups.Projects).FirstOrDefault()?.Key;
        }
        _opportunitySearchTimer.Stop(); ApplyOpportunityFilters();
    }
    private void ApplyOpportunityFilters(string? detailPath = null)
    {
        if (!_opportunityBrowserReady) return;
        detailPath ??= (OpportunityGrid.SelectedItem as OpportunityRow)?.Path;
        string query = OpportunitySearch.Text.Trim();
        string? type = OpportunityTypeFilter.SelectedItem as string;
        bool cleanup = OpportunityGroupingPicker.SelectedIndex == 0;
        var detail = _cleanupGroups.SelectMany(group => group.Children ?? []).FirstOrDefault(group => group.Key == _cleanupDetailKey);
        bool detailScope = cleanup && _cleanupDetailKey is not null;
        var detailMembers = detailScope ? (detail?.Locations ?? []).Select(item => (item.ProviderId, item.DirectoryIndex, item.RuleId)).ToHashSet() : null;
        _matchingOpportunityRows = _rows.Where(row => (detailMembers is null || detailMembers.Contains(row.Identity)) && (type is null or "All types" || row.Category == type)
            && (query.Length == 0 || row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Path.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        bool byType = OpportunityGroupingPicker.SelectedIndex == 2;
        var definitions = detailScope ? detail?.Children ?? [] : cleanup ? _cleanupGroups : byType ? _opportunityGroups.Types : _opportunityGroups.Projects;
        var byIdentity = _matchingOpportunityRows.ToDictionary(row => row.Identity);
        var matches = definitions.ToDictionary(group => group.Key, group => (IReadOnlyList<OpportunityRow>)group.Locations
            .Select(item => byIdentity.GetValueOrDefault((item.ProviderId, item.DirectoryIndex, item.RuleId)))
            .OfType<OpportunityRow>().ToArray(), StringComparer.Ordinal);
        foreach (string key in matches.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToArray()) matches.Remove(key);
        var visibleGroups = definitions.Where(group => matches.ContainsKey(group.Key)).ToArray();
        long largestGroup = visibleGroups.Select(group => group.ObservedBytes).DefaultIfEmpty().Max();
        var groups = visibleGroups.Select(group => new OpportunityGroupRow(group, matches[group.Key], showChildren: cleanup && !_cleanupDrillDown, comparisonBytes: largestGroup));
        _opportunityGroupRows = (OpportunitySortPicker.SelectedIndex switch
        {
            1 => groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.Path, StringComparer.Ordinal),
            2 => groups.OrderByDescending(group => group.Group.Locations.Count).ThenByDescending(group => group.Group.ObservedBytes),
            _ => groups.OrderByDescending(group => group.Group.ObservedBytes).ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
        }).ToArray();
        if (!_opportunityGroupRows.Any(group => group.Group.Key == _activeOpportunityGroup) && (!detailScope || _cleanupProjectScope is null)) _activeOpportunityGroup = null;
        _syncOpportunityBrowser = true;
        try
        {
            OpportunityGroupList.ItemsSource = _opportunityGroupRows;
            CleanupGroupCards.ItemsSource = cleanup ? _opportunityGroupRows : null;
            OpportunityGroupList.SelectedItem = _opportunityGroupRows.FirstOrDefault(group => group.Group.Key == _activeOpportunityGroup);
        }
        finally { _syncOpportunityBrowser = false; }
        GroupNavigationSummary.Text = $"{_opportunityGroupRows.Count:N0}"
            + (_opportunityGroupRows.Count == definitions.Count ? "" : $" of {definitions.Count:N0}")
            + " " + (detailScope ? definitions.Count == 1 ? "project" : "projects" : definitions.Count == 1 ? "group" : "groups");
        GroupSizeNote.Text = _snapshot?.HasAllocationData == false ? "Logical size · Physical savings unknown" : "Estimates";
        GroupSizeNote.ToolTip = byType ? "Sizes may overlap across types." : "Overlapping folders are counted once per group.";
        AllOpportunitiesButton.Content = $"All locations · {_matchingOpportunityRows.Count:N0}";
        CleanupScopeText.Visibility = detailScope ? Visibility.Visible : Visibility.Collapsed;
        CleanupScopeText.Text = detailScope ? _cleanupDetailParentName + " › " + _cleanupDetailName : "";
        CleanupOverviewNote.Text = _snapshot is null ? "Scan a drive to find cleanup groups."
            : _extensions is not null && OpportunityExtensions.Status(_snapshot) is { Length: > 0 } status ? status
            : _cleanupGroups.Count == 0 ? "No cleanup groups found."
            : (_snapshot.HasAllocationData ? "Estimates" : "Logical size · Physical savings unknown")
                + " · Groups may overlap";
        CleanupOverviewNote.ToolTip = "Bars compare each group's observed size with the largest visible group. Groups may overlap. Review counts nested folders once. Empty the Recycle Bin to free space.";
        DisplayOpportunityRows(detailPath);
    }
    private void OpportunityGroup_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncOpportunityBrowser || !_opportunityBrowserReady) return;
        _activeOpportunityGroup = (OpportunityGroupList.SelectedItem as OpportunityGroupRow)?.Group.Key;
        _cleanupProjectScope = _cleanupDetailKey is not null ? (OpportunityGroupList.SelectedItem as OpportunityGroupRow)?.Group : null;
        DisplayOpportunityRows();
    }
    private void AllOpportunities_Click(object sender, RoutedEventArgs e)
    {
        _activeOpportunityGroup = null;
        _cleanupProjectScope = null;
        _syncOpportunityBrowser = true;
        try { OpportunityGroupList.SelectedItem = null; }
        finally { _syncOpportunityBrowser = false; }
        DisplayOpportunityRows();
    }
    private void DisplayOpportunityRows(string? detailPath = null)
    {
        bool overview = OpportunityGroupingPicker.SelectedIndex == 0 && !_cleanupDrillDown;
        CleanupOverview.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        OpportunityNavigation.Visibility = OpportunityLocations.Visibility = OpportunityDetails.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        BackToCleanupButton.Visibility = !overview && OpportunityGroupingPicker.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpportunityPanelTitle.Visibility = BackToCleanupButton.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        OpportunityPanelTitle.Text = OpportunityGroupingPicker.SelectedIndex switch { 1 => "Projects", 2 => "Clutter types", _ => "Cleanup groups" };
        SelectListedButton.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        var active = _opportunityGroupRows.FirstOrDefault(group => group.Group.Key == _activeOpportunityGroup);
        if (active is not null && _cleanupDetailKey is not null) _cleanupProjectScope = active.Group;
        bool missingProject = _cleanupDetailKey is not null && _cleanupProjectScope is not null && active is null;
        var rows = missingProject ? Array.Empty<OpportunityRow>() : active?.Matches ?? _matchingOpportunityRows;
        _visibleOpportunityRows = (OpportunitySortPicker.SelectedIndex == 1 ? rows.OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            : rows.OrderByDescending(row => row.Bytes).ThenBy(row => row.Path, StringComparer.Ordinal)).ToArray();
        foreach (var row in _visibleOpportunityRows) row.SetScope(string.IsNullOrEmpty(active?.Path) ? null : active.Path);
        OpportunityGrid.ItemsSource = overview ? Array.Empty<OpportunityRow>() : _visibleOpportunityRows;
        OpportunityGrid.SelectedItem = _visibleOpportunityRows.FirstOrDefault(row => row.Path.Equals(detailPath, StringComparison.Ordinal))
            ?? _visibleOpportunityRows.FirstOrDefault();
        OpportunityHeading.Text = active?.Name ?? (missingProject ? _cleanupProjectScope!.Name : _cleanupDetailKey is not null ? _cleanupDetailName : "All locations");
        OpportunityScopeText.Text = active?.Path ?? (missingProject ? _cleanupProjectScope!.Path : _snapshot?.RootPath) ?? "";
        OpportunityScopeText.Visibility = OpportunityScopeText.Text == _currentPath ? Visibility.Collapsed : Visibility.Visible;
        int total = active?.Group.Locations.Count ?? (_cleanupDetailKey is not null
            ? _cleanupGroups.SelectMany(group => group.Children ?? []).FirstOrDefault(group => group.Key == _cleanupDetailKey)?.Locations.Count ?? 0 : _rows.Count);
        OpportunityResultSummary.Text = missingProject ? "No matching folders remain in this project."
            : $"{rows.Count:N0}" + (rows.Count == total ? "" : $" of {total:N0}")
                + $" {(total == 1 ? "folder" : "folders")}";
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _snapshot is null ? "Find cleanup opportunities" : _rows.Count == 0 ? "No cleanup opportunities found" : "No matching folders";
        EmptyCaption.Text = missingProject ? "Choose another project or All locations."
            : _snapshot is null ? "Scan a drive to start."
            : _rows.Count == 0 ? "" : "Clear search or change filters.";
        UpdateSelection();
    }
    private void CleanupGroupSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || sender is not CheckBox { DataContext: OpportunityGroupRow group } || !group.CanSelectGroup) return;
        bool select = group.Checked != true;
        _changingOpportunitySelection = true;
        try { foreach (var row in group.Matches) row.Selected = select; }
        finally { _changingOpportunitySelection = false; }
        UpdateSelection();
    }
    private void ViewCleanupGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: OpportunityGroupRow group }) return;
        _cleanupDrillDown = true; _activeOpportunityGroup = group.Group.Key;
        _cleanupDetailKey = _cleanupDetailName = null;
        _cleanupProjectScope = null;
        ApplyOpportunityFilters();
    }
    private bool RevealRecommendedGroup(DriveRecommendation recommendation)
    {
        // Stable keys survive caption changes. Older saved history still
        // resolves by its original group name, including the worktree alias.
        var group = recommendation.GroupKey is { } key
            ? _cleanupGroups.FirstOrDefault(item => item.Key == key)
            : _cleanupGroups.FirstOrDefault(item => item.Name == recommendation.Name
                || recommendation.Name == "Worktrees to review" && item.Role == OpportunityGroupRole.Worktrees);
        _opportunitySearchTimer.Stop();
        OpportunityGroupingPicker.SelectedIndex = 0;
        ClearOpportunityFilters();
        _cleanupDrillDown = group is not null; _activeOpportunityGroup = group?.Key;
        _cleanupDetailKey = _cleanupDetailName = _cleanupDetailParentName = null;
        _cleanupProjectScope = null;
        ApplyOpportunityFilters(); OpportunitiesTab.IsChecked = true;
        ViewOptionsToggle.IsChecked = WorktreeOptionsToggle.IsChecked = false;
        if (group is null) StatusText.Text = recommendation.Name + " no longer found · Showing cleanup groups";
        else OpportunityGrid.Focus();
        return group is not null;
    }
    private void ViewCleanupProjects_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || sender is not Button { DataContext: OpportunityGroupRow group }) return;
        _cleanupDetailKey = group.Group.Key; _cleanupDetailName = group.Name;
        _cleanupDetailParentName = _cleanupGroups.FirstOrDefault(parent => parent.Children?.Any(child => child.Key == group.Group.Key) == true)?.Name ?? "Cleanup groups";
        _cleanupDrillDown = true; _activeOpportunityGroup = null;
        _cleanupProjectScope = null;
        ClearOpportunityFilters(); ApplyOpportunityFilters();
        if (OpportunityGroupList.Items.Count > 0) OpportunityGroupList.SelectedIndex = 0;
    }
    private void BackToCleanup_Click(object sender, RoutedEventArgs e)
    {
        _cleanupDrillDown = false; _activeOpportunityGroup = null;
        _cleanupDetailKey = _cleanupDetailName = null;
        _cleanupProjectScope = null;
        ClearOpportunityFilters(); ApplyOpportunityFilters();
    }
    private void ClearOpportunityFilters()
    {
        _syncOpportunityBrowser = true;
        try { OpportunitySearch.Text = ""; OpportunityTypeFilter.SelectedIndex = 0; }
        finally { _syncOpportunityBrowser = false; }
    }
    private void SelectListedOpportunities_Click(object sender, RoutedEventArgs e)
    {
        _changingOpportunitySelection = true;
        try { foreach (var row in _visibleOpportunityRows) row.Selected = true; }
        finally { _changingOpportunitySelection = false; }
        UpdateSelection();
    }
    private void ClearOpportunitySelection_Click(object sender, RoutedEventArgs e)
    {
        _changingOpportunitySelection = true;
        try { foreach (var row in _rows) row.Selected = false; }
        finally { _changingOpportunitySelection = false; }
        UpdateSelection();
    }
    private void UpdateOpportunitySelection()
    {
        var choices = _rows.Where(row => row.Selected).Select(row => row.Item).ToArray();
        _ = SelectedCheckedWorktrees(choices);
        foreach (string path in _worktreesRequiringCheck.Keys.Where(path => !choices.Any(choice => GitWorktreeGuard.Covers(choice.Path, path))).ToArray())
            _worktreesRequiringCheck.Remove(path);
        int selected = _rows.Count(row => row.Selected);
        int hidden = selected - _visibleOpportunityRows.Count(row => row.Selected);
        bool overview = OpportunityGroupingPicker.SelectedIndex == 0 && !_cleanupDrillDown;
        SelectionText.Text = selected > 0 ? $"{selected:N0} {(selected == 1 ? "location" : "locations")} selected"
            + (hidden > 0 ? $" · {hidden:N0} outside this view" : "") : "";
        if (overview)
        {
            int groups = _opportunityGroupRows.Count(group => group.Matches.Any(row => row.Selected));
            SelectionText.Text = selected > 0 ? $"{groups:N0} {(groups == 1 ? "group" : "groups")} selected · {selected:N0} {(selected == 1 ? "folder" : "folders")}"
                : "";
        }
        ReviewButton.IsEnabled = selected > 0 && _work is null;
        SelectListedButton.IsEnabled = _visibleOpportunityRows.Count > 0 && _work is null;
        ClearOpportunitySelectionButton.IsEnabled = selected > 0 && _work is null;
        ClearOpportunitySelectionButton.Visibility = selected > 0 ? Visibility.Visible : Visibility.Collapsed;
        CheckWorktreesButton.IsEnabled = _work is null && _rows.Any(row => row.Item.IsWorktree);
        int worktrees = _rows.Count(row => row.Item.IsWorktree);
        CheckWorktreesButton.Content = _worktreeAssessments.Count == 0 ? "Check worktrees"
            : _worktreeAssessments.Count < worktrees ? "Check remaining worktrees" : "Recheck worktrees";
        foreach (var group in _opportunityGroupRows) group.SetSelectedCount(group.Matches.Count(row => row.Selected));
        CleanupFreshnessText.Visibility = _scanStale ? Visibility.Visible : Visibility.Collapsed;
        CleanupFreshnessText.Text = "Out of date · Review rechecks your selection";
        OpportunitiesTab.Content = _snapshot is null ? "Opportunities"
            : OpportunityGroupingPicker.SelectedIndex == 0 ? $"Opportunities · {_cleanupGroups.Count:N0} {(_cleanupGroups.Count == 1 ? "group" : "groups")}" : $"Opportunities · {_rows.Count:N0}";
    }

    private void OpportunityOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender == ViewOptionsToggle && ViewOptionsToggle.IsChecked == true) WorktreeOptionsToggle.IsChecked = false;
        else if (sender == WorktreeOptionsToggle && WorktreeOptionsToggle.IsChecked == true) ViewOptionsToggle.IsChecked = false;
    }
}
