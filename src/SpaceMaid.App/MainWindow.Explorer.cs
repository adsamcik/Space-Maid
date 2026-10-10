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
    private IReadOnlyDictionary<int, SpaceKind> _kinds = new Dictionary<int, SpaceKind>();
    private CancellationTokenSource? _browseWork;
    private bool _browsing, _syncTree;
    private int _selectedLink = -1;
    private string _scanStatus = "Ready · Your files stay untouched";
    private TimeSpan _detectionElapsed, _presentationElapsed;

    private void ClearExplorer()
    {
        _browseWork?.Cancel();
        _explorer = null;
        _childOrders.Clear(); _kinds = new Dictionary<int, SpaceKind>();
        SpaceTree.ItemsSource = null;
        Breadcrumbs.Children.Clear(); SpaceMap.Children.Clear(); SpaceMap.Weights = [];
        EntryDetail.DataContext = null; EntryDetail.Visibility = Visibility.Collapsed;
        MapHint.Visibility = TreeHint.Visibility = Visibility.Visible;
        MapHint.Text = "Choose a folder to see its largest contributors.";
        TreeHint.Text = "Scan a drive to explore its files.";
        MapTotal.Text = MapCount.Text = "";
    }

    internal async Task InitializeExplorerAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> opportunities, CancellationToken token)
    {
        ClearExplorer();
        _snapshot = scan;
        _explorer = new(scan);
        var kinds = new Dictionary<int, SpaceKind>();
        foreach (var opportunity in opportunities)
        {
            var kind = opportunity.Category switch
            {
                "Agent files" => SpaceKind.Agent,
                "Git worktrees" => SpaceKind.Project,
                _ => SpaceKind.Cache
            };
            kinds[opportunity.DirectoryIndex] = kind;
        }
        _kinds = kinds;
        SpaceTree.ItemsSource = _explorer;
        TreeHint.Visibility = Visibility.Collapsed;
        TreeNote.Text = scan.HasAllocationData ? "Observed totals · Shared file allocation counted once"
            : "Logical sizes only · Physical allocation is unavailable";
        MapBasis.Text = scan.HasAllocationData ? "Area = allocated size" : "Area = logical size";
        AllocationHeader.Text = scan.HasAllocationData ? "Allocated ↓" : "Allocated";
        LogicalHeader.Text = scan.HasAllocationData ? "Logical" : "Logical ↓";
        var children = await ChildOrderAsync(scan.RootIndex, token);
        _explorer.Expand(-1, children);
        SelectTree(-1);
        await DisplayEntryAsync(-1, token);
    }

    private async Task<int[]> ChildOrderAsync(int directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_childOrders.TryGetValue(directory, out var cached)) return cached;
        var scan = _snapshot!;
        var children = await Task.Run(() => scan.SortedChildren(directory, token), token);
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
        StatusText.Text = "Opening folder…";
        bool failed = false;
        try { await action(work.Token); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException ex) { failed = true; StatusText.Text = $"Could not open the branch: {ex.Message}"; }
        finally
        {
            _browseWork = null; _browsing = false;
            if (!_closing)
            {
                ExplorePanel.IsEnabled = _work is null;
                if (!failed && _work is null && _snapshot is not null) StatusText.Text = _scanStatus;
            }
        }
    }

    private void SelectTree(int link)
    {
        if (_explorer is null) return;
        _selectedLink = link;
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
        var target = scan.Entry(link);
        var ancestors = new Stack<int>();
        var visited = new HashSet<int>();
        int parent = target.ParentIndex;
        while (parent >= 0 && visited.Add(parent))
        {
            int ancestor = scan.PrimaryLink(parent);
            ancestors.Push(ancestor);
            parent = scan.Entry(ancestor).ParentIndex;
        }
        while (ancestors.TryPop(out int ancestor))
        {
            var entry = scan.Entry(ancestor);
            if (!_explorer!.IsExpanded(ancestor)) _explorer.Expand(ancestor, await ChildOrderAsync(entry.FileIndex, token));
        }
        SelectTree(link);
        await DisplayEntryAsync(link, token);
    });

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
        await BrowseAsync(token => DisplayEntryAsync(_selectedLink, token));
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
        var selected = scan.Entry(link);
        var row = new ExplorerRow(scan, selected, 0, _explorer!.IsExpanded(link));
        EntryDetail.DataContext = row; EntryDetail.Visibility = Visibility.Visible;
        var folder = selected.IsDirectory && selected.IsPrimaryName ? selected : scan.Entry(scan.PrimaryLink(selected.ParentIndex));
        var children = await ChildOrderAsync(folder.FileIndex, token);
        token.ThrowIfCancellationRequested();
        var kinds = _kinds;
        var contributions = await Task.Run(() => TreemapLayout.Contributions(scan, folder.FileIndex, children, kinds), token);
        token.ThrowIfCancellationRequested();
        Breadcrumbs.Children.Clear();
        var crumbs = new Stack<ScanEntry>(); var visited = new HashSet<int>();
        for (var current = folder; visited.Add(current.FileIndex); current = scan.Entry(scan.PrimaryLink(current.ParentIndex)))
        { crumbs.Push(current); if (current.ParentIndex < 0) break; }
        while (crumbs.TryPop(out var crumb))
        {
            if (Breadcrumbs.Children.Count > 0) Breadcrumbs.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Margin = new(3, 0, 3, 0), Opacity = .65 });
            int destination = crumb.LinkIndex;
            string label = crumb.LinkIndex == -1 && scan.RootPath != Path.GetPathRoot(scan.RootPath)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(scan.RootPath)) : crumb.Name;
            var button = new Button { Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 }, Padding = new(4, 2, 4, 2), MinHeight = 24, Background = Brushes.Transparent, BorderThickness = new(0), FontSize = 12, ToolTip = scan.EntryPath(destination) };
            button.Click += async (_, _) => await NavigateToEntryAsync(destination);
            Breadcrumbs.Children.Add(button);
        }
        MapCount.Text = $"{children.Length:N0} immediate entries";
        MapTotal.Text = SizeText.Format(folder.AttributedBytes(scan.HasAllocationData)) + (scan.HasAllocationData ? " allocated" : " logical");
        SpaceMap.Children.Clear();
        SpaceMap.Weights = contributions.Select(item => item.Bytes).ToArray();
        foreach (var contribution in contributions)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = contribution.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = SizeText.Format(contribution.Bytes), Margin = new(0, 4, 0, 0), FontSize = 12 });
            long total = folder.AttributedBytes(scan.HasAllocationData);
            text.Children.Add(new TextBlock { Text = total > 0 ? $"{100d * contribution.Bytes / total:0.#}%" : "—", Margin = new(0, 3, 0, 0), FontSize = 12, Opacity = .7 });
            var button = new Button { Content = text, Tag = text, DataContext = contribution, Style = (Style)FindResource("TreemapTile"), ToolTip = $"{contribution.Path}\n{SizeText.Format(contribution.Bytes)} · {(total > 0 ? 100d * contribution.Bytes / total : 0):0.#}% of folder" };
            AutomationProperties.SetName(button, $"{contribution.Name}, {SizeText.Format(contribution.Bytes)}. {contribution.Path}");
            button.Click += async (_, _) =>
            {
                if (contribution.Link >= 0) await NavigateToEntryAsync(contribution.Link);
                else await BrowseAsync(async cancellation =>
                {
                    if (!_explorer!.IsExpanded(folder.LinkIndex)) _explorer.Expand(folder.LinkIndex, await ChildOrderAsync(folder.FileIndex, cancellation));
                    SelectTree(folder.LinkIndex); SpaceTree.Focus();
                    await DisplayEntryAsync(folder.LinkIndex, cancellation);
                });
            };
            SpaceMap.Children.Add(button);
        }
        SpaceMap.InvalidateMeasure();
        MapHint.Text = scan.HasAllocationData ? "No observed allocation in this folder." : "No observed logical bytes in this folder.";
        MapHint.Visibility = contributions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMapColors();
    }

    private void UpdateMapColors()
    {
        if (SpaceMap is null) return;
        bool dark = Background is SolidColorBrush brush && brush.Color.R + brush.Color.G + brush.Color.B < 384;
        Brush ColorFor(SpaceKind kind)
        {
            if (SystemParameters.HighContrast) return SystemColors.ControlBrush;
            string color = (kind, dark) switch
            {
                (SpaceKind.Project, false) => "#DCEBF4", (SpaceKind.Project, true) => "#2F4655",
                (SpaceKind.Cache, false) => "#F3E8CB", (SpaceKind.Cache, true) => "#554932",
                (SpaceKind.Agent, false) => "#EADDF2", (SpaceKind.Agent, true) => "#4A3A54",
                (_, false) => "#E8EAED", _ => "#3E4147"
            };
            var result = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); result.Freeze(); return result;
        }
        foreach (Button button in SpaceMap.Children)
            if (button.DataContext is SpaceContribution item) { button.Background = ColorFor(item.Kind); button.Foreground = Foreground; }
        ProjectSwatch.Background = ColorFor(SpaceKind.Project); CacheSwatch.Background = ColorFor(SpaceKind.Cache);
        AgentSwatch.Background = ColorFor(SpaceKind.Agent); OtherSwatch.Background = ColorFor(SpaceKind.Other);
    }
    private void MapGeometry_Changed(object sender, SizeChangedEventArgs e)
    {
        if (MapScroller is null || MapLayout is null || SpaceMap is null || MapScroller.ActualHeight <= 0) return;
        double other = MapLayout.RowDefinitions.Where((_, index) => index != 3).Sum(row => row.ActualHeight);
        double height = Math.Max(120, MapScroller.ActualHeight - other);
        if (Math.Abs(SpaceMap.Height - height) > .5) SpaceMap.Height = height;
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BackgroundProperty || e.Property == ForegroundProperty) UpdateMapColors();
    }
    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (ExplorePanel is null || OpportunitiesPanel is null) return;
        ExplorePanel.Visibility = ExploreTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OpportunitiesPanel.Visibility = ExploreTab.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void ShowInTree_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || OpportunityGrid.SelectedItem is not OpportunityRow row) return;
        ExploreTab.IsChecked = true;
        await NavigateToEntryAsync(_snapshot.PrimaryLink(row.Item.DirectoryIndex));
    }
}
