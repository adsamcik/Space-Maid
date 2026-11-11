using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckExplorerLayout(MainWindow window, Application app, string artifactRoot)
    {
        ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        window.SetScanTarget(@"G:\");
        var scan = NestedLayoutFixture(smallEntries: true);
        var opportunities = new[] { Kind(@"G:\Github\Project A", OpportunityAppearance.Project), Kind(@"G:\Github\Project B", OpportunityAppearance.Cache) };
        Complete(window.InitializeExplorerAsync(scan, opportunities, default));
        ((TextBlock)window.FindName("StatusText")).Text = "Illustrative inventory · Layout verification";
        var tree = (ListView)window.FindName("SpaceTree");
        var map = (TreemapPanel)window.FindName("SpaceMap");
        var list = (Border)window.FindName("ListPane");
        var divider = (GridSplitter)window.FindName("ExplorerDivider");
        var views = (StackPanel)window.FindName("CompactViews");
        var listView = (RadioButton)window.FindName("ListViewButton");
        var mapView = (RadioButton)window.FindName("MapViewButton");
        var detail = (StackPanel)window.FindName("EntryDetail");
        var rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
        listView.IsChecked = true;
        window.Width = 1220;
        window.UpdateLayout();
        ((ColumnDefinition)window.FindName("ListColumn")).Width = new(1, GridUnitType.Star);
        ((ColumnDefinition)window.FindName("MapColumn")).Width = new(1.1, GridUnitType.Star);
        Draw("wide", 1220, 910);
        Assert.True(list.IsVisible && map.IsVisible && divider.IsVisible);
        Assert.False(views.IsVisible);
        Assert.True(((StackPanel)window.FindName("ProjectLegend")).IsVisible);
        Assert.True(((StackPanel)window.FindName("CacheLegend")).IsVisible);
        Assert.False(((StackPanel)window.FindName("AgentLegend")).IsVisible);
        Assert.All(map.Buttons, button =>
        {
            var labels = Descendants(button).OfType<TextBlock>().ToArray();
            Assert.DoesNotContain(labels, label => label.Text.Contains('%'));
            Assert.Contains("% of parent", AutomationProperties.GetName(button));
        });

        double initialWidth = list.ActualWidth;
        Drag(60);
        Assert.InRange(list.ActualWidth - initialWidth, 59, 61);
        double draggedWidth = list.ActualWidth;
        divider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(divider), 0, Key.Right)
            { RoutedEvent = Keyboard.KeyDownEvent });
        window.UpdateLayout();
        Assert.InRange(list.ActualWidth - draggedWidth, 23, 25);
        Drag(10000);
        Assert.InRange(((ColumnDefinition)window.FindName("MapColumn")).ActualWidth, 339, 341);
        AssertTilesContained(map);
        Draw("wide-map-minimum", 1220, 910);
        Drag(-10000);
        Assert.InRange(list.ActualWidth, 339, 341);
        Drag(180);
        double savedRatio = list.ActualWidth / ((Grid)window.FindName("ExplorePanel")).ActualWidth;

        Complete(window.NavigateToEntryAsync(scan.FindEntry(@"G:\Github\Project A")!.Value));
        Complete(window.NavigateToEntryAsync(scan.FindEntry(@"G:\Github")!.Value));
        var scroll = Descendants(tree).OfType<ScrollViewer>().First();
        scroll.ScrollToVerticalOffset(20);
        window.UpdateLayout();
        double savedOffset = scroll.VerticalOffset;
        Assert.True(savedOffset > 0);
        var selected = tree.SelectedItem;
        var expanded = rows.ExpandedPaths();
        Draw("compact-list", 1020, 790);
        Assert.True(list.IsVisible && views.IsVisible && detail.IsVisible);
        Assert.False(map.IsVisible || divider.IsVisible);
        Assert.InRange(list.ActualWidth, views.ActualWidth - 1, views.ActualWidth + 1);
        Assert.Same(selected, tree.SelectedItem);
        Assert.Equal(expanded, rows.ExpandedPaths());
        Assert.InRange(scroll.VerticalOffset, savedOffset - 1, savedOffset + 1);
        mapView.IsChecked = true;
        Draw("compact-map", 1020, 790);
        Assert.True(map.IsVisible && detail.IsVisible);
        Assert.False(list.IsVisible);
        AssertMapFillsPane(window, map);
        AssertTilesContained(map);
        listView.IsChecked = true;
        window.UpdateLayout();
        Assert.Same(rows, tree.ItemsSource);
        Assert.Same(selected, tree.SelectedItem);
        Assert.InRange(scroll.VerticalOffset, savedOffset - 1, savedOffset + 1);
        mapView.IsChecked = true;
        Draw("wide-restored", 1220, 910);
        Assert.InRange(list.ActualWidth / ((Grid)window.FindName("ExplorePanel")).ActualWidth, savedRatio - .002, savedRatio + .002);
        Draw("compact-map-restored", 1020, 790);
        Assert.True(map.IsVisible && !list.IsVisible);

        // Expanding available content via the sidebar uses the same breakpoint.
        var sidebar = (Button)window.FindName("SidebarToggle");
        sidebar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Assert.True(list.IsVisible && map.IsVisible && divider.IsVisible);
        sidebar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Assert.True(!list.IsVisible && map.IsVisible);
        window.Activate();
        PumpUntil(() => window.IsActive, TimeSpan.FromSeconds(5));
        var basis = (ComboBox)window.FindName("CompactSizeBasisSelector");
        Assert.True(basis.IsVisible && basis.Focus());
        basis.SelectedIndex = 1;
        PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        Assert.False(Assert.IsType<ExplorerRows>(tree.ItemsSource).UseAllocation);
        Assert.Equal("Area = file size", ((TextBlock)window.FindName("MapBasis")).Text);
        Assert.True(basis.IsKeyboardFocusWithin);
        basis.SelectedIndex = 0;
        PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));

        // Map navigation works without bringing back the hidden list. Grouped
        // entries deliberately reveal the list where their individual files live.
        var project = map.Buttons.First(button => button.DataContext is SpaceContribution { Name: "Project A" });
        project.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        Assert.Equal(@"G:\Github\Project A", Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
        Assert.True(map.IsVisible && !list.IsVisible);
        Assert.True(map.IsKeyboardFocusWithin);
        Assert.True(((Button)window.FindName("SelectionMoreButton")).IsVisible);
        Complete(window.NavigateToEntryAsync(scan.FindEntry(@"G:\Github")!.Value));
        var group = map.Buttons.Single(button => button.DataContext is SpaceContribution { Link: -2 });
        group.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        Assert.True(listView.IsChecked);
        Assert.True(list.IsVisible && !map.IsVisible);
        Assert.True(tree.IsKeyboardFocusWithin);
        Assert.False(((Button)window.FindName("RecycleSelectionButton")).IsEnabled);

        Complete(window.InitializeExplorerAsync(CrowdedMapFixture(), [], default));
        Assert.False(((StackPanel)window.FindName("OtherLegend")).IsVisible);
        mapView.IsChecked = true;
        window.Width = 1220;
        window.UpdateLayout();
        Assert.True(((Button)window.FindName("ColumnsButton")).Focus());
        window.Width = 1020;
        window.UpdateLayout();
        Assert.True(mapView.IsKeyboardFocusWithin);
        window.Width = 1220;
        window.UpdateLayout();
        Assert.True(divider.IsKeyboardFocusWithin);
#pragma warning disable WPF0001
        var originalTheme = app.ThemeMode;
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            listView.IsChecked = true;
            Draw($"list-{theme}", 1020, 790);
            mapView.IsChecked = true;
            Draw($"map-{theme}", 1020, 790);
            AssertTilesContained(map);
            AssertMapFillsPane(window, map);
            Draw($"split-{theme}", 1220, 910);
            AssertTilesContained(map);
        }
        app.ThemeMode = originalTheme;
#pragma warning restore WPF0001
        window.SetScanTarget(@"G:\");
        Draw("empty-map", 1020, 790);
        Assert.True(((TextBlock)window.FindName("MapHint")).IsVisible);
        Assert.False(detail.IsVisible);
        listView.IsChecked = true;
        Draw("empty-list", 1020, 790);
        Assert.True(((TextBlock)window.FindName("TreeHint")).IsVisible);
        Assert.InRange(((Border)window.FindName("MapPane")).ActualHeight, 0, 1);

        using var pathFixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(pathFixture.Path, "Documents")).FullName;
        File.WriteAllBytes(Path.Join(folder, "notes.txt"), new byte[4096]);
        window.SetScanTarget(pathFixture.Path);
        Complete(window.ScanCurrentPathAsync());
        mapView.IsChecked = true;
        Complete(window.NavigatePathAsync(folder));
        Assert.Equal(folder, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
        Assert.True(map.IsKeyboardFocusWithin);
        Complete(window.NavigatePathAsync(pathFixture.Path));
        Assert.True(map.IsKeyboardFocusWithin);
        listView.IsChecked = true;
        Complete(window.NavigatePathAsync(folder));
        Assert.Equal(folder, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
        Assert.True(tree.IsKeyboardFocusWithin);

        Opportunity Kind(string path, OpportunityAppearance appearance) =>
            new(scan.Entry(scan.FindEntry(path)!.Value).FileIndex, "Layout fixture", "", path, 0, 0, "", "", "", true) { Appearance = appearance };
        void Draw(string name, int width, int height) => Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"explorer-layout-{name}.png"), width, height);
        void Drag(double distance)
        {
            divider.RaiseEvent(new DragStartedEventArgs(0, 0));
            divider.RaiseEvent(new DragDeltaEventArgs(distance, 0));
            divider.RaiseEvent(new DragCompletedEventArgs(distance, 0, false));
            window.UpdateLayout();
        }
        static void Complete(Task task)
        {
            PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));
            task.GetAwaiter().GetResult();
        }
    }
}
