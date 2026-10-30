using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.IO;
using SpaceMaid.Extensions.Contracts;
using System.ComponentModel;
using System.Windows.Input;

namespace SpaceMaid.Extensions.Opportunities;

public partial class OpportunitiesView : UserControl, IOpportunityBrowserView, IDisposable
{
    private OpportunityGroupSet _opportunityGroups = new([], []);
    private IReadOnlyList<OpportunityGroup> _cleanupGroups = [];

    private bool _cleanupDrillDown;
    private IReadOnlyList<OpportunityGroupRow> _opportunityGroupRows = [];
    private IReadOnlyList<OpportunityRow> _matchingOpportunityRows = [], _visibleOpportunityRows = [];
    private string? _activeOpportunityGroup;
    private string? _cleanupDetailKey, _cleanupDetailName, _cleanupDetailParentName;
    private OpportunityGroup? _cleanupProjectScope;
    private bool _opportunityBrowserReady, _syncOpportunityBrowser, _changingOpportunitySelection;
    private readonly DispatcherTimer _opportunitySearchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly HashSet<string> _expandedCleanupGroups = new(StringComparer.Ordinal), _expandedCleanupContents = new(StringComparer.Ordinal);
    private double _cleanupOverviewOffset;

    private readonly IOpportunityBrowserHost _host;
    private IReadOnlyList<OpportunityRow> _rows = [];
    private OpportunityBrowserState? _state;
    private bool _busy, _disposed;
    private OpportunityRecycleBinState? _recycleBin;
    private string _currentPath = "";
    private bool _scanStale;
    private bool _buildRulesNeedRepair;
    private IReadOnlyDictionary<string, GitWorktreeAssessment> _worktreeAssessments
        => _state?.WorktreeAssessments ?? new Dictionary<string, GitWorktreeAssessment>();
    public OpportunitiesView(IOpportunityBrowserHost host) : this(host, DiskAccess.Default) { }
    public OpportunitiesView(IOpportunityBrowserHost host, DiskAccess files)
    {
        _host = host; _files = files; InitializeComponent();
        _opportunitySearchTimer.Tick += SearchElapsed;
        _opportunityBrowserReady = true; Reset(); UpdateSelection();
    }
    private void SearchElapsed(object? sender, EventArgs e)
    { _opportunitySearchTimer.Stop(); if (!_disposed) ApplyOpportunityFilters(); }
    public OpportunityRow? Detail => OpportunityGrid.SelectedItem as OpportunityRow;
    public WorktreeCleanupPolicy WorktreePolicy => new((WorktreeCleanupMode)WorktreeRulePicker.SelectedIndex, (int)Math.Round(WorktreeAgeSlider.Value));
    public IReadOnlyList<OpportunityGroup> CleanupGroups => _cleanupGroups;
    public string BuildRetention => BuildKeepRecentDaysBox.Text;
    public void SetBuildRetention(string value, bool needsRepair)
    { BuildKeepRecentDaysBox.Text = value; _buildRulesNeedRepair = needsRepair; if (needsRepair) ShowOptions(); }
    public void SetEmptyMessage(string message) => EmptyCaption.Text = message;
    public void ShowOptions()
    {
        BuildRulesToggle.Visibility = Visibility.Visible; BuildRulesToggle.IsChecked = true;
        ViewOptionsToggle.IsChecked = WorktreeOptionsToggle.IsChecked = false;
    }
    public void Reset()
    {
        foreach (var row in _rows) row.PropertyChanged -= RowChanged;
        _rows = []; _state = null; _opportunitySearchTimer.Stop();
        _lastCleanupBrowse = _selectionBrowse = null; SelectedOnlyToggle.IsChecked = false;
        _syncOpportunityBrowser = true;
        try
        {
            _opportunityGroups = new([], []); _activeOpportunityGroup = null;
            _cleanupGroups = []; _cleanupDrillDown = false;
            _cleanupDetailKey = _cleanupDetailName = _cleanupDetailParentName = null; _cleanupProjectScope = null;
            _expandedCleanupGroups.Clear(); _expandedCleanupContents.Clear(); _cleanupOverviewOffset = 0;
            OpportunitySearch.Text = "";
            OpportunityTypeFilter.ItemsSource = new[] { "All types" }; OpportunityTypeFilter.SelectedIndex = 0;
        }
        finally { _syncOpportunityBrowser = false; }
        RebuildWorktreeGroups(); ApplyOpportunityFilters();
    }
    public void Publish(OpportunityBrowserState state, string? detailPath = null, bool preserveScope = false)
    {
        bool wasMatchedWorktrees = _cleanupGroups.FirstOrDefault(group => group.Key == _activeOpportunityGroup)?.Role == OpportunityGroupRole.MatchedWorktrees;
        foreach (var row in _rows) row.PropertyChanged -= RowChanged;
        _state = state; _rows = state.Rows; _opportunityGroups = state.Groups;
        _currentPath = state.CurrentPath; _scanStale = state.Stale;
        foreach (var row in _rows) row.PropertyChanged += RowChanged;
        var groups = state.Groups;
        if (!preserveScope)
        {
            _expandedCleanupGroups.Clear(); _expandedCleanupContents.Clear(); _cleanupOverviewOffset = 0;
            _lastCleanupBrowse = _selectionBrowse = null; SelectedOnlyToggle.IsChecked = false;
        }
        RebuildWorktreeGroups();
        string? previousType = OpportunityTypeFilter.SelectedItem as string;
        _syncOpportunityBrowser = true;
        try
        {
            OpportunityTypeFilter.ItemsSource = new[] { "All types" }.Concat(_rows.Select(item => item.Item).Select(item => item.Category).Distinct().Order()).ToArray();
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
            SelectedOnlyToggle.IsChecked = false; _selectionBrowse = null;
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
        bool detailScope = cleanup && _cleanupDetailKey is not null && !SelectedView;
        var detailMembers = detailScope ? (detail?.Locations ?? []).Select(item => (item.ProviderId, item.DirectoryIndex, item.RuleId)).ToHashSet() : null;
        _matchingOpportunityRows = _rows.Where(row => (!SelectedView || row.Selected) && (detailMembers is null || detailMembers.Contains(row.Identity)) && (type is null or "All types" || row.Category == type)
            && (query.Length == 0 || row.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Path.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        bool byType = OpportunityGroupingPicker.SelectedIndex == 2;
        var definitions = SelectedView ? [] : detailScope ? detail?.Children ?? [] : cleanup ? _cleanupGroups : byType ? _opportunityGroups.Types : _opportunityGroups.Projects;
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
        foreach (var group in _opportunityGroupRows)
        {
            group.Expanded = _expandedCleanupGroups.Contains(group.Group.Key);
            foreach (var child in group.Children) child.ContentsExpanded = _expandedCleanupContents.Contains(child.Group.Key);
        }
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
        GroupNavigationSummary.Visibility = _opportunityGroupRows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        GroupSizeNote.Text = _state?.HasAllocationData == false ? "Logical size · Physical savings unknown" : "Estimates";
        GroupSizeNote.ToolTip = byType ? "Sizes may overlap across types." : "Overlapping folders are counted once per group.";
        AllOpportunitiesButton.Content = $"All locations · {_matchingOpportunityRows.Count:N0}";
        CleanupScopeText.Visibility = detailScope ? Visibility.Visible : Visibility.Collapsed;
        CleanupScopeText.Text = detailScope ? _cleanupDetailParentName + " › " + _cleanupDetailName : "";
        CleanupOverviewNote.Text = _state?.RootPath is null ? "Scan a drive to find cleanup groups."
            : _host.ProviderStatus is { Length: > 0 } status ? status
            : _cleanupGroups.Count == 0 ? "No cleanup groups found."
            : (_state!.HasAllocationData ? "Estimates" : "Logical size · Physical savings unknown")
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
        bool overview = OpportunityGroupingPicker.SelectedIndex == 0 && !_cleanupDrillDown && !SelectedView;
        bool returningToOverview = overview && CleanupOverview.Visibility != Visibility.Visible;
        CleanupOverview.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        OpportunityNavigation.Visibility = OpportunityLocations.Visibility = OpportunityDetails.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        if (SelectedView) OpportunityNavigation.Visibility = Visibility.Collapsed;
        Grid.SetColumn(OpportunityLocations, SelectedView ? 0 : 2);
        Grid.SetColumnSpan(OpportunityLocations, SelectedView ? 3 : 1);
        BackToCleanupButton.Visibility = !overview && OpportunityGroupingPicker.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpportunityPanelTitle.Visibility = BackToCleanupButton.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        OpportunityPanelTitle.Text = OpportunityGroupingPicker.SelectedIndex switch { 1 => "Projects", 2 => "Clutter types", _ => "Cleanup groups" };
        SelectListedButton.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        var active = SelectedView ? null : _opportunityGroupRows.FirstOrDefault(group => group.Group.Key == _activeOpportunityGroup);
        if (active is not null && _cleanupDetailKey is not null) _cleanupProjectScope = active.Group;
        bool missingProject = !SelectedView && _cleanupDetailKey is not null && _cleanupProjectScope is not null && active is null;
        var rows = missingProject ? Array.Empty<OpportunityRow>() : active?.Matches ?? _matchingOpportunityRows;
        _visibleOpportunityRows = (OpportunitySortPicker.SelectedIndex == 1 ? rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Path, StringComparer.Ordinal)
            : rows.OrderByDescending(row => row.Bytes).ThenBy(row => row.Path, StringComparer.Ordinal)).ToArray();
        foreach (var row in _visibleOpportunityRows) row.SetScope(string.IsNullOrEmpty(active?.Path) ? null : active.Path);
        OpportunityGrid.Tag = _visibleOpportunityRows.Select(row => row.Title).Distinct(StringComparer.Ordinal).Take(2).Count() == 1;
        OpportunityGrid.ItemsSource = overview ? Array.Empty<OpportunityRow>() : _visibleOpportunityRows;
        OpportunityGrid.SelectedItem = _visibleOpportunityRows.FirstOrDefault(row => row.Path.Equals(detailPath, StringComparison.Ordinal))
            ?? _visibleOpportunityRows.FirstOrDefault();
        OpportunityHeading.Text = SelectedView ? "Selected folders" : active?.Name ?? (missingProject ? _cleanupProjectScope!.Name : _cleanupDetailKey is not null ? _cleanupDetailName : "All locations");
        OpportunityScopeText.Text = active?.Path ?? (missingProject ? _cleanupProjectScope!.Path : _state?.RootPath) ?? "";
        OpportunityScopeText.Visibility = OpportunityScopeText.Text == _currentPath ? Visibility.Collapsed : Visibility.Visible;
        int total = SelectedView ? _rows.Count(row => row.Selected) : active?.Group.Locations.Count ?? (_cleanupDetailKey is not null
            ? _cleanupGroups.SelectMany(group => group.Children ?? []).FirstOrDefault(group => group.Key == _cleanupDetailKey)?.Locations.Count ?? 0 : _rows.Count);
        OpportunityResultSummary.Text = missingProject ? "No matching folders remain in this project."
            : $"{rows.Count:N0}" + (rows.Count == total ? "" : $" of {total:N0}")
                + $" {(total == 1 ? "folder" : "folders")}";
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = SelectedView && total == 0 ? "No folders selected" : _state?.RootPath is null ? "Find cleanup opportunities" : _rows.Count == 0 ? "No cleanup opportunities found" : "No matching folders";
        EmptyCaption.Text = SelectedView && total == 0 ? "" : missingProject ? "Choose another project or All locations."
            : _state?.RootPath is null ? "Scan a drive to start."
            : _rows.Count == 0 ? "" : "Clear search or change filters.";
        UpdateModelLibrary();
        BuildRulesToggle.Visibility = _buildRulesNeedRepair || (overview ? _rows : _visibleOpportunityRows).Any(row => row.Item.IsBuildOutput)
            ? Visibility.Visible : Visibility.Collapsed;
        if (BuildRulesToggle.Visibility != Visibility.Visible) BuildRulesToggle.IsChecked = false;
        SelectionChanged();
        if (returningToOverview) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (CleanupOverview.Visibility == Visibility.Visible) CleanupOverviewScroll.ScrollToVerticalOffset(_cleanupOverviewOffset);
        }));
    }
    private void CleanupExpander_Changed(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) || sender is not Expander { DataContext: OpportunityGroupRow group } expander) return;
        var expanded = expander.Name == "CleanupContentExpander" ? _expandedCleanupContents : _expandedCleanupGroups;
        if (expander.IsExpanded) expanded.Add(group.Group.Key); else expanded.Remove(group.Group.Key);
    }
    private void CleanupGroupSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not CheckBox { DataContext: OpportunityGroupRow group } || !group.CanSelectGroup) return;
        bool select = group.Checked != true;
        _changingOpportunitySelection = true;
        try { foreach (var row in group.Matches) row.Selected = select; }
        finally { _changingOpportunitySelection = false; }
        SelectionChanged();
    }
    private void ViewCleanupGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: OpportunityGroupRow group }) return;
        _cleanupOverviewOffset = CleanupOverviewScroll.VerticalOffset;
        _cleanupDrillDown = true; _activeOpportunityGroup = group.Group.Key;
        _cleanupDetailKey = _cleanupDetailName = null;
        _cleanupProjectScope = null;
        if (RestoreLastCleanupBrowse(group.Group.Key)) return;
        ApplyOpportunityFilters();
    }
    public bool ShowGroup(string? key, string name)
    {
        // Stable keys survive caption changes. Older saved history still
        // resolves by its original group name, including the worktree alias.
        var group = key is not null
            ? _cleanupGroups.FirstOrDefault(item => item.Key == key)
            : _cleanupGroups.FirstOrDefault(item => item.Name == name
                || name == "Worktrees to review" && item.Role == OpportunityGroupRole.Worktrees);
        _opportunitySearchTimer.Stop();
        OpportunityGroupingPicker.SelectedIndex = 0;
        ClearOpportunityFilters();
        _cleanupDrillDown = group is not null; _activeOpportunityGroup = group?.Key;
        _cleanupDetailKey = _cleanupDetailName = _cleanupDetailParentName = null;
        _cleanupProjectScope = null;
        ApplyOpportunityFilters();
        ViewOptionsToggle.IsChecked = WorktreeOptionsToggle.IsChecked = false;
        if (group is null) _host.ShowStatus(name + " no longer found · Showing cleanup groups");
        else OpportunityGrid.Focus();
        return group is not null;
    }
    private void ViewCleanupProjects_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: OpportunityGroupRow group }) return;
        _cleanupOverviewOffset = CleanupOverviewScroll.VerticalOffset;
        _cleanupDetailKey = group.Group.Key; _cleanupDetailName = group.Name;
        _cleanupDetailParentName = _cleanupGroups.FirstOrDefault(parent => parent.Children?.Any(child => child.Key == group.Group.Key) == true)?.Name ?? "Cleanup groups";
        _cleanupDrillDown = true; _activeOpportunityGroup = null;
        _cleanupProjectScope = null;
        if (RestoreLastCleanupBrowse(group.Group.Key)) return;
        ClearOpportunityFilters(); ApplyOpportunityFilters();
        if (OpportunityGroupList.Items.Count > 0) OpportunityGroupList.SelectedIndex = 0;
    }
    private void BackToCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedView)
        {
            SelectedOnlyToggle.IsChecked = false;
            if (_selectionBrowse is { } location) RestoreBrowseLocation(location);
            _selectionBrowse = null;
        }
        _lastCleanupBrowse = CaptureBrowseLocation();
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
        SelectionChanged();
    }
    private void ClearOpportunitySelection_Click(object sender, RoutedEventArgs e)
    {
        _changingOpportunitySelection = true;
        try { foreach (var row in _rows) row.Selected = false; }
        finally { _changingOpportunitySelection = false; }
        if (SelectedView) ApplyOpportunityFilters(); else SelectionChanged();
    }
    public void UpdateSelection()
    {
        int selected = _rows.Count(row => row.Selected);
        int hidden = selected - _visibleOpportunityRows.Count(row => row.Selected);
        bool overview = OpportunityGroupingPicker.SelectedIndex == 0 && !_cleanupDrillDown && !SelectedView;
        SelectionText.Text = selected > 0 ? $"{selected:N0} {(selected == 1 ? "location" : "locations")} selected"
            + (hidden > 0 ? $" · {hidden:N0} outside this view" : "") : "";
        if (overview)
        {
            int groups = _opportunityGroupRows.Count(group => group.Matches.Any(row => row.Selected));
            SelectionText.Text = selected > 0 ? $"{groups:N0} {(groups == 1 ? "group" : "groups")} selected · {selected:N0} {(selected == 1 ? "folder" : "folders")}"
                : "";
        }
        ReviewButton.IsEnabled = selected > 0 && !_busy;
        SelectListedButton.IsEnabled = _visibleOpportunityRows.Any(row => row.CanSelect) && !_busy;
        ClearOpportunitySelectionButton.IsEnabled = selected > 0 && !_busy;
        ClearOpportunitySelectionButton.Visibility = selected > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedOnlyToggle.Visibility = !overview && (selected > 0 || SelectedView) ? Visibility.Visible : Visibility.Collapsed;
        SelectedOnlyToggle.Content = SelectedView ? "Back to folders" : "Show selected";
        SelectedOnlyToggle.IsEnabled = !_busy;
        bool modelsOnly = OllamaModelsPanel.Visibility == Visibility.Visible && OpportunityGrid.Visibility == Visibility.Collapsed;
        FolderActionsBar.Visibility = modelsOnly && selected == 0 ? Visibility.Collapsed : Visibility.Visible;
        SelectListedButton.Visibility = overview || modelsOnly ? Visibility.Collapsed : Visibility.Visible;
        CheckWorktreesButton.IsEnabled = !_busy && _rows.Any(row => row.Item.IsWorktree);
        int worktrees = _rows.Count(row => row.Item.IsWorktree);
        CheckWorktreesButton.Content = _worktreeAssessments.Count == 0 ? "Check worktrees"
            : _worktreeAssessments.Count < worktrees ? "Check remaining worktrees" : "Recheck worktrees";
        foreach (var group in _opportunityGroupRows) group.SetSelectedCount(group.Matches.Count(row => row.Selected));
        CleanupFreshnessText.Visibility = _scanStale ? Visibility.Visible : Visibility.Collapsed;
        CleanupFreshnessText.Text = "Out of date · Rechecked at review";
        _host.SetCaption("Cleanup");
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OpportunityRow.Selected) || _changingOpportunitySelection) return;
        if (SelectedView) ApplyOpportunityFilters(); else SelectionChanged();
    }
    private void SelectionChanged() => _host.SelectionChanged();
    public void UpdateContext(string currentPath, bool stale)
    {
        _currentPath = currentPath; _scanStale = stale;
        OpportunityScopeText.Visibility = OpportunityScopeText.Text == currentPath ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelection();
    }
    public void SetBusy(bool busy)
    {
        _busy = busy;
        BuildKeepRecentDaysBox.IsEnabled = OpportunityGrid.IsEnabled = GroupBrowserControls.IsEnabled = OpportunityFilters.IsEnabled
            = OpportunityToolbar.IsEnabled = CleanupGroupCards.IsEnabled = WorktreeCleanupControls.IsEnabled = OllamaModelsPanel.IsEnabled = !busy;
        ApplyRecycleBinState();
        UpdateModelRemoval();
        UpdateSelection();
    }
    public void UpdateRecycleBin(OpportunityRecycleBinState state) { _recycleBin = state; ApplyRecycleBinState(); }
    private void ApplyRecycleBinState()
    {
        RecycleBinRow.IsEnabled = !_busy;
        if (_recycleBin is not { } state) return;
        RecycleBinTitle.Text = state.Title; RecycleBinSize.Text = state.Size; RecycleBinCount.Text = state.Count;
        RecycleBinRow.ToolTip = state.Hint; EmptyRecycleBinButton.IsEnabled = !_busy && state.CanEmpty;
    }
    private async void RefreshRecycleBin_Click(object sender, RoutedEventArgs e) => await _host.RefreshRecycleBinAsync();
    private async void EmptyRecycleBin_Click(object sender, RoutedEventArgs e) => await _host.EmptyRecycleBinAsync();
    public void RefreshGroups() { RebuildWorktreeGroups(); ApplyOpportunityFilters(); }
    private async void Review_Click(object sender, RoutedEventArgs e) => await _host.ReviewSelectionAsync();
    private async void CheckWorktrees_Click(object sender, RoutedEventArgs e) => await _host.CheckWorktreesAsync();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) { if (Detail is { } row) _host.OpenFolder(row.Path); }
    private async void ShowInTree_Click(object sender, RoutedEventArgs e) { if (Detail is { } row) await _host.ShowInTreeAsync(row.Item); }
    private void BuildRetention_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; _host.SaveBuildRetention(BuildRetention); } }
    private void BuildRetention_LostFocus(object sender, RoutedEventArgs e) => _host.SaveBuildRetention(BuildRetention);
    private void Opportunity_Changed(object sender, SelectionChangedEventArgs e)
    {
        var row = Detail;
        UpdateModelLibrary();
        DetailPanel.DataContext = row?.Item;
        DetailName.Text = row?.Name ?? "";
        DetailPanel.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        DetailHint.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        BuildFolderContentsPanel.Visibility = row?.Category == "Build output" ? Visibility.Visible : Visibility.Collapsed;
        var contents = row?.Category == "Build output" ? _host.FolderContents(row.Item) : new OpportunityFolderContents([], "");
        BuildFolderContentsList.ItemsSource = contents.Items; BuildFolderContentsNote.Text = contents.Note;
        var assessment = row is null ? null : _worktreeAssessments.GetValueOrDefault(row.Path);
        WorktreeDetailText.Visibility = assessment is null ? Visibility.Collapsed : Visibility.Visible;
        WorktreeDetailText.Text = assessment is null ? "" : assessment.Status + ": " + assessment.Reason
            + (assessment.Branch is null ? "" : "\nBranch: " + assessment.Branch)
            + (assessment.IntegrationTarget is null ? "" : "\nCompared with cached ref: " + assessment.IntegrationTarget)
            + (assessment.LastActivityUtc is { } activity ? "\nLast observed activity: " + activity.ToLocalTime().ToString("g") : "\nLast observed activity: unavailable")
            + (assessment.VerifiedClean ? "\nLocal tracked/untracked changes: none" : "")
            + (assessment.Integrated ? "\nCommits already in upstream: yes" : "");
        UpdateModelDetails(); UpdateSelection();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _modelRead?.Cancel();
        _modelRemoval?.Cancel();
        _opportunitySearchTimer.Stop(); _opportunitySearchTimer.Tick -= SearchElapsed;
        foreach (var row in _rows) row.PropertyChanged -= RowChanged;
    }
    private void OpportunityOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { IsChecked: true }) return;
        if (sender != ViewOptionsToggle) ViewOptionsToggle.IsChecked = false;
        if (sender != WorktreeOptionsToggle) WorktreeOptionsToggle.IsChecked = false;
        if (sender != BuildRulesToggle) BuildRulesToggle.IsChecked = false;
    }
    private void WorktreeRule_Changed(object sender, SelectionChangedEventArgs e) => WorktreeOptionsChanged();
    private void WorktreeAge_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => WorktreeOptionsChanged();

    private void SelectWorktreeMatches_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var members = _cleanupGroups.Where(group => group.Role == OpportunityGroupRole.MatchedWorktrees).SelectMany(group => group.Locations)
            .Select(item => (item.ProviderId, item.DirectoryIndex, item.RuleId)).ToHashSet();
        if (members.Count == 0) return;
        _changingOpportunitySelection = true;
        try { foreach (var row in _rows) if (members.Contains(row.Identity)) row.Selected = true; }
        finally { _changingOpportunitySelection = false; }
        SelectionChanged();
    }

    private void WorktreeOptionsChanged()
    {
        if (!_opportunityBrowserReady || _syncOpportunityBrowser || _busy) return;
        var policy = WorktreePolicy;
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
            _host.WorktreePolicyChanged();
        }
        finally { _changingOpportunitySelection = false; }
        RebuildWorktreeGroups(); ApplyOpportunityFilters();
    }

    private void RebuildWorktreeGroups()
    {
        var policy = WorktreePolicy;
        var matching = _worktreeAssessments.Values.Where(item => policy.Matches(item, DateTime.UtcNow))
            .Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        _cleanupGroups = _state?.RootPath is not null ? _host.BuildCleanupGroups(new(matching, policy)) : [];
        int total = _rows.Count(row => row.Item.IsWorktree);
        WorktreeOptionsToggle.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        WorktreeOptionsToggle.Content = $"Worktrees · {total:N0}";
        if (total == 0) WorktreeOptionsToggle.IsChecked = false;
        bool ageApplies = policy.Mode != WorktreeCleanupMode.Integrated;
        WorktreeAgeSlider.IsEnabled = WorktreeAgeLabel.IsEnabled = ageApplies;
        WorktreeAgeText.Text = ageApplies ? $"{policy.InactiveDays} {(policy.InactiveDays == 1 ? "day" : "days")}" : "Any age";
        WorktreeRuleHint.ToolTip = policy.Mode switch
        {
            WorktreeCleanupMode.Old => "Age uses file changes and Git activity, not access. Unmerged commits stay in their branches. Detached commits must be integrated. Stop agents and builds; review ignored files.",
            WorktreeCleanupMode.Integrated => "Age is ignored. Uses a single cached remote default, or main/master for detached checkouts; no fetch. Feature upstream retention is a separate fact. Changed and ignored local files are protected.",
            _ => "Age uses creation, file changes and Git activity, not actual use. Uses a single cached remote default, or main/master for detached checkouts. Changed and ignored local files are protected."
        };
        WorktreeRuleHint.Text = policy.Mode == WorktreeCleanupMode.Old
            ? "Unmerged branches kept"
            : "Local refs · Changed worktrees kept";
        var matchedGroups = _cleanupGroups.Where(item => item.Role == OpportunityGroupRole.MatchedWorktrees).ToArray();
        SelectWorktreeMatchesButton.IsEnabled = matchedGroups.Any(group => group.Locations.Count > 0);
        WorktreeCheckSummary.Text = _worktreeAssessments.Count == 0
            ? $"{total:N0} worktrees · Not checked"
            : $"{matching.Count:N0} {(matching.Count == 1 ? "worktree matches" : "worktrees match")} · {OpportunitySizeText.Format(matchedGroups.Sum(group => group.ObservedBytes))} observed · {_worktreeAssessments.Count:N0} of {total:N0} checked";
    }

}
