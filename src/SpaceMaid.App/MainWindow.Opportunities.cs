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
    private string? _buildLanguageKey, _buildLanguageName;
    private OpportunityGroup? _buildProjectScope;
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
            _buildLanguageKey = _buildLanguageName = null;
            _buildProjectScope = null;
            _worktreesRequiringCheck.Clear();
            OpportunitySearch.Text = "";
            OpportunityTypeFilter.ItemsSource = new[] { "All types" }; OpportunityTypeFilter.SelectedIndex = 0;
        }
        finally { _syncOpportunityBrowser = false; }
        RebuildWorktreeGroups();
        ApplyOpportunityFilters();
    }
    private void PublishOpportunities(ScanSnapshot scan, IReadOnlyList<Opportunity> locations, OpportunityGroupSet groups,
        IReadOnlySet<string>? selectedPaths = null, string? detailPath = null, bool preserveScope = false)
    {
        foreach (var row in _rows) row.PropertyChanged -= Row_Changed;
        _rows.Clear(); _opportunityGroups = groups;
        if (!preserveScope) _worktreesRequiringCheck.Clear();
        else foreach (string path in _worktreesRequiringCheck.Keys.Where(path => selectedPaths is null || !selectedPaths.Any(selected => GitWorktreeGuard.Covers(selected, path))).ToArray())
            _worktreesRequiringCheck.Remove(path);
        _worktreeAssessments.Clear();
        var owners = groups.Projects.SelectMany(group => group.Locations.Select(item => (item.DirectoryIndex, Group: group)))
            .DistinctBy(item => item.DirectoryIndex).ToDictionary(item => item.DirectoryIndex, item => item.Group);
        foreach (var location in locations)
        {
            var row = new OpportunityRow(location, scan.HasAllocationData, owners.GetValueOrDefault(location.DirectoryIndex), scan.IsUnmeasuredDirectory(location.DirectoryIndex))
                { Selected = selectedPaths?.Contains(location.Path) == true };
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
            _buildLanguageKey = _buildLanguageName = null;
            _buildProjectScope = null;
            _activeOpportunityGroup = OpportunityGroupingPicker.SelectedIndex == 0 ? null
                : (OpportunityGroupingPicker.SelectedIndex == 2 ? groups.Types : groups.Projects).FirstOrDefault()?.Key;
        }
        else if (_activeOpportunityGroup == "cleanup:old-worktrees") _activeOpportunityGroup = "cleanup:worktrees";
        ApplyOpportunityFilters(detailPath);
    }
    private void OpportunitySearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_opportunityBrowserReady || _syncOpportunityBrowser) return;
        _buildProjectScope = null; // An explicit search may show other projects within the chosen language.
        _opportunitySearchTimer.Stop(); _opportunitySearchTimer.Start();
    }
    private void OpportunityFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_opportunityBrowserReady || _syncOpportunityBrowser) return;
        if (ReferenceEquals(sender, OpportunityGroupingPicker))
        {
            _activeOpportunityGroup = null; _cleanupDrillDown = false;
            _buildLanguageKey = _buildLanguageName = null;
            _buildProjectScope = null;
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
        var language = (_opportunityGroups.BuildLanguages ?? []).FirstOrDefault(group => group.Key == _buildLanguageKey);
        bool languageScope = cleanup && _buildLanguageKey is not null;
        var buildPaths = languageScope ? (language?.Locations ?? []).Select(item => item.Path).ToHashSet(StringComparer.Ordinal) : null;
        _matchingOpportunityRows = _rows.Where(row => (buildPaths is null || buildPaths.Contains(row.Path)) && (type is null or "All types" || row.Category == type)
            && (query.Length == 0 || row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Path.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        bool byType = OpportunityGroupingPicker.SelectedIndex == 2;
        var definitions = languageScope ? language?.Children ?? [] : cleanup ? _cleanupGroups : byType ? _opportunityGroups.Types : _opportunityGroups.Projects;
        var membership = definitions.SelectMany(group => group.Locations.Select(item => (item.Path, group.Key)))
            .ToDictionary(item => item.Path, item => item.Key, StringComparer.Ordinal);
        var matches = _matchingOpportunityRows.GroupBy(row => membership.GetValueOrDefault(row.Path) ?? "")
            .ToDictionary(group => group.Key, group => (IReadOnlyList<OpportunityRow>)group.ToArray(), StringComparer.Ordinal);
        var groups = definitions.Where(group => matches.ContainsKey(group.Key)).Select(group => new OpportunityGroupRow(group, matches[group.Key], showChildren: cleanup && !_cleanupDrillDown));
        _opportunityGroupRows = (OpportunitySortPicker.SelectedIndex switch
        {
            1 => groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.Path, StringComparer.Ordinal),
            2 => groups.OrderByDescending(group => group.Group.Locations.Count).ThenByDescending(group => group.Group.ObservedBytes),
            _ => groups.OrderByDescending(group => group.Group.ObservedBytes).ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
        }).ToArray();
        if (!_opportunityGroupRows.Any(group => group.Group.Key == _activeOpportunityGroup) && (!languageScope || _buildProjectScope is null)) _activeOpportunityGroup = null;
        _syncOpportunityBrowser = true;
        try
        {
            OpportunityGroupList.ItemsSource = _opportunityGroupRows;
            CleanupGroupCards.ItemsSource = cleanup ? _opportunityGroupRows : null;
            OpportunityGroupList.SelectedItem = _opportunityGroupRows.FirstOrDefault(group => group.Group.Key == _activeOpportunityGroup);
        }
        finally { _syncOpportunityBrowser = false; }
        GroupNavigationSummary.Text = $"{_opportunityGroupRows.Count:N0} of {definitions.Count:N0} {(languageScope ? "projects" : "groups")}";
        GroupSizeNote.Text = "Observed group totals. " + (byType ? "Sizes may overlap across types. " : "Overlapping folders counted once per group. ")
            + (_snapshot?.HasAllocationData == false ? "Logical size; physical savings unknown." : "Observed allocation; savings need review.");
        AllOpportunitiesButton.Content = $"All locations · {_matchingOpportunityRows.Count:N0}";
        BuildScopeText.Visibility = languageScope ? Visibility.Visible : Visibility.Collapsed;
        BuildScopeText.Text = languageScope ? "Build files › " + _buildLanguageName : "";
        CleanupOverviewNote.Text = _snapshot is null ? "Scan a drive to find cleanup groups."
            : _cleanupGroups.Count == 0 ? "No matching development folders found."
            : (_snapshot.HasAllocationData ? "Observed allocation" : "Logical size · Physical savings unknown")
                + " · Groups can overlap; review counts nested folders once. Recycled files occupy space until the bin is emptied.";
        DisplayOpportunityRows(detailPath);
    }
    private void OpportunityGroup_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncOpportunityBrowser || !_opportunityBrowserReady) return;
        _activeOpportunityGroup = (OpportunityGroupList.SelectedItem as OpportunityGroupRow)?.Group.Key;
        _buildProjectScope = _buildLanguageKey is not null ? (OpportunityGroupList.SelectedItem as OpportunityGroupRow)?.Group : null;
        DisplayOpportunityRows();
    }
    private void AllOpportunities_Click(object sender, RoutedEventArgs e)
    {
        _activeOpportunityGroup = null;
        _buildProjectScope = null;
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
        SelectListedButton.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        var active = _opportunityGroupRows.FirstOrDefault(group => group.Group.Key == _activeOpportunityGroup);
        if (active is not null && _buildLanguageKey is not null) _buildProjectScope = active.Group;
        bool missingProject = _buildLanguageKey is not null && _buildProjectScope is not null && active is null;
        var rows = missingProject ? Array.Empty<OpportunityRow>() : active?.Matches ?? _matchingOpportunityRows;
        _visibleOpportunityRows = (OpportunitySortPicker.SelectedIndex == 1 ? rows.OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            : rows.OrderByDescending(row => row.Bytes).ThenBy(row => row.Path, StringComparer.Ordinal)).ToArray();
        foreach (var row in _visibleOpportunityRows) row.SetScope(string.IsNullOrEmpty(active?.Path) ? null : active.Path);
        OpportunityGrid.ItemsSource = overview ? Array.Empty<OpportunityRow>() : _visibleOpportunityRows;
        OpportunityGrid.SelectedItem = _visibleOpportunityRows.FirstOrDefault(row => row.Path.Equals(detailPath, StringComparison.Ordinal))
            ?? _visibleOpportunityRows.FirstOrDefault();
        OpportunityHeading.Text = active?.Name ?? (missingProject ? _buildProjectScope!.Name : _buildLanguageKey is not null ? _buildLanguageName : "All locations");
        OpportunityScopeText.Text = active?.Path ?? (missingProject ? _buildProjectScope!.Path : _snapshot?.RootPath) ?? "";
        OpportunityResultSummary.Text = missingProject ? "No matching build folders remain in this project. Choose another project or All locations."
            : active is null ? _buildLanguageKey is not null
            ? $"{rows.Count:N0} of {((_opportunityGroups.BuildLanguages ?? []).FirstOrDefault(group => group.Key == _buildLanguageKey)?.Locations.Count ?? 0):N0} build locations · All projects"
            : $"{rows.Count:N0} of {_rows.Count:N0} locations · All groups"
            : $"{rows.Count:N0} of {active.Group.Locations.Count:N0} locations · {active.Group.Kind}";
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _snapshot is null ? "Find room for your next project." : _rows.Count == 0 ? "No matching development clutter." : "No matching locations.";
        EmptyCaption.Text = missingProject ? "Return to Cleanup groups, choose another project, or use All locations."
            : _snapshot is null ? "Scan a drive to review build output, caches,\nenvironments, and files left by coding agents."
            : _rows.Count == 0 ? "No matching development clutter was found in the accessible files." : "Clear the search or change the type filter to see more locations.";
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
        _buildLanguageKey = _buildLanguageName = null;
        _buildProjectScope = null;
        ApplyOpportunityFilters();
    }
    private void ViewBuildProjects_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || sender is not Button { DataContext: OpportunityGroupRow group }) return;
        _buildLanguageKey = group.Group.Key; _buildLanguageName = group.Name;
        _cleanupDrillDown = true; _activeOpportunityGroup = null;
        _buildProjectScope = null;
        ClearOpportunityFilters(); ApplyOpportunityFilters();
        if (OpportunityGroupList.Items.Count > 0) OpportunityGroupList.SelectedIndex = 0;
    }
    private void BackToCleanup_Click(object sender, RoutedEventArgs e)
    {
        _cleanupDrillDown = false; _activeOpportunityGroup = null;
        _buildLanguageKey = _buildLanguageName = null;
        _buildProjectScope = null;
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
            + (hidden > 0 ? $" · {hidden:N0} outside this view" : "") : "Select locations to review cleanup";
        if (overview)
        {
            int groups = _opportunityGroupRows.Count(group => group.Matches.Any(row => row.Selected));
            SelectionText.Text = selected > 0 ? $"{groups:N0} {(groups == 1 ? "group" : "groups")} selected · {selected:N0} folders"
                : "Choose the groups you want to review";
        }
        ReviewButton.IsEnabled = selected > 0 && _work is null;
        SelectListedButton.IsEnabled = _visibleOpportunityRows.Count > 0 && _work is null;
        ClearOpportunitySelectionButton.IsEnabled = selected > 0 && _work is null;
        CheckWorktreesButton.IsEnabled = _work is null && _rows.Any(row => row.Category == "Git worktrees");
        int worktrees = _rows.Count(row => row.Category == "Git worktrees");
        CheckWorktreesButton.Content = _worktreeAssessments.Count == 0 ? "Check worktrees"
            : _worktreeAssessments.Count < worktrees ? "Check remaining worktrees" : "Recheck worktrees";
        foreach (var group in _opportunityGroupRows) group.SetSelectedCount(group.Matches.Count(row => row.Selected));
        OpportunityFreshnessText.Visibility = _scanStale ? Visibility.Visible : Visibility.Collapsed;
        OpportunityFreshnessText.Text = "Some results are out of date. Review rechecks the selected locations.";
        CleanupFreshnessText.Visibility = _scanStale ? Visibility.Visible : Visibility.Collapsed;
        CleanupFreshnessText.Text = OpportunityFreshnessText.Text;
        OpportunitiesTab.Content = _scanStale ? "Opportunities · Out of date" : _snapshot is null ? "Opportunities"
            : OpportunityGroupingPicker.SelectedIndex == 0 ? $"Opportunities · {_cleanupGroups.Count:N0} groups" : $"Opportunities · {_rows.Count:N0}";
    }
}
