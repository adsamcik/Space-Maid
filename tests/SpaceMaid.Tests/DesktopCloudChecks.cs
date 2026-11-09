using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckExplorerSizeBasis(MainWindow window, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string online = Path.Join(fixture.Path, "online.bin");
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "Other folder")).FullName;
        const int logicalBytes = 8 * 1024 * 1024;
        using (var stream = File.Create(online)) stream.SetLength(logicalBytes);
        File.WriteAllBytes(Path.Join(fixture.Path, "local.bin"), new byte[4096]);
        using var registration = CloudFilesTests.RegisterSyncedFixture(fixture.Path, online, onlineOnly: true);
        var navigation = window.NavigatePathAsync(fixture.Path);
        PumpUntil(() => navigation.IsCompleted, TimeSpan.FromSeconds(15));
        Assert.True(navigation.GetAwaiter().GetResult());

        var tree = (ListView)window.FindName("SpaceTree");
        var map = (TreemapPanel)window.FindName("SpaceMap");
        var selector = (ComboBox)window.FindName("SizeBasisSelector");
        var total = (TextBlock)window.FindName("ScannedSizeText");
        var rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
        var scan = rows.Scan;
        int link = scan.FindEntry(online)!.Value;
        Assert.Equal(0, selector.SelectedIndex);
        Assert.Equal(SizeText.Format(scan.AllocatedBytes), total.Text);
        Assert.DoesNotContain(map.Buttons, button => button.DataContext is SpaceContribution part && part.Link == link);

        int selectedLink = scan.FindEntry(folder)!.Value;
        Assert.True((bool)typeof(MainWindow).GetMethod("SelectContextEntry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, selectedLink])!);
        selector.SelectedIndex = 1;
        PumpUntil(() => tree.ItemsSource is ExplorerRows { UseAllocation: false } && selector.IsEnabled, TimeSpan.FromSeconds(10));
        Assert.Same(scan, ((ExplorerRows)tree.ItemsSource).Scan);
        Assert.Equal(selectedLink, Assert.IsType<ExplorerRow>(tree.SelectedItem).Entry.LinkIndex);
        var logicalRows = (ExplorerRows)tree.ItemsSource;
        Assert.Equal(SizeText.Format(logicalBytes), logicalRows.Row(logicalRows.Find(link)).PrimarySize);
        Assert.Equal(SizeText.Format(scan.LogicalBytes), total.Text);
        Assert.Contains(map.Buttons, button => button.DataContext is SpaceContribution part && part.Link == link && part.Bytes == logicalBytes);
        Assert.Equal("Area = file size", ((TextBlock)window.FindName("MapBasis")).Text);
        Assert.Equal(0, CloudFiles.Inspect(online)!.LocalBytes);
        foreach (var size in new[] { (1220, 940), (1020, 820) })
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"explorer-logical-{size.Item1}.png"), size.Item1, size.Item2);

        var recheck = window.RecheckFolderAsync(fixture.Path);
        PumpUntil(() => recheck.IsCompleted, TimeSpan.FromSeconds(15));
        recheck.GetAwaiter().GetResult();
        var refreshed = Assert.IsType<ExplorerRows>(tree.ItemsSource);
        Assert.NotNull(refreshed.Scan.LastRecheckUtc);
        Assert.False(refreshed.UseAllocation);
        Assert.Equal("File size · Partially refreshed", ((TextBlock)window.FindName("ScannedSizeCaption")).Text);

        selector.SelectedIndex = 0;
        PumpUntil(() => tree.ItemsSource is ExplorerRows { UseAllocation: true } && selector.IsEnabled, TimeSpan.FromSeconds(10));
        Assert.Equal(folder, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
        Assert.Equal(SizeText.Format(refreshed.Scan.AllocatedBytes), total.Text);
    }

    private static void CheckCloudDrives(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string root = Directory.CreateDirectory(Path.Join(fixture.Path, "Proton Drive")).FullName;
        string folder = Directory.CreateDirectory(Path.Join(root, "Holiday photos")).FullName;
        File.WriteAllBytes(Path.Join(folder, "pending.jpg"), new byte[1024]);
        string file = Path.Join(root, "Lake panorama — original.jpg");
        File.WriteAllBytes(file, new byte[4096]);
        using var registration = CloudFilesTests.RegisterSyncedFixture(root, file);
        var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow).GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var entry = Assert.Single(runtime.Entries, entry => entry.Id == "space-maid.cloud");
        Assert.Equal("Loaded", entry.Status); Assert.Null(entry.Error);
        var view = Assert.IsAssignableFrom<UserControl>(entry.View);
        Assert.True(window.OpenExtension(entry.Id));
        Task Invoke(string method, params object[] args) => (Task)view.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, args)!;
        void Finish(Task task) { PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10)); task.GetAwaiter().GetResult(); }
        var list = (DataGrid)view.FindName("FileList");
        var free = (Button)view.FindName("FreeButton");
        var measure = (Button)view.FindName("MeasureButton");
        var cleanup = (Button)view.FindName("CloudCleanupButton");
        var providerSite = (Button)view.FindName("ProviderButton");
        var search = (TextBox)view.FindName("SearchBox");
        Assert.Empty(list.Items); Assert.False(free.IsEnabled); Assert.False(measure.IsEnabled);
        Assert.False(cleanup.IsEnabled); Assert.False(providerSite.IsVisible);
        Finish(Invoke("AddRootAsync", "Proton Drive", root));
        Assert.Equal(root, ((TextBox)view.FindName("FolderPath")).Text);
        Assert.Equal(2, list.Items.Count);
        list.SelectedIndex = 1; Assert.True(free.IsEnabled);
        search.Text = "no-such-file"; Assert.Empty(list.Items);
        search.Text = ""; Assert.Equal(2, list.Items.Count); list.SelectedIndex = 1;
        Assert.True(measure.IsEnabled);
        measure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var status = (TextBlock)view.FindName("StatusText");
        PumpUntil(() => status.Text.Contains("5 KiB total size"), TimeSpan.FromSeconds(10));
        Assert.Equal(1, list.SelectedIndex);
        Assert.True(free.IsEnabled);
        Assert.Equal("Allocated", list.Columns[2].Header);
        Assert.Equal("1 KiB", list.Items[0].GetType().GetProperty("SizeText")!.GetValue(list.Items[0]));
        Assert.True(cleanup.IsEnabled); Assert.False(providerSite.IsVisible);
        cleanup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => status.Text.StartsWith("Cloud cleanup review", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal(file, ((CloudDirectoryEntry)list.SelectedItem.GetType().GetProperty("Entry")!.GetValue(list.SelectedItem)!).Path);
        Assert.Equal("4 KiB", list.Items[0].GetType().GetProperty("SizeText")!.GetValue(list.Items[0]));
        Assert.Equal("Open Proton Drive", providerSite.Content);
        Assert.True(providerSite.IsVisible && providerSite.IsEnabled);
        Assert.True(free.IsEnabled);
        Assert.Equal(4096, new FileInfo(file).Length);
        Assert.NotEqual(2, CloudFiles.Inspect(file)!.PinState);

#pragma warning disable WPF0001
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            foreach (var size in new[] { (1220, 940), (1020, 820) })
            {
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cloud-drives-{theme}-{size.Item1}.png"), size.Item1, size.Item2);
                Assert.True(list.ActualHeight > 350); Assert.True(list.ActualWidth > 550);
                Assert.True(free.IsVisible && providerSite.IsVisible);
            }
        }
#pragma warning restore WPF0001
        var explore = (Button)view.FindName("ExploreButton");
        Assert.Equal("Explore logical size", explore.Content);
        explore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var sizeBasis = (ComboBox)window.FindName("SizeBasisSelector");
        PumpUntil(() => sizeBasis.SelectedIndex == 1 && sizeBasis.IsEnabled, TimeSpan.FromSeconds(15));
        var explorerTree = (ListView)window.FindName("SpaceTree");
        Assert.Equal(root, Assert.IsType<ExplorerRow>(explorerTree.SelectedItem).Path);
        sizeBasis.SelectedIndex = 0;
        PumpUntil(() => explorerTree.ItemsSource is ExplorerRows { UseAllocation: true } && sizeBasis.IsEnabled, TimeSpan.FromSeconds(10));
        Assert.True(window.OpenExtension(entry.Id));
        void AnswerConfirmation(bool confirm)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                if (app.Windows.OfType<OnlineOnlyWindow>().FirstOrDefault(candidate => candidate.IsVisible
                    && ((TextBox)candidate.FindName("TargetPath")).Text == file) is not { } dialog) return;
                timer.Stop();
                Assert.Equal(file, ((TextBox)dialog.FindName("TargetPath")).Text);
                dialog.DialogResult = confirm;
            };
            timer.Start();
            try { Finish(Invoke("FreeSelectedAsync")); }
            finally { timer.Stop(); }
        }
        AnswerConfirmation(false);
        Assert.NotEqual(2, CloudFiles.Inspect(file)!.PinState); Assert.Equal(2, list.Items.Count);
        Assert.Contains("cancelled", ((TextBlock)view.FindName("StatusText")).Text);
        AnswerConfirmation(true);
        Assert.Equal(2, CloudFiles.Inspect(file)!.PinState);
        Assert.Equal(4096, new FileInfo(file).Length); Assert.Equal(4096, CloudFiles.Inspect(file)!.LocalBytes);
        Assert.Empty(list.Items); Assert.False(free.IsEnabled);
        Assert.False(providerSite.IsVisible);
        Assert.Contains("requests accepted", ((TextBlock)view.FindName("StatusText")).Text);
        Finish(Invoke("BrowseAsync", root));
        list.SelectedIndex = 0;
        ((Button)view.FindName("OpenFolderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((TextBox)view.FindName("FolderPath")).Text == folder, TimeSpan.FromSeconds(5));
        Assert.Single(list.Items); list.SelectedIndex = 0; Assert.False(free.IsEnabled);
        Assert.True(((Button)view.FindName("ExplorerButton")).IsEnabled);
        ((Button)view.FindName("UpButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((TextBox)view.FindName("FolderPath")).Text == root, TimeSpan.FromSeconds(5));
        Assert.NotNull(list.SelectedItem);
        Assert.Equal(folder, ((CloudDirectoryEntry)list.SelectedItem.GetType().GetProperty("Entry")!.GetValue(list.SelectedItem)!).Path);
        ((Button)view.FindName("OpenFolderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((TextBox)view.FindName("FolderPath")).Text == folder, TimeSpan.FromSeconds(5));
        search.Text = "pending";
        Assert.True(window.OpenExtension("space-maid.apps")); Assert.True(window.OpenExtension(entry.Id));
        Assert.Equal("pending", search.Text); Assert.Equal(folder, ((TextBox)view.FindName("FolderPath")).Text);
        search.Text = "";
        // An unavailable saved root remains a recoverable error rather than an empty success.
        Finish(Invoke("AddRootAsync", "Google Drive", Path.Join(fixture.Path, "Disconnected Google Drive")));
        Assert.Contains("Could not finish", ((TextBlock)view.FindName("StatusText")).Text);
        Assert.False(free.IsEnabled);
        foreach (string provider in new[] { "OneDrive", "Dropbox" })
        {
            string location = Directory.CreateDirectory(Path.Join(fixture.Path, provider)).FullName;
            File.WriteAllText(Path.Join(location, "original.txt"), "keep");
            Finish(Invoke("AddRootAsync", provider, location));
            Assert.Single(list.Items); list.SelectedIndex = 0; Assert.False(free.IsEnabled);
            Assert.Equal("keep", File.ReadAllText(Path.Join(location, "original.txt")));
            if (provider == "OneDrive")
            {
                cleanup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => providerSite.IsEnabled && providerSite.IsVisible, TimeSpan.FromSeconds(10));
                providerSite.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var menu = (ContextMenu)view.Resources["OneDriveWebsiteMenu"];
                Assert.True(menu.IsOpen);
                Assert.Equal(2, menu.Items.Count);
                menu.IsOpen = false;
            }
        }
        Assert.True(window.OpenExtension("space-maid.apps"));
    }
}
