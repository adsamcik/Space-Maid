using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private OpportunityGroupSet _opportunityGroups = new([], []);
    private IReadOnlyList<OpportunityGroupRow> _opportunityGroupRows = [];
    private IReadOnlyList<OpportunityRow> _matchingOpportunityRows = [], _visibleOpportunityRows = [];
    private string? _activeOpportunityGroup;
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
            OpportunitySearch.Text = "";
            OpportunityTypeFilter.ItemsSource = new[] { "All types" }; OpportunityTypeFilter.SelectedIndex = 0;
        }
        finally { _syncOpportunityBrowser = false; }
        ApplyOpportunityFilters();
    }
    private void PublishOpportunities(ScanSnapshot scan, IReadOnlyList<Opportunity> locations, OpportunityGroupSet groups,
        IReadOnlySet<string>? selectedPaths = null, string? detailPath = null, bool preserveScope = false)
    {
        foreach (var row in _rows) row.PropertyChanged -= Row_Changed;
        _rows.Clear(); _opportunityGroups = groups;
        var owners = groups.Projects.SelectMany(group => group.Locations.Select(item => (item.DirectoryIndex, Group: group)))
            .DistinctBy(item => item.DirectoryIndex).ToDictionary(item => item.DirectoryIndex, item => item.Group);
        foreach (var location in locations)
        {
            var row = new OpportunityRow(location, scan.HasAllocationData, owners.GetValueOrDefault(location.DirectoryIndex))
                { Selected = selectedPaths?.Contains(location.Path) == true };
            row.PropertyChanged += Row_Changed; _rows.Add(row);
        }
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
            _activeOpportunityGroup = (OpportunityGroupingPicker.SelectedIndex == 1 ? groups.Types : groups.Projects).FirstOrDefault()?.Key;
        ApplyOpportunityFilters(detailPath);
    }
    private void OpportunitySearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_opportunityBrowserReady || _syncOpportunityBrowser) return;
        _opportunitySearchTimer.Stop(); _opportunitySearchTimer.Start();
    }
    private void OpportunityFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_opportunityBrowserReady || _syncOpportunityBrowser) return;
        if (ReferenceEquals(sender, OpportunityGroupingPicker)) _activeOpportunityGroup = null;
        _opportunitySearchTimer.Stop(); ApplyOpportunityFilters();
    }
    private void ApplyOpportunityFilters(string? detailPath = null)
    {
        if (!_opportunityBrowserReady) return;
        detailPath ??= (OpportunityGrid.SelectedItem as OpportunityRow)?.Path;
        string query = OpportunitySearch.Text.Trim();
        string? type = OpportunityTypeFilter.SelectedItem as string;
        _matchingOpportunityRows = _rows.Where(row => (type is null or "All types" || row.Category == type)
            && (query.Length == 0 || row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Path.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        bool byType = OpportunityGroupingPicker.SelectedIndex == 1;
        var definitions = byType ? _opportunityGroups.Types : _opportunityGroups.Projects;
        var matches = _matchingOpportunityRows.GroupBy(row => byType ? "type:" + row.Category : row.GroupKey)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<OpportunityRow>)group.ToArray(), StringComparer.Ordinal);
        var groups = definitions.Where(group => matches.ContainsKey(group.Key)).Select(group => new OpportunityGroupRow(group, matches[group.Key]));
        _opportunityGroupRows = (OpportunitySortPicker.SelectedIndex switch
        {
            1 => groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.Path, StringComparer.Ordinal),
            2 => groups.OrderByDescending(group => group.Group.Locations.Count).ThenByDescending(group => group.Group.ObservedBytes),
            _ => groups.OrderByDescending(group => group.Group.ObservedBytes).ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
        }).ToArray();
        if (!_opportunityGroupRows.Any(group => group.Group.Key == _activeOpportunityGroup)) _activeOpportunityGroup = null;
        _syncOpportunityBrowser = true;
        try
        {
            OpportunityGroupList.ItemsSource = _opportunityGroupRows;
            OpportunityGroupList.SelectedItem = _opportunityGroupRows.FirstOrDefault(group => group.Group.Key == _activeOpportunityGroup);
        }
        finally { _syncOpportunityBrowser = false; }
        GroupNavigationSummary.Text = $"{_opportunityGroupRows.Count:N0} of {definitions.Count:N0} groups";
        GroupSizeNote.Text = "Full group totals. " + (byType ? "Sizes may overlap across types. " : "Overlapping folders counted once per group. ")
            + (_snapshot?.HasAllocationData == false ? "Logical size; physical savings unknown." : "Observed allocation; savings need review.");
        AllOpportunitiesButton.Content = $"All locations · {_matchingOpportunityRows.Count:N0}";
        DisplayOpportunityRows(detailPath);
    }
    private void OpportunityGroup_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncOpportunityBrowser || !_opportunityBrowserReady) return;
        _activeOpportunityGroup = (OpportunityGroupList.SelectedItem as OpportunityGroupRow)?.Group.Key;
        DisplayOpportunityRows();
    }
    private void AllOpportunities_Click(object sender, RoutedEventArgs e)
    {
        _activeOpportunityGroup = null;
        _syncOpportunityBrowser = true;
        try { OpportunityGroupList.SelectedItem = null; }
        finally { _syncOpportunityBrowser = false; }
        DisplayOpportunityRows();
    }
    private void DisplayOpportunityRows(string? detailPath = null)
    {
        var active = _opportunityGroupRows.FirstOrDefault(group => group.Group.Key == _activeOpportunityGroup);
        var rows = active?.Matches ?? _matchingOpportunityRows;
        _visibleOpportunityRows = (OpportunitySortPicker.SelectedIndex == 1 ? rows.OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            : rows.OrderByDescending(row => row.Bytes).ThenBy(row => row.Path, StringComparer.Ordinal)).ToArray();
        foreach (var row in _visibleOpportunityRows) row.SetScope(active?.Path);
        OpportunityGrid.ItemsSource = _visibleOpportunityRows;
        OpportunityGrid.SelectedItem = _visibleOpportunityRows.FirstOrDefault(row => row.Path.Equals(detailPath, StringComparison.Ordinal))
            ?? _visibleOpportunityRows.FirstOrDefault();
        OpportunityHeading.Text = active?.Name ?? "All locations";
        OpportunityScopeText.Text = active?.Path ?? _snapshot?.RootPath ?? "";
        OpportunityResultSummary.Text = active is null ? $"{rows.Count:N0} of {_rows.Count:N0} locations · All groups"
            : $"{rows.Count:N0} of {active.Group.Locations.Count:N0} locations · {active.Group.Kind}";
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _snapshot is null ? "Find room for your next project." : _rows.Count == 0 ? "No matching development clutter." : "No matching locations.";
        EmptyCaption.Text = _snapshot is null ? "Scan a drive to review build output, caches,\nenvironments, and files left by coding agents."
            : _rows.Count == 0 ? "No matching development clutter was found in the accessible files." : "Clear the search or change the type filter to see more locations.";
        UpdateSelection();
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
        int selected = _rows.Count(row => row.Selected);
        int hidden = selected - _visibleOpportunityRows.Count(row => row.Selected);
        SelectionText.Text = selected > 0 ? $"{selected:N0} {(selected == 1 ? "location" : "locations")} selected"
            + (hidden > 0 ? $" · {hidden:N0} outside this view" : "") : "Select locations for read-only review";
        ReviewButton.IsEnabled = selected > 0 && _work is null && !_scanStale;
        SelectListedButton.IsEnabled = _visibleOpportunityRows.Count > 0 && _work is null && !_scanStale;
        ClearOpportunitySelectionButton.IsEnabled = selected > 0 && _work is null;
        bool byType = OpportunityGroupingPicker.SelectedIndex == 1;
        var counts = _rows.Where(row => row.Selected).GroupBy(row => byType ? "type:" + row.Category : row.GroupKey)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var group in _opportunityGroupRows) group.SetSelectedCount(counts.GetValueOrDefault(group.Group.Key));
        OpportunityFreshnessText.Visibility = _scanStale ? Visibility.Visible : Visibility.Collapsed;
        OpportunityFreshnessText.Text = "Out of date · Recheck affected folders or rescan before review.";
    }
}
