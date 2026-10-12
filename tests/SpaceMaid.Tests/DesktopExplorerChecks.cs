using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void AssertMapFillsPane(MainWindow window, TreemapPanel map)
    {
        var layout = (Grid)window.FindName("MapLayout");
        var legend = layout.Children.OfType<WrapPanel>().Single();
        double gap = legend.TranslatePoint(new Point(), layout).Y - map.TranslatePoint(new Point(0, map.ActualHeight), layout).Y;
        Assert.InRange(gap, 0, 12); // A small legend gutter, with no unused map space.
    }

    private static void AssertTilesContained(TreemapPanel map)
    {
        var cells = TreemapLayout.Arrange(map.Weights, map.RenderSize);
        for (int i = 0; i < map.Children.Count; i++)
        {
            var tile = (FrameworkElement)map.Children[i];
            var bounds = tile.TransformToAncestor(map).TransformBounds(new Rect(tile.RenderSize));
            var cell = cells[i];
            Assert.True(bounds.Left >= -.5 && bounds.Top >= -.5
                && bounds.Right <= map.ActualWidth + .5 && bounds.Bottom <= map.ActualHeight + .5,
                $"A map tile extends outside its pane: {bounds}, pane {map.RenderSize}.");
            Assert.True(bounds.Left >= cell.Left - .5 && bounds.Top >= cell.Top - .5
                && bounds.Right <= cell.Right + .5 && bounds.Bottom <= cell.Bottom + .5,
                $"A map tile overlaps a sibling cell: {bounds}, cell {cell}.");
            if (tile is Button button)
            {
                var surface = (Border)button.Template.FindName("TileSurface", button);
                Assert.True(surface.ActualWidth <= button.ActualWidth + .5 && surface.ActualHeight <= button.ActualHeight + .5,
                    "A tile surface is cropped instead of fitting its proportional cell.");
            }
            if (tile is Border { Child: Grid grid, Tag: TreemapPanel nested } && nested.IsVisible)
            {
                var nestedBounds = nested.TransformToAncestor(tile).TransformBounds(new Rect(nested.RenderSize));
                Assert.True(nestedBounds.Left >= -.5 && nestedBounds.Top >= -.5
                    && nestedBounds.Right <= tile.ActualWidth + .5 && nestedBounds.Bottom <= tile.ActualHeight + .5,
                    "A child map extends outside its folder frame.");
                var header = (Button)grid.Children[0];
                Assert.True(header.TranslatePoint(new Point(0, header.ActualHeight), tile).Y <= nestedBounds.Top + .5,
                    "A folder header overlaps its child map.");
                AssertTilesContained(nested);
            }
        }
    }

    private static ScanSnapshot CrowdedMapFixture()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        Folder(2, "Games"); Folder(3, "Cloud"); Folder(4, "Tools");
        File(20, 2, "Call of Duty- Black Ops Cold War", 220.99);
        File(21, 2, "World of Warcraft", 125.56);
        File(22, 2, "Indiana Jones and the Great Circle", 86.7);
        File(23, 2, "The Alters", 63.84); File(24, 2, "Warcraft III", 32.92);
        File(25, 2, "Voidtrain", 18); File(26, 2, "More games", 10.4);
        File(30, 3, "Proton", 230.76); File(31, 3, "OneDrive", 81.64);
        File(40, 4, "tools.bin", 5); File(50, 1, "Other entries", 1.62);
        return builder.Build(@"G:\", "Layout fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);

        void Folder(ulong id, string name)
        {
            int index = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(index, new(1), name);
        }
        void File(ulong id, ulong parent, string name, double size)
        {
            int index = builder.AddFile(new(new(id), FileAttributes.Normal, GiB(size), GiB(size), 0));
            builder.AddLink(index, new(parent), name);
        }
    }

    private static ScanSnapshot NestedLayoutFixture(bool smallEntries = false)
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        Folder(2, 1, "Github"); Folder(3, 1, "Software"); Folder(4, 1, "Other data");
        Folder(20, 2, "Project A"); Folder(21, 2, "Project B"); Folder(22, 2, "Project C");
        File(200, 20, "data.bin", smallEntries ? 495.2 : 500.2, smallEntries ? 220.4 : 225.4);
        File(210, 21, "data.bin", 253.2, 103.9);
        File(220, 22, "data.bin", 84.2, 43.34);
        if (smallEntries)
            for (ulong id = 500; id < 550; id++) File(id, 2, $"small-{id}.bin", .1, .1);
        Folder(30, 3, "SDKs"); Folder(31, 3, "Applications");
        File(300, 30, "data.bin", 45.2, 45.2);
        File(310, 31, "data.bin", 22.5, 22.5);
        File(400, 4, "other.bin", 9.09, 6.93);
        return builder.Build(@"G:\", "Layout fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);

        void Folder(ulong id, ulong parent, string name)
        {
            int index = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(index, new(parent), name);
        }
        void File(ulong id, ulong parent, string name, double logical, double allocated)
        {
            int index = builder.AddFile(new(new(id), FileAttributes.Normal, GiB(logical), GiB(allocated), 0));
            builder.AddLink(index, new(parent), name);
        }
    }

    private static void CheckTreeColumns(MainWindow window, ListBox tree)
    {
        var columns = (GridView)window.FindName("TreeColumns");
        Assert.Equal(new[] { "Name", "Allocated", "Modified" }, columns.Columns.Select(column => ((GridViewColumnHeader)column.Header).Tag));
        var rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
        var selected = tree.SelectedItem;
        var expansion = rows.ExpandedPaths();
        var button = (Button)window.FindName("ColumnsButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var menu = button.ContextMenu;
        Assert.True(menu.IsOpen);
        Assert.False(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, ScanSortColumn.Name)).IsEnabled);
        foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.Tag is ScanSortColumn && !item.IsChecked))
        { item.IsChecked = true; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        Assert.Equal(9, columns.Columns.Count);
        Assert.Same(selected, tree.SelectedItem); Assert.Equal(expansion, rows.ExpandedPaths());
        RenderMenu(menu, Path.Join(FindRepository(), "artifacts", "ui", "tree-column-menu.png"));
        menu.IsOpen = false;
        window.UpdateLayout();
        var dateColumn = (GridViewColumn)window.FindName("ModifiedColumn");
        var header = (GridViewColumnHeader)dateColumn.Header;
        Assert.True(header.Focusable); Assert.True(header.IsTabStop);
        foreach (var key in new[] { Key.Left, Key.Right })
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(header), 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = header };
            tree.RaiseEvent(input);
            Assert.False(input.Handled); Assert.Equal(expansion, rows.ExpandedPaths());
        }
        var thumb = Assert.Single(Descendants(header).OfType<Thumb>());
        // Header and gripper double-clicks never toggle the selected folder.
        foreach (var target in new UIElement[] { header, thumb })
        {
            tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Control.MouseDoubleClickEvent, Source = target });
            Assert.Equal(expansion, rows.ExpandedPaths()); Assert.Same(selected, tree.SelectedItem);
        }
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(27, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(27, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert.Equal(177, dateColumn.Width);
        columns.Columns.Move(columns.Columns.IndexOf(dateColumn), 1);
        Render(window, Path.Join(FindRepository(), "artifacts", "ui", "tree-columns-all.png"), 1220, 910);
        var viewer = Descendants(tree).OfType<ScrollViewer>().First();
        viewer.ScrollToHorizontalOffset(190); window.UpdateLayout();
        PumpUntil(() => true, TimeSpan.FromSeconds(1));
        Assert.True(viewer.HorizontalOffset > 0, $"Horizontal scrolling: extent {viewer.ExtentWidth}, viewport {viewer.ViewportWidth}, scrollable {viewer.ScrollableWidth}, actual {viewer.ActualWidth}, bar {viewer.HorizontalScrollBarVisibility}, columns {columns.Columns.Sum(column => column.ActualWidth)}");
        Assert.True(Descendants(tree).OfType<ListBoxItem>().Count() <= 100);
        window.SaveColumnLayout();
        var saved = TreeColumnLayout.Load(window.ColumnLayoutPath);
        Assert.Equal(ScanSortColumn.Modified, saved.Columns[1].Id); Assert.Equal(177, saved.Columns[1].Width);
        Assert.All(saved.Columns, state => Assert.True(state.Visible));
        ResetTreeColumns(window);
        PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        typeof(MainWindow).GetMethod("LoadColumnLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Assert.Equal(9, columns.Columns.Count); Assert.Same(dateColumn, columns.Columns[1]); Assert.Equal(177, dateColumn.Width);
        viewer.ScrollToHorizontalOffset(0); window.UpdateLayout();
        var nameHeader = (GridViewColumnHeader)window.FindName("NameHeader");
        RightClick(window, nameHeader);
        Assert.False(tree.ContextMenu.IsOpen);
        Assert.True(nameHeader.ContextMenu.IsOpen); nameHeader.ContextMenu.IsOpen = false;
    }

    private static void CheckVisibleInitialSort(MainWindow window)
    {
        ResetTreeColumns(window);
        PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        Load(true);
        ClickSort("AllocationHeader");
        var button = (Button)window.FindName("ColumnsButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var allocation = button.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, ScanSortColumn.Allocated));
        allocation.IsChecked = false; allocation.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(button.ContextMenu.IsEnabled); // Another toggle cannot hide the pending sort column.
        button.ContextMenu.IsOpen = false;
        PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        Assert.True(button.ContextMenu.IsEnabled);
        Load(true); Assert.Equal("Name ↑", ((GridViewColumnHeader)window.FindName("NameHeader")).Content);
        ResetTreeColumns(window);
        ClickSort("AllocationHeader");
        Load(false); Assert.Equal("Name ↑", ((GridViewColumnHeader)window.FindName("NameHeader")).Content);
        var columns = (GridView)window.FindName("TreeColumns");
        Assert.Contains(columns.Columns, column => ReferenceEquals(column.Header, window.FindName("NameHeader")));

        void Load(bool allocationData)
        {
            var builder = new ScanBuilder { AllocationData = allocationData };
            builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
            for (ulong i = 2; i <= 3; i++)
            {
                int file = builder.AddFile(new(new(i), FileAttributes.Normal, (long)i, allocationData ? (long)i : 0, 0));
                builder.AddLink(file, new(1), $"file-{i}.bin");
            }
            var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
            var task = window.InitializeExplorerAsync(scan, [], default);
            PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10)); task.GetAwaiter().GetResult();
        }
        void ClickSort(string name)
        {
            ((GridViewColumnHeader)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        }
    }

    private static void ResetTreeColumns(MainWindow window)
    {
        var button = (Button)window.FindName("ColumnsButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var reset = button.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Reset columns"));
        reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        button.ContextMenu.IsOpen = false;
    }
}
