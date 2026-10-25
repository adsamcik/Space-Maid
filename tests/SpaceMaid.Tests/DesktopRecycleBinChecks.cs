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
    private static void CheckRecycleBinCleanup(MainWindow window, Application app, string artifactRoot)
    {
        string root = Path.GetPathRoot(window.CurrentPath)!;
        long items = 3, bytes = GiB(6); int emptyCalls = 0;
        var roots = new List<string>();
        var operations = new FileOperations(recycleBins: new(path => "fixture-" + path, _ => (bytes, items), (_, path) =>
        { roots.Add(path); emptyCalls++; bytes = 0; items = 0; return 0; }));
        window.Operations = operations;
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex = 0;
        ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var refreshed = window.RefreshRecycleBinAsync();
        PumpUntil(() => refreshed.IsCompleted, TimeSpan.FromSeconds(3)); refreshed.GetAwaiter().GetResult();
        var empty = (Button)window.FindName("EmptyRecycleBinButton");
        Assert.True(empty.IsEnabled);
        Assert.Contains(root, ((TextBlock)window.FindName("RecycleBinTitle")).Text);
        Assert.Equal("3 items", ((TextBlock)window.FindName("RecycleBinCount")).Text);
        Assert.Equal("6 GiB", ((TextBlock)window.FindName("RecycleBinSize")).Text);
#pragma warning disable WPF0001
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"recycle-bin-overview-{theme}.png"), 1220, 910);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"recycle-bin-overview-compact-{theme}.png"), 1020, 790);
        }
#pragma warning restore WPF0001
        RunConfirmation(accept: false);
        Assert.Equal(0, emptyCalls); Assert.Equal(3, items);
        RunConfirmation(accept: false, cancelWork: true);
        Assert.Equal(0, emptyCalls); Assert.Equal(3, items);
        window.DriveSpaceProvider = _ => (GiB(500), GiB(24));
        RunConfirmation(accept: true);
        PumpUntil(() => ((TextBlock)window.FindName("RecycleBinCount")).Text == "Empty", TimeSpan.FromSeconds(3));
        Assert.False(empty.IsEnabled); Assert.Equal(1, emptyCalls); Assert.Equal(root, Assert.Single(roots));
        Assert.Contains("empty", ((TextBlock)window.FindName("StatusText")).Text, StringComparison.OrdinalIgnoreCase);
        PumpUntil(() => ((TextBlock)window.FindName("FreeSpaceText")).Text == "24 GiB", TimeSpan.FromSeconds(3));
        Assert.Contains("recycle-bin-native-finish", File.ReadAllText(Assert.IsType<string>(((Button)window.FindName("OpenLogButton")).ToolTip)));
        window.Operations = new(recycleBins: new(_ => null, _ => throw new InvalidOperationException("Unavailable query must not run."),
            (_, _) => throw new InvalidOperationException("Unavailable empty must not run.")));
        var unavailable = window.RefreshRecycleBinAsync();
        PumpUntil(() => unavailable.IsCompleted, TimeSpan.FromSeconds(3)); unavailable.GetAwaiter().GetResult();
        Assert.Equal("Unavailable", ((TextBlock)window.FindName("RecycleBinCount")).Text);
        Assert.Equal("—", ((TextBlock)window.FindName("RecycleBinSize")).Text); Assert.False(empty.IsEnabled);

        // Synthetic drive metadata and a fake Shell callback keep every real bin untouched.
        string previousRoot = root;
        root = root.Equals(@"Q:\", StringComparison.OrdinalIgnoreCase) ? @"R:\" : @"Q:\";
        items = 3; bytes = GiB(6); emptyCalls = 0; roots.Clear();
        window.Operations = operations;
        window.SetScanTarget(root);
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int bin = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(bin, new(1), "$Recycle.Bin");
        int recycled = builder.AddFile(new(new(3), FileAttributes.Normal, GiB(6), GiB(6), 0)); builder.AddLink(recycled, new(2), "$Rfixture");
        int folder = builder.AddFile(new(new(4), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(folder, new(1), "Projects");
        int file = builder.AddFile(new(new(5), FileAttributes.Normal, GiB(2), GiB(2), 0)); builder.AddLink(file, new(4), "sample.bin");
        int legacy = builder.AddFile(new(new(6), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(legacy, new(1), "Recycler");
        var scan = builder.Build(root, "fixture", TimeSpan.Zero, 0, default);
        ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        var initialized = window.InitializeExplorerAsync(scan, [], default);
        PumpUntil(() => initialized.IsCompleted, TimeSpan.FromSeconds(3)); initialized.GetAwaiter().GetResult();
        var tree = (ListBox)window.FindName("SpaceTree");
        var explorer = Assert.IsType<ExplorerRows>(tree.ItemsSource);
        foreach (int directory in new[] { folder, legacy, bin })
        {
            var container = Assert.IsAssignableFrom<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(explorer.Find(scan.PrimaryLink(directory))));
            RightClick(window, container);
            PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
            var command = Assert.IsType<MenuItem>(tree.ContextMenu.Items[1]);
            Assert.Equal(directory == folder ? "Move to Recycle Bin" : "Empty Recycle Bin…", command.Header);
            Assert.True(command.IsEnabled);
            tree.ContextMenu.IsOpen = false;
        }
        RightClick(window, tree, keyboard: true);
        PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
        Assert.Equal("Empty Recycle Bin…", ((MenuItem)tree.ContextMenu.Items[1]).Header);
#pragma warning disable WPF0001
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"recycle-bin-explorer-{theme}.png"), 1220, 910);
            RenderMenu(tree.ContextMenu, Path.Join(artifactRoot, "artifacts", "ui", $"recycle-bin-menu-{theme}.png"));
        }
#pragma warning restore WPF0001
        tree.ContextMenu.IsOpen = false;
        var map = (TreemapPanel)window.FindName("SpaceMap");
        var binTile = map.Buttons.Single(button => ((SpaceContribution)button.DataContext).Path == root + "$Recycle.Bin");
        RightClick(window, binTile);
        PumpUntil(() => binTile.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
        var capturedEmpty = Assert.IsType<MenuItem>(binTile.ContextMenu.Items[1]);
        Assert.Equal("Empty Recycle Bin…", capturedEmpty.Header);
        binTile.ContextMenu.IsOpen = false;
        RunConfirmation(accept: false, menuItem: capturedEmpty);
        Assert.Equal(0, emptyCalls); Assert.Equal(3, items);
        RunConfirmation(accept: true, menuItem: capturedEmpty);
        Assert.Equal(1, emptyCalls); Assert.Equal(root, Assert.Single(roots)); Assert.Equal(0, items);
        window.SetScanTarget(previousRoot);
        capturedEmpty.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, emptyCalls); // An old menu cannot empty a newly selected drive.

        void RunConfirmation(bool accept, bool cancelWork = false, MenuItem? menuItem = null)
        {
            Exception? failure = null; bool seen = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                var dialog = app.Windows.OfType<EmptyRecycleBinWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (dialog is null) return;
                timer.Stop(); seen = true;
                try
                {
                    Assert.True(((Button)dialog.FindName("KeepButton")).IsDefault);
                    Assert.Contains(root, ((TextBlock)dialog.FindName("BinScope")).Text);
                    Assert.Contains("3 items", ((TextBlock)dialog.FindName("BinScope")).Text);
#pragma warning disable WPF0001
                    foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                    {
                        app.ThemeMode = theme;
                        Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", $"empty-recycle-bin-{theme}.png"), 440, 250);
                    }
#pragma warning restore WPF0001
                    if (cancelWork) ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (accept) ((Button)dialog.FindName("EmptyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            };
            timer.Start();
            try
            {
                var action = menuItem is null ? window.EmptySelectedRecycleBinAsync() : null;
                menuItem?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                PumpUntil(() => action?.IsCompleted ?? ((Button)window.FindName("ScanButton")).IsEnabled, TimeSpan.FromSeconds(5));
                action?.GetAwaiter().GetResult();
                if (failure is not null) throw failure;
                Assert.True(seen);
            }
            finally { timer.Stop(); }
        }
    }
}
