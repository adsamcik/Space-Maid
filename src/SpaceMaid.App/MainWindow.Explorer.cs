using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private ExplorerRows? _explorer;
    private readonly Dictionary<int, int[]> _childOrders = [];
    private readonly Dictionary<int, int[]> _mapOrders = [];
    private IReadOnlyDictionary<int, SpaceKind> _kinds = new Dictionary<int, SpaceKind>();
    private CancellationTokenSource? _browseWork;
    private TaskCompletionSource? _browseIdle;
    private bool _foregroundBusy;
    private bool _browsing, _syncTree, _selectionIsGroup;
    private bool _preferLogicalSize, _updatingSizeBasis;
    private int _selectedLink = -1;
    private int _explorerRevision;

    private void ClearExplorer()
    {
        _browseWork?.Cancel();
        // Menus capture a scan for command safety; discard their callbacks
        // when clearing results so a prior large inventory is not retained.
        ClearExplorerMenus();
        ClearMap();
        _explorer = null;
        SizeBasisSelector.IsEnabled = false;
        _selectedLink = -1;
        _selectionIsGroup = false;
        _childOrders.Clear(); _mapOrders.Clear(); _kinds = new Dictionary<int, SpaceKind>();
        SpaceTree.ItemsSource = null;
        EntryDetail.DataContext = null; EntryDetail.Visibility = Visibility.Collapsed;
        MapHint.Visibility = TreeHint.Visibility = Visibility.Visible;
        MapHint.Text = "Select a folder.";
        TreeHint.Text = "Scan a folder to explore.";
        UpdateExplorerActions();
    }

    internal async Task InitializeExplorerAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> opportunities, CancellationToken token)
    {
        await _classificationReady.WaitAsync(token);
        ClearExplorer();
        _snapshot = scan;
        _scanStale = false;
        _staleScopes.Clear();
        _explorer = new(scan) { PreferLogicalSize = _preferLogicalSize };
        _kinds = OpportunityKinds(opportunities);
        SpaceTree.ItemsSource = _explorer;
        TreeHint.Visibility = Visibility.Collapsed;
        TreeNote.Text = scan.HasAllocationData ? "Shared files counted once"
            : "Logical sizes only · Physical allocation is unavailable";
        UpdateExplorerSizeLabels();
        var visibleSort = VisibleTreeSort(scan);
        if (_treeSortColumn != visibleSort) _treeDescending = visibleSort != ScanSortColumn.Name;
        _treeSortColumn = visibleSort;
        UpdateTreeSortHeaders();
        var children = await ChildOrderAsync(scan.RootIndex, token);
        _explorer.Expand(-1, children);
        SelectTree(-1);
        await DisplayEntryAsync(-1, token);
        token.ThrowIfCancellationRequested();
        ClearPreliminary();
    }

    private static Dictionary<int, SpaceKind> OpportunityKinds(IReadOnlyList<Opportunity> opportunities)
    {
        var kinds = new Dictionary<int, SpaceKind>();
        foreach (var opportunity in opportunities)
        {
            var kind = opportunity.Appearance switch
            {
                OpportunityAppearance.Agent => SpaceKind.Agent,
                OpportunityAppearance.Project => SpaceKind.Project,
                OpportunityAppearance.Cache => SpaceKind.Cache,
                _ => SpaceKind.Other
            };
            kinds[opportunity.DirectoryIndex] = kind;
        }
        return kinds;
    }

    private sealed record PreparedExplorer(ExplorerRows Rows, IReadOnlyDictionary<int, SpaceKind> Kinds,
        Dictionary<int, int[]> ChildOrders, Dictionary<int, int[]> MapOrders, ExplorerPresentation Entry, ExplorerRow[] Selection);

    private ExplorerRow[] SelectedTreeRows() => SpaceTree.SelectedItems.OfType<ExplorerRow>().ToArray();

    private string[] SelectedTreePaths() => SpaceTree.SelectedItems.OfType<ExplorerRow>()
        .Select(row => row.Scan.EntryPath(row.Entry.LinkIndex)).OfType<string>().ToArray();

    private async Task<PreparedExplorer> PrepareExplorerRefreshAsync(ScanSnapshot scan,
        IReadOnlyDictionary<int, SpaceKind> kinds, string[] expanded, string selected, string[] selectedPaths,
        ScanSortColumn column, bool descending, bool preferLogicalSize, CancellationToken token, string? mapPath = null)
    {
        await _classificationReady.WaitAsync(token);
        return await Task.Run(() =>
        {
            var rows = new ExplorerRows(scan) { PreferLogicalSize = preferLogicalSize };
            var childOrders = new Dictionary<int, int[]>();
            var mapOrders = new Dictionary<int, int[]>();
            // Build the compact visible index before attaching it to WPF. None
            // of these expansion notifications can reset the current list.
            foreach (string path in expanded.OrderBy(path => path.Length))
            {
                token.ThrowIfCancellationRequested();
                if (scan.FindEntry(path) is not { } link || scan.Entry(link) is not { IsDirectory: true, IsPrimaryName: true } entry
                    || scan.IsUnmeasuredDirectory(entry.FileIndex)) continue;
                rows.Expand(link, PreparedChildOrder(scan, entry.FileIndex, childOrders, column, descending, rows.UseAllocation, token));
            }
            while (scan.FindEntry(selected) is null
                && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(selected)) is { } parent) selected = parent;
            int selection = scan.FindEntry(selected) ?? -1;
            var selectedRows = rows.RowsForLinks(selectedPaths.Select(path => scan.FindEntry(path) is { } link
                && scan.EntryPath(link) == path ? link : (int?)null).OfType<int>().ToArray());
            if (selectedRows.Length > 0 && !selectedRows.Any(row => row.Entry.LinkIndex == selection))
                selection = selectedRows[0].Entry.LinkIndex;
            else if (selectedRows.Length == 0 && selectedPaths.Length == 1)
                selectedRows = rows.RowsForLinks([selection]);
            int? mapFolder = mapPath is null ? null : scan.FindEntry(mapPath);
            var presentation = PrepareEntry(scan, rows, selection, kinds, childOrders, mapOrders, column, descending, token, mapFolder);
            token.ThrowIfCancellationRequested();
            return new PreparedExplorer(rows, kinds, childOrders, mapOrders, presentation,
                selectedRows.OrderByDescending(row => row.Entry.LinkIndex == selection).ToArray());
        }, token);
    }

    private void PublishExplorerRefresh(PreparedExplorer prepared, ScanSortColumn column, bool descending, bool grouped)
    {
        ClearExplorerMenus();
        _snapshot = prepared.Rows.Scan;
        _explorer = prepared.Rows;
        _kinds = prepared.Kinds;
        PublishExplorerOrders(prepared.ChildOrders, prepared.MapOrders);
        _treeSortColumn = column; _treeDescending = descending;
        _selectedLink = prepared.Entry.Selected.LinkIndex;
        _selectionIsGroup = grouped;
        _syncTree = true;
        try
        {
            SpaceTree.ItemsSource = prepared.Rows;
            // Restore selection without scrolling it into view. The user's
            // viewport can be elsewhere in a large expanded branch.
            SpaceTree.RestoreSelection(prepared.Selection);
            TreeHint.Visibility = Visibility.Collapsed;
            PublishEntry(prepared.Entry);
        }
        finally { _syncTree = false; }
        UpdateTreeSortHeaders();
        ClearPreliminary();
    }

    internal async Task SetExplorerSizeBasisAsync(bool preferLogicalSize)
    {
        if (preferLogicalSize != _preferLogicalSize)
        {
            await BrowseAsync(async token =>
            {
                var scan = _snapshot!;
                var column = _treeSortColumn == ScanSortColumn.Logical && TreeColumns.Columns.Contains(AllocationColumn)
                    ? ScanSortColumn.Allocated : _treeSortColumn;
                bool descending = _treeDescending, grouped = _selectionIsGroup;
                // Context selection can change the highlighted row without
                // changing the folder shown by the map and breadcrumb.
                var prepared = await PrepareExplorerRefreshAsync(scan, _kinds,
                    _explorer!.ExpandedPaths(), scan.EntryPath(_selectedLink) ?? scan.RootPath, SelectedTreePaths(),
                    column, descending, preferLogicalSize, token, _currentPath);
                token.ThrowIfCancellationRequested();
                if (_closing || !ReferenceEquals(_snapshot, scan)) return;
                var viewport = CaptureTreeViewport();
                _preferLogicalSize = preferLogicalSize;
                PublishExplorerRefresh(prepared, column, descending, grouped);
                UpdateExplorerSizeLabels();
                RestoreTreeViewport(viewport);
            });
        }
        SyncSizeBasisSelector();
    }

    private async void SizeBasis_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingSizeBasis && _explorer is not null && SizeBasisSelector.SelectedIndex >= 0)
        {
            bool hadFocus = SizeBasisSelector.IsKeyboardFocusWithin;
            await SetExplorerSizeBasisAsync(SizeBasisSelector.SelectedIndex == 1);
            if (hadFocus && !_closing && SizeBasisSelector.IsVisible && SizeBasisSelector.IsEnabled)
                SizeBasisSelector.Focus();
        }
    }

    private void UpdateExplorerSizeLabels()
    {
        if (_explorer is not { } rows) return;
        var scan = rows.Scan;
        bool allocated = rows.UseAllocation;
        string basis = allocated ? "size on disk" : "file size";
        ScannedSizeText.Text = SizeText.Format(allocated ? scan.AllocatedBytes : scan.LogicalBytes);
        string caption = allocated ? "On disk" : scan.HasAllocationData ? "File size" : "File size · Size on disk unavailable";
        ScannedSizeCaption.Text = caption + (_scanStale ? " · Out of date" : scan.LastRecheckUtc is not null ? " · Partially refreshed" : "");
        MapBasis.Text = _scanStale ? $"Previous scan · {basis}" : $"Area = {basis}"
            + (scan.LastRecheckUtc is not null ? " · Partial refresh" : "");
        SizeBasisSelector.ToolTip = !scan.HasAllocationData ? "Allocation is unavailable for this scan. Showing logical sizes."
            : "Choose the size used by the tree, map, and scanned total.";
        SyncSizeBasisSelector();
    }

    private void SyncSizeBasisSelector()
    {
        if (_explorer is not { } rows) return;
        _updatingSizeBasis = true;
        try
        {
            SizeBasisSelector.IsEnabled = rows.Scan.HasAllocationData;
            SizeBasisSelector.SelectedIndex = rows.UseAllocation ? 0 : 1;
        }
        finally { _updatingSizeBasis = false; }
    }

    private void PublishExplorerOrders(Dictionary<int, int[]> childOrders, Dictionary<int, int[]> mapOrders)
    {
        _childOrders.Clear(); _mapOrders.Clear();
        foreach (var order in childOrders) _childOrders.Add(order.Key, order.Value);
        foreach (var order in mapOrders) _mapOrders.Add(order.Key, order.Value);
    }

    private sealed record TreeViewport(double Horizontal, double Vertical, string[] Anchors);

    private TreeViewport? CaptureTreeViewport(bool includeVisibleNeighbors = false)
    {
        if (TreeScrollViewer(SpaceTree) is not { } scroll) return null;
        string[] anchors = [];
        if (_explorer is { Count: > 0 } rows && scroll.CanContentScroll
            && VirtualizingPanel.GetScrollUnit(SpaceTree) == ScrollUnit.Item)
        {
            int top = Math.Clamp((int)scroll.VerticalOffset, 0, rows.Count - 1);
            int visible = includeVisibleNeighbors ? Math.Max(1, (int)Math.Ceiling(scroll.ViewportHeight)) : 1;
            anchors = Enumerable.Range(top, Math.Min(visible, rows.Count - top))
                .Select(index => rows.Scan.EntryPath(rows.Row(index).Entry.LinkIndex)).OfType<string>().ToArray();
        }
        return new(scroll.HorizontalOffset, scroll.VerticalOffset, anchors);
    }

    private void RestoreTreeViewport(TreeViewport? viewport)
    {
        if (viewport is null) return;
        // Realize only the new viewport, then apply offsets before returning to
        // the dispatcher; no blank or root-only frame is rendered in between.
        SpaceTree.UpdateLayout();
        if (TreeScrollViewer(SpaceTree) is not { } scroll) return;
        double vertical = viewport.Vertical;
        foreach (string path in viewport.Anchors)
        {
            int index = VisibleTreeIndex(path);
            if (index < 0) continue;
            vertical = index + (viewport.Vertical - Math.Floor(viewport.Vertical));
            break;
        }
        scroll.ScrollToHorizontalOffset(viewport.Horizontal);
        scroll.ScrollToVerticalOffset(vertical);
        SpaceTree.UpdateLayout();
    }

    private int VisibleTreeIndex(string? path) => path is not null && _explorer is { } rows
        && rows.Scan.FindEntry(path) is { } link ? rows.Find(link) : -1;

    private sealed record ExplorerFocus(string? Path, bool MapTile = false);

    private ExplorerFocus? CaptureExplorerFocus()
    {
        if (Keyboard.FocusedElement is not FrameworkElement focused) return null;
        if (ItemsControl.ContainerFromElement(SpaceTree, focused) is ListBoxItem { DataContext: ExplorerRow row })
            return new(row.Scan.EntryPath(row.Entry.LinkIndex));
        if (SpaceMap.IsKeyboardFocusWithin && focused is Button { DataContext: SpaceContribution tile })
            return new(tile.Link >= 0 ? tile.Path : null, MapTile: true);
        return null;
    }

    private void RestoreExplorerFocus(ExplorerFocus? focus)
    {
        if (focus is null || !IsActive || ExploreTab.IsChecked != true) return;
        if (focus is { MapTile: true, Path: { } mapPath })
        {
            var tile = SpaceMap.Buttons.FirstOrDefault(button => button.DataContext is SpaceContribution { Link: >= 0 } item
                && item.Path == mapPath);
            if (tile?.Focus() == true) return;
        }

        int index = focus.MapTile ? -1 : VisibleTreeIndex(focus.Path);
        if (index < 0) index = SpaceTree.SelectedIndex;
        FrameworkElement target = SpaceTree;
        if (index >= 0 && TreeScrollViewer(SpaceTree) is { } scroll && index >= (int)scroll.VerticalOffset
            && index < scroll.VerticalOffset + scroll.ViewportHeight
            && SpaceTree.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem itemContainer)
            target = itemContainer;

        // Focus can request that WPF scroll the whole row into view, including
        // its columns. Keep the restored viewport and the current selection.
        var selected = SelectedTreeRows();
        _syncTree = true;
        target.RequestBringIntoView += KeepViewport;
        try
        {
            target.Focus();
            if (!SpaceTree.SelectedItems.Cast<ExplorerRow>().SequenceEqual(selected)) SpaceTree.RestoreSelection(selected);
        }
        finally
        {
            target.RequestBringIntoView -= KeepViewport;
            _syncTree = false;
        }

        static void KeepViewport(object sender, RequestBringIntoViewEventArgs e) => e.Handled = true;
    }

    private static ScrollViewer? TreeScrollViewer(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroll) return scroll;
            if (TreeScrollViewer(child) is { } descendant) return descendant;
        }
        return null;
    }

    private async Task<int[]> ChildOrderAsync(int directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_childOrders.TryGetValue(directory, out var cached)) return cached;
        var scan = _snapshot!;
        var column = _treeSortColumn;
        bool descending = _treeDescending;
        bool useAllocation = UseExplorerAllocation;
        var children = await Task.Run(() => SortExplorerChildren(scan, directory, column, descending, useAllocation, token), token);
        token.ThrowIfCancellationRequested();
        _childOrders[directory] = children;
        return children;
    }

    private async Task BrowseAsync(Func<CancellationToken, Task> action)
    {
        if (_snapshot is null || _scanning || _browsing || _closing) return;
        _browsing = true;
        var idle = _browseIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerRevision++;
        using var work = new CancellationTokenSource();
        _browseWork = work;
        UpdatePathAvailability(_work is not null);
        if (_work is null) StatusText.Text = "Opening folder…";
        bool failed = false;
        try { await action(work.Token); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException ex) { failed = true; StatusText.Text = $"Could not open the branch: {ex.Message}"; }
        finally
        {
            _browseWork = null; _browsing = false;
            _browseIdle = null;
            idle.TrySetResult();
            SignalRecycleStateChange();
            UpdatePathAvailability(_work is not null);
            if (!_closing)
            {
                if (!failed && _work is null && _snapshot is not null) StatusText.Text = _scanStatus;
                DrainExplorerRecheck();
            }
        }
    }

    private void SelectTree(int link)
    {
        if (_explorer is null) return;
        _explorerRevision++;
        _selectedLink = link;
        _selectionIsGroup = false;
        _syncTree = true;
        try
        {
            SpaceTree.RestoreSelection(_explorer.RowsForLinks([link]));
            if (SpaceTree.SelectedItem is { } row) SpaceTree.ScrollIntoView(row);
        }
        finally { _syncTree = false; }
    }

    internal Task NavigateToEntryAsync(int link) => BrowseAsync(async token =>
    {
        var scan = _snapshot!;
        var ancestors = AncestorLinks(scan, link);
        while (ancestors.TryPop(out int ancestor))
        {
            var entry = scan.Entry(ancestor);
            if (!_explorer!.IsExpanded(ancestor)) _explorer.Expand(ancestor, await ChildOrderAsync(entry.FileIndex, token));
        }
        SelectTree(link);
        await DisplayEntryAsync(link, token);
    });

    private static Stack<int> AncestorLinks(ScanSnapshot scan, int link)
    {
        var ancestors = new Stack<int>();
        var visited = new HashSet<int>();
        int parent = scan.Entry(link).ParentIndex;
        while (parent >= 0 && visited.Add(parent))
        {
            int ancestor = scan.PrimaryLink(parent);
            ancestors.Push(ancestor);
            parent = scan.Entry(ancestor).ParentIndex;
        }
        return ancestors;
    }

    private async Task ToggleFolderAsync(ExplorerRow row, CancellationToken token)
    {
        if (!row.CanExpand || !ReferenceEquals(row.Scan, _snapshot)) return;
        var links = SelectedTreeRows().Select(item => item.Entry.LinkIndex).ToArray();
        int current = _selectedLink;
        var viewport = links.Length > 1 ? CaptureTreeViewport() : null;
        _syncTree = true;
        try
        {
            if (_explorer!.IsExpanded(row.Entry.LinkIndex)) _explorer.Collapse(row.Entry.LinkIndex);
            else _explorer.Expand(row.Entry.LinkIndex, await ChildOrderAsync(row.Entry.FileIndex, token));
            var selected = _explorer.RowsForLinks(links);
            if (selected.Length == 0) selected = _explorer.RowsForLinks([row.Entry.LinkIndex]);
            SpaceTree.RestoreSelection(selected);
            _selectedLink = selected.Any(item => item.Entry.LinkIndex == current) ? current : selected[0].Entry.LinkIndex;
            _selectionIsGroup = false;
            if (viewport is not null) RestoreTreeViewport(viewport);
            else if (SpaceTree.SelectedItem is { } item) SpaceTree.ScrollIntoView(item);
        }
        finally { _syncTree = false; }
        await DisplayEntryAsync(_selectedLink, token);
    }

    private async void Expand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExplorerRow row)
            await BrowseAsync(token => ToggleFolderAsync(row, token));
        e.Handled = true;
    }
    private async void Tree_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncTree) return;
        UpdateExplorerActions();
        _explorerRevision++;
        if (SpaceTree.SelectedItem is not ExplorerRow row || (!_selectionIsGroup && _selectedLink == row.Entry.LinkIndex)) return;
        _selectedLink = row.Entry.LinkIndex;
        _selectionIsGroup = false;
        await BrowseAsync(token => DisplayEntryAsync(_selectedLink, token));
    }
    private void Tree_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Re-selecting the same real row resolves a preceding grouped map
        // selection even when WPF does not raise SelectionChanged again.
        if (ItemsControl.ContainerFromElement(SpaceTree, e.OriginalSource as DependencyObject) is ListBoxItem item)
        {
            _selectionIsGroup = false;
            UpdateExplorerActions();
            if (e.ChangedButton == MouseButton.Right && item.DataContext is ExplorerRow row && CanUseFolderMenu(row.Scan))
            {
                // Avoid ListBox selection triggering async navigation before menu opening.
                e.Handled = SelectContextEntry(row.Scan, row.Entry.LinkIndex, preserveSelection: item.IsSelected);
            }
        }
    }
    private async void Tree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SpaceTree, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: ExplorerRow row } && row.CanExpand)
            await BrowseAsync(token => ToggleFolderAsync(row, token));
    }
    private async void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        // Headers now live inside the list. Their keys must not navigate a selected folder.
        var focusedRow = ItemsControl.ContainerFromElement(SpaceTree, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (!ReferenceEquals(e.OriginalSource, SpaceTree) && focusedRow is null) return;
        var row = focusedRow?.DataContext as ExplorerRow ?? SpaceTree.SelectedItem as ExplorerRow;
        if (row is null || _explorer is null) return;
        bool hadFocus = SpaceTree.IsKeyboardFocusWithin;
        int destination = row.Entry.LinkIndex;
        e.Handled = true;
        if (e.Key == Key.Right)
        {
            if (row.CanExpand && !row.Expanded) await BrowseAsync(token => ToggleFolderAsync(row, token));
            else if (row.Expanded && _explorer.Find(row.Entry.LinkIndex) is var index && index >= 0 && index + 1 < _explorer.Count
                && _explorer.Row(index + 1) is { } child && child.Depth == row.Depth + 1)
            {
                destination = child.Entry.LinkIndex;
                await NavigateToEntryAsync(destination);
            }
            else return;
        }
        else if (row.Expanded) await BrowseAsync(token => ToggleFolderAsync(row, token));
        else if (row.Entry.ParentIndex >= 0)
        {
            destination = row.Scan.PrimaryLink(row.Entry.ParentIndex);
            await NavigateToEntryAsync(destination);
        }
        else return;

        if (hadFocus && !_closing && _work is null && ReferenceEquals(_snapshot, row.Scan))
        {
            SpaceTree.UpdateLayout();
            RestoreExplorerFocus(new(row.Scan.EntryPath(destination)));
        }
    }

    private async Task DisplayEntryAsync(int link, CancellationToken token)
    {
        var scan = _snapshot!;
        var explorer = _explorer!;
        var kinds = _kinds;
        var column = _treeSortColumn; bool descending = _treeDescending;
        var childOrders = new Dictionary<int, int[]>(_childOrders);
        var mapOrders = new Dictionary<int, int[]>(_mapOrders);
        var presentation = await Task.Run(() => PrepareEntry(scan, explorer, link, kinds,
            childOrders, mapOrders, column, descending, token), token);
        token.ThrowIfCancellationRequested();
        if (_closing || !ReferenceEquals(_snapshot, scan)) return;
        PublishExplorerOrders(childOrders, mapOrders);
        PublishEntry(presentation);
    }

    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (ExplorePanel is null || OpportunitiesPanel is null || PreliminaryPanel is null || PhotosPanel is null || AppsPanel is null) return;
        ExplorePanel.Visibility = ExploreTab.IsChecked == true && _preliminary is null ? Visibility.Visible : Visibility.Collapsed;
        PreliminaryPanel.Visibility = ExploreTab.IsChecked == true && _preliminary is not null ? Visibility.Visible : Visibility.Collapsed;
        OpportunitiesPanel.Visibility = OpportunitiesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PhotosPanel.Visibility = PhotosTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateAppsDrive();
        AppsPanel.Visibility = AppsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateExtensionWorkspace();
        UpdateExplorerActions();
    }
}
