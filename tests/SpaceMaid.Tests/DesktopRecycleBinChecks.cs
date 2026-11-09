using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.IO;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckRecycleLeftoversReview(MainWindow owner, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        File.WriteAllBytes(Path.Join(fixture.Path, "$Rsample.jpg"), new byte[5 * 1024 * 1024]);
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "$Rfolder")).FullName;
        File.WriteAllText(Path.Join(folder, "notes.txt"), "fixture");
        string version = Directory.CreateDirectory(Path.Join(fixture.Path, "$Rcache.nuget", "packages", "demo", "1.0.0")).FullName;
        File.WriteAllText(Path.Join(version, ".nupkg.metadata"), "{}");
        File.WriteAllBytes(Path.Join(version, "demo.1.0.0.nupkg"), new byte[2 * 1024 * 1024]);
        string toolchains = Directory.CreateDirectory(Path.Join(fixture.Path, "$Rrust.rustup", "toolchains")).FullName;
        File.WriteAllBytes(Path.Join(toolchains, "rustc.exe"), new byte[4 * 1024 * 1024]);
        File.WriteAllText(Path.Join(toolchains, "..", "settings.toml"), "fixture");
        var operations = new FileOperations();
        var dialog = new RecycleLeftoversWindow(operations, Path.GetPathRoot(fixture.Path)!, default,
            token => operations.Files.ReviewRecycleLeftoversAt(fixture.Path, DateTime.UtcNow.AddMinutes(1), token))
        { Owner = owner };
        try
        {
            dialog.Show();
            var list = (DataGrid)dialog.FindName("EntryList");
            var groups = (ListBox)dialog.FindName("GroupList");
            var hint = (TextBlock)dialog.FindName("GroupHint");
            var status = (TextBlock)dialog.FindName("StatusText");
            var contents = (ListBox)dialog.FindName("ContentsList");
            var select = (Button)dialog.FindName("SelectButton");
            var recover = (Button)dialog.FindName("RecoverButton");
            var delete = (Button)dialog.FindName("DeleteButton");
            PumpUntil(() => list.IsEnabled && !string.IsNullOrEmpty(status.Text), TimeSpan.FromSeconds(10));
            Assert.Equal(4, groups.Items.Count);
            Assert.Single(list.Items.Cast<object>());
            Assert.False(recover.IsEnabled);
            Assert.False(delete.IsEnabled);
            Assert.Contains("Suggested cleanup", hint.Text);
            Assert.Equal("Check suggested", select.Content);
            Assert.StartsWith("packages", Assert.Single(contents.Items.Cast<string>()));
            select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("1 checked", status.Text);
            Assert.True(delete.IsEnabled);
            groups.SelectedIndex = 1;
            Assert.Contains("custom toolchains", hint.Text);
            Assert.Contains(contents.Items.Cast<string>(), item => item.StartsWith("settings.toml", StringComparison.Ordinal));
            Assert.Contains("1 checked", status.Text);
            Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "recycle-leftovers-review.png"), 1100, 640);
            ((Button)dialog.FindName("ContentsToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("toolchains\\rustc.exe", contents.Items.Cast<string>());
            ((Button)dialog.FindName("ClearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(delete.IsEnabled);
            dialog.UpdateLayout();
            var checkboxCell = Assert.IsAssignableFrom<FrameworkElement>(list.Columns[0].GetCellContent(list.Items[0]));
            var checkbox = Assert.IsType<CheckBox>(VisualTreeHelper.GetChild(checkboxCell, 0));
            checkbox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            PumpUntil(() => recover.IsEnabled && delete.IsEnabled, TimeSpan.FromSeconds(2));
            var filter = (TextBox)dialog.FindName("FilterBox");
            filter.Text = "$Rfolder";
            Assert.False(delete.IsEnabled);
            filter.Clear();
            groups.SelectedIndex = 3;
            Assert.Equal(Visibility.Collapsed, select.Visibility);
            Assert.StartsWith("notes.txt", Assert.Single(contents.Items.Cast<string>()));
            Assert.True(File.Exists(Path.Join(fixture.Path, "$Rsample.jpg")));
        }
        finally { dialog.Close(); }
    }

    private static void CheckRecycleBinStorage(MainWindow window, string artifactRoot)
    {
        const string root = @"Q:\";
        window.SetScanTarget(root);
        window.Operations = new(recycleBins: new(path => "fixture-" + path, _ => (0, 0),
            (_, _) => throw new InvalidOperationException("This check is read-only.")));
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int bin = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0));
        builder.AddLink(bin, new(1), "$Recycle.Bin");
        int leftover = builder.AddFile(new(new(3), FileAttributes.Normal, GiB(6), GiB(6), 0));
        builder.AddLink(leftover, new(2), "$Rfixture");
        var scan = builder.Build(root, "fixture", TimeSpan.Zero, 0, default);
        var initialized = window.InitializeExplorerAsync(scan, [], default);
        PumpUntil(() => initialized.IsCompleted, TimeSpan.FromSeconds(3));
        initialized.GetAwaiter().GetResult();

        var tree = (ListBox)window.FindName("SpaceTree");
        var rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
        var row = rows.Row(rows.Find(scan.PrimaryLink(bin)));
        Assert.Equal("Recycle Bin storage", row.Name);
        Assert.Equal("6 GiB", row.Allocated);
        Assert.Contains("Cleanup checks your account", row.SizeNote);
        var map = (TreemapPanel)window.FindName("SpaceMap");
        var tile = map.Buttons.Single(button => ((SpaceContribution)button.DataContext).Path == root + "$Recycle.Bin");
        Assert.StartsWith("Recycle Bin storage, 6 GiB", AutomationProperties.GetName(tile));
        Assert.Contains("leftover files and other Windows accounts", Assert.IsType<TextBlock>(tile.ToolTip).Text);
        foreach (var size in new[] { (1220, 910), (1020, 790) })
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"recycle-bin-storage-{size.Item1}.png"), size.Item1, size.Item2);

        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        ((Button)OpportunityControl(window, "BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var refreshed = window.RefreshRecycleBinAsync();
        PumpUntil(() => refreshed.IsCompleted, TimeSpan.FromSeconds(3));
        refreshed.GetAwaiter().GetResult();
        Assert.Equal("No listed items", ((TextBlock)OpportunityControl(window, "RecycleBinCount")).Text);
        Assert.True(((Button)OpportunityControl(window, "EmptyRecycleBinButton")).IsEnabled);
    }

    private static void CheckRecycleBinCleanup(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string payload = Path.Join(fixture.Path, "$Rleftover.txt");
        string root = Path.GetPathRoot(window.CurrentPath)!;
        long items = 3, bytes = GiB(6); int emptyCalls = 0;
        int leftoverChecks = 0;
        var roots = new List<string>();
        var operations = new FileOperations(recycleBins: new(path => "fixture-" + path, _ => (bytes, items), (_, path) =>
        { roots.Add(path); emptyCalls++; bytes = 0; items = 0; return 0; }, (files, _, token) =>
        {
            Interlocked.Increment(ref leftoverChecks);
            return files.HasRecycleLeftoversAt(fixture.Path, token);
        }, (files, _, token) => files.ReviewRecycleLeftoversAt(fixture.Path, DateTime.UtcNow.AddMinutes(1), token)));
        window.Operations = operations;
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        ((ComboBox)OpportunityControl(window, "OpportunityGroupingPicker")).SelectedIndex = 0;
        ((Button)OpportunityControl(window, "BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var refreshed = window.RefreshRecycleBinAsync();
        PumpUntil(() => refreshed.IsCompleted, TimeSpan.FromSeconds(3)); refreshed.GetAwaiter().GetResult();
        var empty = (Button)OpportunityControl(window, "EmptyRecycleBinButton");
        Assert.True(empty.IsEnabled);
        Assert.Contains(root, ((TextBlock)OpportunityControl(window, "RecycleBinTitle")).Text);
        Assert.Equal("3 items", ((TextBlock)OpportunityControl(window, "RecycleBinCount")).Text);
        Assert.Equal("6 GiB", ((TextBlock)OpportunityControl(window, "RecycleBinSize")).Text);
#pragma warning disable WPF0001
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"recycle-bin-overview-{theme}.png"), 1220, 910);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"recycle-bin-overview-compact-{theme}.png"), 1020, 790);
        }
#pragma warning restore WPF0001
        RunConfirmation(accept: false);
        Assert.Equal(0, emptyCalls); Assert.Equal(3, items); Assert.Equal(0, leftoverChecks);
        RunConfirmation(accept: false, cancelWork: true);
        Assert.Equal(0, emptyCalls); Assert.Equal(3, items); Assert.Equal(0, leftoverChecks);
        window.DriveSpaceProvider = _ => (GiB(500), GiB(24));
        RunConfirmation(accept: true);
        PumpUntil(() => ((TextBlock)OpportunityControl(window, "RecycleBinCount")).Text == "No listed items", TimeSpan.FromSeconds(3));
        Assert.True(empty.IsEnabled); Assert.Equal(1, emptyCalls); Assert.Equal(root, Assert.Single(roots));
        Assert.Equal(1, leftoverChecks);
        Assert.Contains("Windows-listed items cleared", ((TextBlock)window.FindName("StatusText")).Text);
        PumpUntil(() => ((TextBlock)window.FindName("FreeSpaceText")).Text == "24 GiB", TimeSpan.FromSeconds(3));
        Assert.Contains("recycle-bin-native-finish", File.ReadAllText(Assert.IsType<string>(((Button)window.FindName("OpenLogButton")).ToolTip)));
        window.Operations = new(recycleBins: new(_ => null, _ => throw new InvalidOperationException("Unavailable query must not run."),
            (_, _) => throw new InvalidOperationException("Unavailable empty must not run.")));
        var unavailable = window.RefreshRecycleBinAsync();
        PumpUntil(() => unavailable.IsCompleted, TimeSpan.FromSeconds(3)); unavailable.GetAwaiter().GetResult();
        Assert.Equal("Unavailable", ((TextBlock)OpportunityControl(window, "RecycleBinCount")).Text);
        Assert.Equal("—", ((TextBlock)OpportunityControl(window, "RecycleBinSize")).Text); Assert.False(empty.IsEnabled);

        // Synthetic drive metadata and a fake Shell callback keep every real bin untouched.
        string previousRoot = root;
        root = root.Equals(@"Q:\", StringComparison.OrdinalIgnoreCase) ? @"R:\" : @"Q:\";
        items = 3; bytes = GiB(6); emptyCalls = 0; roots.Clear();
        window.Operations = operations;
        window.SetScanTarget(root);
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int bin = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(bin, new(1), "$Recycle.Bin");
        int recycled = builder.AddFile(new(new(3), FileAttributes.Normal, GiB(39), GiB(39), 0)); builder.AddLink(recycled, new(2), "$Rfixture");
        int folder = builder.AddFile(new(new(4), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(folder, new(1), "Projects");
        int file = builder.AddFile(new(new(5), FileAttributes.Normal, GiB(2), GiB(2), 0)); builder.AddLink(file, new(4), "sample.bin");
        int legacy = builder.AddFile(new(new(6), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(legacy, new(1), "Recycler");
        var scan = builder.Build(root, "fixture", TimeSpan.Zero, 0, default);
        ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        var initialized = window.InitializeExplorerAsync(scan, [], default);
        PumpUntil(() => initialized.IsCompleted, TimeSpan.FromSeconds(3)); initialized.GetAwaiter().GetResult();
        typeof(MainWindow).GetField("_snapshotVolumeId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, "fixture-" + root);
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
        RunConfirmation(accept: false, menuItem: capturedEmpty, scannedSize: "39 GiB");
        Assert.Equal(0, emptyCalls); Assert.Equal(3, items);
        File.WriteAllText(payload, "Keep this orphaned payload");
        RunConfirmation(accept: true, menuItem: capturedEmpty, scannedSize: "39 GiB", reviewLeftovers: false);
        Assert.Equal(1, emptyCalls); Assert.Equal(root, Assert.Single(roots)); Assert.Equal(0, items);
        Assert.Contains("Leftovers kept", ((TextBlock)window.FindName("StatusText")).Text);
        RunConfirmation(accept: null, reviewLeftovers: true);
        Assert.Equal(1, emptyCalls); // Windows already lists zero items, so review without another purge.
        Assert.Equal(3, leftoverChecks);
        Assert.Equal("Keep this orphaned payload", File.ReadAllText(payload));
        window.SetScanTarget(previousRoot);
        capturedEmpty.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, emptyCalls); // An old menu cannot empty a newly selected drive.

        void RunConfirmation(bool? accept, bool cancelWork = false, MenuItem? menuItem = null,
            string? scannedSize = null, bool? reviewLeftovers = null)
        {
            Exception? failure = null;
            bool seen = false, sawAlert = false, sawReview = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                var review = app.Windows.OfType<RecycleLeftoversWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (review is not null)
                {
                    if (!((Button)review.FindName("RefreshButton")).IsEnabled) return;
                    try
                    {
                        Assert.Single(((DataGrid)review.FindName("EntryList")).Items);
                        Assert.False(((Button)review.FindName("DeleteButton")).IsEnabled);
                        sawReview = true;
                    }
                    catch (Exception error) { failure = error; timer.Stop(); }
                    review.Close();
                    return;
                }
                var dialog = app.Windows.OfType<EmptyRecycleBinWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (dialog is null) return;
                try
                {
                    Assert.True(((Button)dialog.FindName("KeepButton")).IsDefault);
                    Assert.Contains(root, ((TextBlock)dialog.FindName("BinScope")).Text);
                    if (dialog.Title == "Recycle Bin leftovers found")
                    {
                        Assert.False(sawAlert);
                        sawAlert = true;
                        Assert.True(reviewLeftovers.HasValue);
                        Assert.Equal(0, items);
                        Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "recycle-bin-leftovers-alert.png"), 460, 250);
                        if (reviewLeftovers == true) ((Button)dialog.FindName("EmptyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else dialog.Close();
                        return;
                    }
                    Assert.False(seen);
                    seen = true;
                    Assert.True(accept.HasValue);
                    Assert.Contains("3 items listed by Windows", ((TextBlock)dialog.FindName("BinScope")).Text);
                    Assert.Equal("6 GiB", ((TextBlock)dialog.FindName("BinSize")).Text);
                    if (scannedSize is not null) Assert.Equal(scannedSize, ((TextBlock)dialog.FindName("ScanSize")).Text);
#pragma warning disable WPF0001
                    foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                    {
                        app.ThemeMode = theme;
                        Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", $"empty-recycle-bin-{theme}.png"), 460, 270);
                        if (scannedSize is not null)
                            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"empty-recycle-bin-owner-{theme}.png"), 1220, 910);
                    }
#pragma warning restore WPF0001
                    if (cancelWork)
                    {
                        timer.Stop();
                        ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if (accept == true) ((Button)dialog.FindName("EmptyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
                catch (Exception error) { failure = error; timer.Stop(); dialog.Close(); }
            };
            timer.Start();
            try
            {
                var action = menuItem is null ? window.EmptySelectedRecycleBinAsync() : null;
                menuItem?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                PumpUntil(() => action?.IsCompleted ?? ((Button)window.FindName("ScanButton")).IsEnabled, TimeSpan.FromSeconds(10));
                action?.GetAwaiter().GetResult();
                if (failure is not null) throw failure;
                Assert.Equal(accept.HasValue, seen);
                Assert.Equal(reviewLeftovers.HasValue, sawAlert);
                Assert.Equal(reviewLeftovers == true, sawReview);
            }
            finally { timer.Stop(); }
        }
    }
}
