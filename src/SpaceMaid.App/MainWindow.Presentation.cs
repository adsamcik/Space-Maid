using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
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
}
