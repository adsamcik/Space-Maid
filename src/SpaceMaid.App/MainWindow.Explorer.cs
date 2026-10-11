using System.IO;
using System.Windows;
using System.Windows.Automation;
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
    private bool _browsing, _syncTree, _selectionIsGroup;
    private int _selectedLink = -1;
    private string _scanStatus = "Ready · Scan a drive to explore space";
    private TimeSpan _detectionElapsed, _presentationElapsed;

    private void ClearExplorer()
    {
        _browseWork?.Cancel();
        // Menus capture a scan for command safety; discard their callbacks
        // when clearing results so a prior large inventory is not retained.
        SpaceTree.ContextMenu.IsOpen = false;
        SpaceTree.ContextMenu.Items.Clear();
        ContextMenuService.SetPlacementTarget(SpaceTree, null);
        ClearMap();
        _explorer = null;
        _selectedLink = -1;
        _selectionIsGroup = false;
        _childOrders.Clear(); _mapOrders.Clear(); _kinds = new Dictionary<int, SpaceKind>();
        SpaceTree.ItemsSource = null;
        EntryDetail.DataContext = null; EntryDetail.Visibility = Visibility.Collapsed;
        MapHint.Visibility = TreeHint.Visibility = Visibility.Visible;
        MapHint.Text = "Select a folder.";
        TreeHint.Text = "Scan a folder to explore.";
    }

    internal async Task InitializeExplorerAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> opportunities, CancellationToken token)
    {
        await _classificationReady.WaitAsync(token);
        ClearExplorer();
        _snapshot = scan;
        _scanStale = false;
        _staleScopes.Clear();
        _explorer = new(scan);
        _kinds = OpportunityKinds(opportunities);
        SpaceTree.ItemsSource = _explorer;
        TreeHint.Visibility = Visibility.Collapsed;
        TreeNote.Text = scan.HasAllocationData ? "Shared files counted once"
            : "Logical sizes only · Physical allocation is unavailable";
        MapBasis.Text = scan.HasAllocationData ? "Area = allocated size" : "Area = logical size";
        _treeSortColumn = scan.HasAllocationData ? ScanSortColumn.Allocated : ScanSortColumn.Logical;
        _treeDescending = true;
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
            var kind = opportunity.Category switch
            {
                "Agent files" => SpaceKind.Agent,
                "Git worktrees" => SpaceKind.Project,
                "Test artifacts" or "Editor workspace data" => SpaceKind.Other,
                _ => SpaceKind.Cache
            };
            kinds[opportunity.DirectoryIndex] = kind;
        }
        return kinds;
    }

    private sealed record PreparedExplorer(ExplorerRows Rows, Dictionary<int, SpaceKind> Kinds,
        Dictionary<int, int[]> ChildOrders, Dictionary<int, int[]> MapOrders, ExplorerPresentation Entry);

    private async Task<PreparedExplorer> PrepareExplorerRefreshAsync(ScanSnapshot scan,
        IReadOnlyList<Opportunity> opportunities, string[] expanded, string selected,
        ScanSortColumn column, bool descending, CancellationToken token)
    {
        await _classificationReady.WaitAsync(token);
        return await Task.Run(() =>
        {
            var rows = new ExplorerRows(scan);
            var kinds = OpportunityKinds(opportunities);
            var childOrders = new Dictionary<int, int[]>();
            var mapOrders = new Dictionary<int, int[]>();
            // Build the compact visible index before attaching it to WPF. None
            // of these expansion notifications can reset the current list.
            foreach (string path in expanded.OrderBy(path => path.Length))
            {
                token.ThrowIfCancellationRequested();
                if (scan.FindEntry(path) is not { } link || scan.Entry(link) is not { IsDirectory: true, IsPrimaryName: true } entry
                    || scan.IsUnmeasuredDirectory(entry.FileIndex)) continue;
                rows.Expand(link, PreparedChildOrder(scan, entry.FileIndex, childOrders, column, descending, token));
            }
            while (scan.FindEntry(selected) is null
                && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(selected)) is { } parent) selected = parent;
            int selection = scan.FindEntry(selected) ?? -1;
            var presentation = PrepareEntry(scan, rows, selection, kinds, childOrders, mapOrders, column, descending, token);
            token.ThrowIfCancellationRequested();
            return new PreparedExplorer(rows, kinds, childOrders, mapOrders, presentation);
        }, token);
    }

    private void PublishExplorerRefresh(PreparedExplorer prepared, ScanSortColumn column, bool descending, bool grouped)
    {
        SpaceTree.ContextMenu.IsOpen = false;
        SpaceTree.ContextMenu.Items.Clear();
        ContextMenuService.SetPlacementTarget(SpaceTree, null);
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
            SpaceTree.SelectedIndex = prepared.Rows.Find(_selectedLink);
            TreeHint.Visibility = Visibility.Collapsed;
            PublishEntry(prepared.Entry);
        }
        finally { _syncTree = false; }
        UpdateTreeSortHeaders();
        ClearPreliminary();
    }

    private void PublishExplorerOrders(Dictionary<int, int[]> childOrders, Dictionary<int, int[]> mapOrders)
    {
        _childOrders.Clear(); _mapOrders.Clear();
        foreach (var order in childOrders) _childOrders.Add(order.Key, order.Value);
        foreach (var order in mapOrders) _mapOrders.Add(order.Key, order.Value);
    }

    private sealed record TreeViewport(double Horizontal, double Vertical, string? TopPath, double Fraction);

    private TreeViewport? CaptureTreeViewport()
    {
        if (TreeScrollViewer(SpaceTree) is not { } scroll) return null;
        string? topPath = null;
        double fraction = 0;
        if (_explorer is { Count: > 0 } rows && scroll.CanContentScroll
            && VirtualizingPanel.GetScrollUnit(SpaceTree) == ScrollUnit.Item)
        {
            int top = Math.Clamp((int)scroll.VerticalOffset, 0, rows.Count - 1);
            topPath = rows.Scan.EntryPath(rows.Row(top).Entry.LinkIndex);
            fraction = scroll.VerticalOffset - Math.Floor(scroll.VerticalOffset);
        }
        return new(scroll.HorizontalOffset, scroll.VerticalOffset, topPath, fraction);
    }

    private void RestoreTreeViewport(TreeViewport? viewport)
    {
        if (viewport is null) return;
        // Realize only the new viewport, then apply offsets before returning to
        // the dispatcher; no blank or root-only frame is rendered in between.
        SpaceTree.UpdateLayout();
        if (TreeScrollViewer(SpaceTree) is not { } scroll) return;
        double vertical = viewport.Vertical;
        if (viewport.TopPath is { } path && _explorer is { } rows
            && rows.Scan.FindEntry(path) is { } link && rows.Find(link) is var index && index >= 0)
            vertical = index + viewport.Fraction;
        scroll.ScrollToHorizontalOffset(viewport.Horizontal);
        scroll.ScrollToVerticalOffset(vertical);
        SpaceTree.UpdateLayout();
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
        var children = await Task.Run(() => scan.SortedChildren(directory, column, descending, token), token);
        token.ThrowIfCancellationRequested();
        _childOrders[directory] = children;
        return children;
    }

    private async Task BrowseAsync(Func<CancellationToken, Task> action)
    {
        if (_snapshot is null || _work is not null || _browsing) return;
        _browsing = true;
        using var work = new CancellationTokenSource();
        _browseWork = work;
        ExplorePanel.IsEnabled = false;
        UpdatePathAvailability(_work is not null);
        StatusText.Text = "Opening folder…";
        bool failed = false;
        try { await action(work.Token); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException ex) { failed = true; StatusText.Text = $"Could not open the branch: {ex.Message}"; }
        finally
        {
            _browseWork = null; _browsing = false;
            UpdatePathAvailability(_work is not null);
            if (!_closing)
            {
                ExplorePanel.IsEnabled = _work is null;
                if (!failed && _work is null && _snapshot is not null) StatusText.Text = _scanStatus;
                DrainExplorerRecheck();
            }
        }
    }

    private void SelectTree(int link)
    {
        if (_explorer is null) return;
        _selectedLink = link;
        _selectionIsGroup = false;
        _syncTree = true;
        try
        {
            SpaceTree.SelectedIndex = _explorer.Find(link);
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
        if (!row.CanExpand) return;
        int selected = _selectedLink;
        if (_explorer!.IsExpanded(row.Entry.LinkIndex)) _explorer.Collapse(row.Entry.LinkIndex);
        else _explorer.Expand(row.Entry.LinkIndex, await ChildOrderAsync(row.Entry.FileIndex, token));
        if (_explorer.Find(selected) < 0) selected = row.Entry.LinkIndex;
        SelectTree(selected);
        await DisplayEntryAsync(selected, token);
    }

    private async void Expand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExplorerRow row)
            await BrowseAsync(token => ToggleFolderAsync(row, token));
        e.Handled = true;
    }
    private async void Tree_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncTree || _work is not null || SpaceTree.SelectedItem is not ExplorerRow row) return;
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
            if (e.ChangedButton == MouseButton.Right && item.DataContext is ExplorerRow row && CanUseFolderMenu(row.Scan))
            {
                // Avoid ListBox selection triggering async navigation before menu opening.
                e.Handled = SelectContextEntry(row.Scan, row.Entry.LinkIndex);
            }
        }
    }
    private async void Tree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SpaceTree.SelectedItem is ExplorerRow row && row.CanExpand)
            await BrowseAsync(token => ToggleFolderAsync(row, token));
    }
    private async void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (SpaceTree.SelectedItem is not ExplorerRow row || _explorer is null) return;
        if (e.Key == Key.Right)
        {
            e.Handled = true;
            if (row.CanExpand && !row.Expanded) await BrowseAsync(token => ToggleFolderAsync(row, token));
            else if (row.Expanded && SpaceTree.SelectedIndex + 1 < _explorer.Count) SpaceTree.SelectedIndex++;
        }
        else if (e.Key == Key.Left)
        {
            e.Handled = true;
            if (row.Expanded) await BrowseAsync(token => ToggleFolderAsync(row, token));
            else if (row.Entry.ParentIndex >= 0) await NavigateToEntryAsync(row.Scan.PrimaryLink(row.Entry.ParentIndex));
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

    private sealed record ExplorerPresentation(ScanSnapshot Scan, ScanEntry Selected, bool Expanded,
        ScanEntry Folder, int ChildCount, IReadOnlyList<SpaceContribution> Contributions);

    private static int[] PreparedChildOrder(ScanSnapshot scan, int directory, Dictionary<int, int[]> orders,
        ScanSortColumn column, bool descending, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (orders.TryGetValue(directory, out var cached)) return cached;
        return orders[directory] = scan.SortedChildren(directory, column, descending, token);
    }

    private static ExplorerPresentation PrepareEntry(ScanSnapshot scan, ExplorerRows explorer, int link,
        IReadOnlyDictionary<int, SpaceKind> kinds, Dictionary<int, int[]> childOrders, Dictionary<int, int[]> mapOrders,
        ScanSortColumn column, bool descending, CancellationToken token)
    {
        var selected = scan.Entry(link);
        var folder = selected.IsDirectory && selected.IsPrimaryName ? selected : scan.Entry(scan.PrimaryLink(selected.ParentIndex));
        PreparedChildOrder(scan, folder.FileIndex, childOrders, column, descending, token);
        var children = MapOrder(folder.FileIndex);
        long total = folder.AttributedBytes(scan.HasAllocationData);
        var contributions = TreemapLayout.Contributions(scan, folder.FileIndex, children, kinds);
        if (contributions.Count > 32 && contributions.Any(part => TreemapLayout.CanNest(scan, part, total)))
            contributions = TreemapLayout.Contributions(scan, folder.FileIndex, children, kinds, maximum: 32);
        var nestedOrders = new Dictionary<int, int[]>();
        foreach (var part in contributions.Where(part => TreemapLayout.CanNest(scan, part, total)))
        {
            int directory = scan.Entry(part.Link).FileIndex;
            PreparedChildOrder(scan, directory, childOrders, column, descending, token);
            nestedOrders[directory] = MapOrder(directory);
        }
        contributions = TreemapLayout.Nest(scan, contributions, nestedOrders, kinds);
        token.ThrowIfCancellationRequested();
        return new(scan, selected, explorer.IsExpanded(link), folder, children.Length, contributions);

        int[] MapOrder(int directory)
        {
            token.ThrowIfCancellationRequested();
            if (mapOrders.TryGetValue(directory, out var cached)) return cached;
            return mapOrders[directory] = descending && (column == ScanSortColumn.Share
                || column == ScanSortColumn.Allocated && scan.HasAllocationData)
                && childOrders.TryGetValue(directory, out var treeOrder)
                ? treeOrder : scan.SortedChildren(directory, token);
        }
    }

    private void PublishEntry(ExplorerPresentation presentation)
    {
        var (scan, selected, expanded, folder, childCount, contributions) = presentation;
        long total = folder.AttributedBytes(scan.HasAllocationData);
        EntryDetail.DataContext = new ExplorerRow(scan, selected, 0, expanded);
        EntryDetail.Visibility = Visibility.Visible;
        ShowPath(scan.EntryPath(folder.LinkIndex)!);
        SelectedEntryPath.Visibility = scan.EntryPath(selected.LinkIndex) == _currentPath ? Visibility.Collapsed : Visibility.Visible;
        bool unmeasured = scan.IsUnmeasuredDirectory(folder.FileIndex);
        ClearMap();
        SpaceMap.Weights = contributions.Select(item => item.Bytes).ToArray();
        foreach (var contribution in contributions)
        {
            if (contribution.Children is not { Count: > 0 } nested)
            {
                SpaceMap.Children.Add(CreateMapButton(scan, contribution, folder, total));
                continue;
            }
            var header = CreateMapButton(scan, contribution, folder, total, isHeader: true);
            var nestedMap = new TreemapPanel { Weights = nested.Select(part => part.Bytes).ToArray(), ClipToBounds = true };
            nestedMap.Margin = new(2);
            nestedMap.SetBinding(Panel.BackgroundProperty, new System.Windows.Data.Binding(nameof(Panel.Background)) { Source = SpaceMap });
            var nestedFolder = scan.Entry(contribution.Link);
            foreach (var part in nested) nestedMap.Children.Add(CreateMapButton(scan, part, nestedFolder, contribution.Bytes));
            var grid = new Grid();
            grid.RowDefinitions.Add(new() { Height = new(44) });
            grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            grid.Children.Add(header);
            Grid.SetRow(nestedMap, 1);
            grid.Children.Add(nestedMap);
            var region = new Border { Child = grid, Tag = nestedMap, CornerRadius = new(6), BorderThickness = new(2), ClipToBounds = true };
            region.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Button.Background)) { Source = header });
            region.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Button.BorderBrush)) { Source = header });
            SpaceMap.Children.Add(region);
        }
        SpaceMap.InvalidateMeasure();
        MapHint.Text = unmeasured ? "Folder contents were not checked. Use Recheck this folder to measure them."
            : scan.HasAllocationData ? "No observed allocation in this folder." : "No observed logical bytes in this folder.";
        MapHint.Visibility = contributions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMapColors();
    }

    private Button CreateMapButton(ScanSnapshot scan, SpaceContribution contribution, ScanEntry folder, long total, bool isHeader = false)
    {
        string share = total > 0 ? $"{100d * contribution.Bytes / total:0.#}%" : "—";
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = contribution.Name + (isHeader ? " ›" : ""), FontWeight = contribution.Link == -2 ? FontWeights.Normal : FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = SizeText.Format(contribution.Bytes) + (isHeader ? $" · {share}" : ""), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, isHeader ? 2 : 4, 0, 0), FontSize = isHeader ? 11 : 12 });
        if (!isHeader) text.Children.Add(new TextBlock { Text = share, Margin = new(0, 3, 0, 0), FontSize = 12, Opacity = .7 });
        string type = contribution.Link >= 0 && !scan.Entry(contribution.Link).IsDirectory
            ? FileTypeClassifier.DescribeDetails(contribution.Name) : "";
        var button = new Button { Content = text, Tag = text, DataContext = contribution, Style = (Style)FindResource("TreemapTile"), Padding = isHeader ? new(8, 5, 8, 5) : new(10), ToolTip = $"{contribution.Path}\n{SizeText.Format(contribution.Bytes)} · {share} of parent" + (type.Length > 0 ? $"\n{type}" : "") };
        if (isHeader)
        {
            // Wide parent bands keep size on the right; compact bands stack it
            // below the name within the parent band.
            var title = new Grid();
            title.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            title.RowDefinitions.Add(new() { Height = GridLength.Auto });
            title.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var name = (TextBlock)text.Children[0];
            var facts = (TextBlock)text.Children[1];
            text.Children.Clear();
            name.VerticalAlignment = VerticalAlignment.Center;
            facts.FontSize = 12; facts.Opacity = .85;
            title.Children.Add(name); title.Children.Add(facts);
            button.Content = title; button.Tag = title;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.BorderThickness = new(0, 0, 0, 1);
        }
        AutomationProperties.SetName(button, $"{contribution.Name}, {SizeText.Format(contribution.Bytes)}, {share} of parent. {contribution.Path} {type}");
        if (contribution.Link >= 0 && scan.EntryPath(contribution.Link) is not null)
        {
            button.ContextMenu = new ContextMenu();
            button.ContextMenuOpening += (_, args) =>
            {
                if (!PrepareFolderMenu(button.ContextMenu, scan, contribution.Link)) args.Handled = true;
            };
        }
        button.Click += async (_, _) =>
        {
            if (contribution.Link >= 0)
            {
                await NavigateToEntryAsync(contribution.Link);
                SpaceTree.Focus();
            }
            else await BrowseAsync(async cancellation =>
            {
                var ancestors = AncestorLinks(scan, folder.LinkIndex);
                while (ancestors.TryPop(out int ancestor))
                    if (!_explorer!.IsExpanded(ancestor))
                        _explorer.Expand(ancestor, await ChildOrderAsync(scan.Entry(ancestor).FileIndex, cancellation));
                if (!_explorer!.IsExpanded(folder.LinkIndex)) _explorer.Expand(folder.LinkIndex, await ChildOrderAsync(folder.FileIndex, cancellation));
                SelectTree(folder.LinkIndex); SpaceTree.Focus();
                await DisplayEntryAsync(folder.LinkIndex, cancellation);
                _selectionIsGroup = true;
            });
        };
        return button;
    }

    private void ClearMap()
    {
        foreach (var button in SpaceMap.Buttons)
            if (button.ContextMenu is { } menu) { menu.IsOpen = false; menu.Items.Clear(); }
        SpaceMap.Children.Clear();
        SpaceMap.Weights = [];
    }

    private void UpdateMapColors()
    {
        if (SpaceMap is null) return;
        bool dark = Background is SolidColorBrush brush && brush.Color.R + brush.Color.G + brush.Color.B < 384;
        Brush ColorFor(SpaceKind kind, bool alternate = false)
        {
            if (SystemParameters.HighContrast) return SystemColors.ControlBrush;
            string color = (kind, dark) switch
            {
                (SpaceKind.Project, false) => "#DCEBF4", (SpaceKind.Project, true) => "#2F4655",
                (SpaceKind.Cache, false) => "#F3E8CB", (SpaceKind.Cache, true) => "#554932",
                (SpaceKind.Agent, false) => "#EADDF2", (SpaceKind.Agent, true) => "#4A3A54",
                (_, false) => "#DFE3E9", _ => "#373B42"
            };
            var value = (Color)ColorConverter.ConvertFromString(color);
            byte Tone(byte channel) => (byte)Math.Min(255, channel + (dark ? 18 : 12));
            if (alternate) value = Color.FromRgb(Tone(value.R), Tone(value.G), Tone(value.B));
            var result = new SolidColorBrush(value); result.Freeze(); return result;
        }
        Brush frame = SystemParameters.HighContrast ? SystemColors.ControlTextBrush
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#69717B" : "#A9AFB8"));
        Brush parent = SystemParameters.HighContrast ? SystemColors.ControlBrush
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#30353C" : "#DCE0E6"));
        SpaceMap.Background = SystemParameters.HighContrast ? SystemColors.ControlBrush
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#1E2023" : "#D2D5DA"));
        int sibling = 0;
        foreach (var button in SpaceMap.Buttons)
            if (button.DataContext is SpaceContribution item)
            {
                button.Background = item.Children is { Count: > 0 } || item.Link == -2 ? parent : ColorFor(item.Kind, sibling++ % 2 == 0);
                button.BorderBrush = frame; button.Foreground = Foreground;
            }
        ProjectSwatch.Background = ColorFor(SpaceKind.Project); CacheSwatch.Background = ColorFor(SpaceKind.Cache);
        AgentSwatch.Background = ColorFor(SpaceKind.Agent); OtherSwatch.Background = ColorFor(SpaceKind.Other);
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BackgroundProperty || e.Property == ForegroundProperty) UpdateMapColors();
    }
    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (ExplorePanel is null || OpportunitiesPanel is null || PreliminaryPanel is null || PhotosPanel is null) return;
        ExplorePanel.Visibility = ExploreTab.IsChecked == true && _preliminary is null ? Visibility.Visible : Visibility.Collapsed;
        PreliminaryPanel.Visibility = ExploreTab.IsChecked == true && _preliminary is not null ? Visibility.Visible : Visibility.Collapsed;
        OpportunitiesPanel.Visibility = OpportunitiesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PhotosPanel.Visibility = PhotosTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ShowInTree_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || OpportunityGrid.SelectedItem is not OpportunityRow row) return;
        ExploreTab.IsChecked = true;
        await NavigateToEntryAsync(_snapshot.PrimaryLink(row.Item.DirectoryIndex));
    }
}
