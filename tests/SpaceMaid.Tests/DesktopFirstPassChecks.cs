using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Input;
using SpaceMaid.App;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckFirstPass(MainWindow window, Application app, string fixtureRoot, string artifactRoot)
    {
        var tools = (ToggleButton)window.FindName("ToolsToggle");
        var specialists = (StackPanel)window.FindName("ExtensionNavigation");
        tools.IsChecked = false;
        Assert.False(tools.IsChecked);
        Assert.False(specialists.IsVisible);
        Assert.Single(((StackPanel)window.FindName("AssistantNavigation")).Children.OfType<RadioButton>());
        Assert.True(window.OpenExtension("space-maid.agents"));
        Assert.True(tools.IsChecked);
        Assert.Equal("true", File.ReadAllText(window.SidebarSettingsPath));
        tools.IsChecked = false;
        ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        Assert.False(tools.IsChecked);

        string root = Directory.CreateDirectory(Path.Join(fixtureRoot, "Documents and downloads")).FullName;
        for (int i = 0; i < 45; i++)
        {
            string folder = Directory.CreateDirectory(Path.Join(root, $"Project {i:00} — photos and reference material")).FullName;
            File.WriteAllBytes(Path.Join(folder, "Reference photo.jpg"), new byte[(46 - i) * 4096]);
        }
        window.SetScanTarget(root);
        var scanTask = window.ScanCurrentPathAsync();
        PumpUntil(() => scanTask.IsCompleted, TimeSpan.FromSeconds(20));
        Assert.True(scanTask.GetAwaiter().GetResult());
        var tree = (ListView)window.FindName("SpaceTree");
        var rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
        var scan = rows.Scan;
        int first = scan.FindEntry(Path.Join(root, "Project 01 — photos and reference material"))!.Value;
        int second = scan.FindEntry(Path.Join(root, "Project 30 — photos and reference material"))!.Value;
        var open = (Button)window.FindName("OpenSelectionButton");
        var recycle = (Button)window.FindName("RecycleSelectionButton");
        var more = (Button)window.FindName("SelectionMoreButton");
        var navigate = window.NavigateToEntryAsync(first);
        PumpUntil(() => navigate.IsCompleted, TimeSpan.FromSeconds(10));
        navigate.GetAwaiter().GetResult();
        Assert.True(open.IsEnabled);
        Assert.True(recycle.IsEnabled);
        Assert.Empty(more.ContextMenu.Items);

        navigate = window.NavigateToEntryAsync(-1);
        PumpUntil(() => navigate.IsCompleted, TimeSpan.FromSeconds(10));
        navigate.GetAwaiter().GetResult();
        var map = (TreemapPanel)window.FindName("SpaceMap");
        var group = map.Buttons.Single(button => button.DataContext is SpaceContribution { Link: -2 });
        group.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        Assert.False(more.IsEnabled);
        tree.UpdateLayout();
        var selectedRow = Assert.IsType<ListViewItem>(tree.ItemContainerGenerator.ContainerFromItem(tree.SelectedItem));
        selectedRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.True(more.IsEnabled);
        navigate = window.NavigateToEntryAsync(first);
        PumpUntil(() => navigate.IsCompleted, TimeSpan.FromSeconds(10));
        navigate.GetAwaiter().GetResult();
        // The visible action opens the existing review; cancelling keeps the fixture.
        bool reviewed = false;
        Exception? reviewFailure = null;
        var reviewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            if (app.Windows.OfType<RecycleFolderWindow>().FirstOrDefault() is not { } dialog) return;
            try
            {
                Assert.Equal(scan.EntryPath(first), ((TextBox)dialog.FindName("TargetPath")).Text);
                Assert.True(((Button)dialog.FindName("KeepButton")).IsCancel);
                reviewed = true;
            }
            catch (Exception error) { reviewFailure = error; }
            finally { dialog.Close(); }
        }, Dispatcher.CurrentDispatcher);
        try
        {
            recycle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => reviewed || reviewFailure is not null, TimeSpan.FromSeconds(10));
        }
        finally { reviewTimer.Stop(); }
        if (reviewFailure is not null) throw reviewFailure;
        Assert.True(Directory.Exists(scan.EntryPath(first)));
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(more.ContextMenu.IsOpen);
        Assert.Contains(more.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Recheck this folder"));

        using var work = new CancellationTokenSource();
        var workField = typeof(MainWindow).GetField("_work", BindingFlags.NonPublic | BindingFlags.Instance)!;
        workField.SetValue(window, work);
        window.SetBusy(true);
        ((TextBlock)window.FindName("StatusText")).Text = "Updating affected folders…";
        try
        {
            Assert.False(more.ContextMenu.IsOpen);
            Assert.True(tree.IsEnabled);
            Assert.False(open.IsEnabled);
            Assert.False(recycle.IsEnabled);
            Assert.False(more.IsEnabled);
            var blocked = window.RecycleSelectedFoldersAsync();
            Assert.Empty(blocked.GetAwaiter().GetResult());
            Assert.True(Directory.Exists(scan.EntryPath(first)));
            navigate = window.NavigateToEntryAsync(second);
            PumpUntil(() => navigate.IsCompleted, TimeSpan.FromSeconds(10));
            navigate.GetAwaiter().GetResult();
            Assert.Equal(second, Assert.IsType<ExplorerRow>(tree.SelectedItem).Entry.LinkIndex);
            Assert.Contains("Project 30", ((TextBlock)window.FindName("SelectionTitle")).Text);
            var scroll = Descendants(tree).OfType<ScrollViewer>().First();
            scroll.ScrollToVerticalOffset(20);
            window.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0);
            Assert.False(recycle.IsEnabled);
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme;
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"first-pass-busy-{theme}.png"), 1220, 940);
            }
#pragma warning restore WPF0001
        }
        finally { workField.SetValue(window, null); window.SetBusy(false); }
        Assert.True(recycle.IsEnabled);

        // Start a real owned-folder refresh, then browse its retained snapshot.
        // Publication must preserve the new selection rather than restoring the old one.
        var recheck = window.RecheckFolderAsync(root);
        navigate = window.NavigateToEntryAsync(first);
        PumpUntil(() => recheck.IsCompleted && navigate.IsCompleted, TimeSpan.FromSeconds(20));
        recheck.GetAwaiter().GetResult(); navigate.GetAwaiter().GetResult();
        Assert.Equal(scan.EntryPath(first), Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
        Assert.NotSame(scan, Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan);
        Assert.True(recycle.IsEnabled);
        Assert.Empty(more.ContextMenu.Items);

        // Header estimates belong to Cleanup; Explore keeps the drive and scan totals.
        Assert.False(((TextBlock)window.FindName("CandidateText")).IsVisible);
        Assert.Equal("Cleanup", ((RadioButton)window.FindName("OpportunitiesTab")).Content);
#pragma warning disable WPF0001
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            foreach (var size in new[] { (1220, 940), (1020, 820) })
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"first-pass-explore-{theme}-{size.Item1}.png"), size.Item1, size.Item2);
            var explore = (Grid)window.FindName("ExplorePanel");
            explore.IsEnabled = false;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"first-pass-disabled-{theme}.png"), 1020, 820);
            explore.IsEnabled = true;
            ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
            if (window.CleanupWorkspaceView is { } workspace)
            {
                ((Button)window.FindName("CleanupBrowserBack")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(((TextBlock)window.FindName("CandidateText")).IsVisible);
                ((Expander)workspace.FindName("MoreOptions")).IsExpanded = true;
                ((Button)workspace.FindName("BrowseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"first-pass-cleanup-{theme}.png"), 1020, 820);
            Assert.True(((TextBlock)window.FindName("CandidateText")).IsVisible);
            Assert.True(((Button)window.FindName("OptimizationButton")).IsVisible);
            ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        }
#pragma warning restore WPF0001
    }
}
