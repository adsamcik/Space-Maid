using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.App;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckPermanentFolderDeletion(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string large = Directory.CreateDirectory(Path.Join(fixture.Path, "Camera originals")).FullName;
        string sparse = Path.Join(large, "owned-sparse.bin");
        long length = SilentRecyclingTests.CreateOversizedFixtureFile(sparse, "fixture"u8.ToArray());
        string direct = Directory.CreateDirectory(Path.Join(fixture.Path, "Old exports")).FullName;
        File.WriteAllText(Path.Join(direct, "nested.txt"), "only delete this folder");
        string keep = Path.Join(fixture.Path, "keep.txt");
        File.WriteAllText(keep, "keep");
        var scan = window.Operations.MeasureFolderAsync(fixture.Path).GetAwaiter().GetResult();
        ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        var initialized = window.InitializeExplorerAsync(scan, [], default);
        PumpUntil(() => initialized.IsCompleted, TimeSpan.FromSeconds(10));
        initialized.GetAwaiter().GetResult();
        var tree = (ListView)window.FindName("SpaceTree");
        var status = (TextBlock)window.FindName("StatusText");
        var scanButton = (Button)window.FindName("ScanButton");

        Select(large);
        RespondToDialogs(approve: false, expectRecycle: true, () => _ = window.DeleteSelectedFolderAsync());
        Assert.Equal(length, new FileInfo(sparse).Length);
        Assert.True(status.Text is "Folder unchanged." or "Recycling stopped. Some contents may already have moved. Recheck the folder.",
            status.Text);
        Assert.False(Assert.IsType<ExplorerRow>(tree.SelectedItem).IsRemoved);

        RespondToDialogs(approve: true, expectRecycle: true, () => _ = window.DeleteSelectedFolderAsync());
        Assert.False(Directory.Exists(large));
        Assert.Contains("Permanently deleted Camera originals.", status.Text);
        Assert.DoesNotContain("OUT OF DATE", ((TextBlock)window.FindName("TreeNote")).Text);
        Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(large));
        Assert.Equal("keep", File.ReadAllText(keep));

        Select(direct);
        RightClick(window, tree, keyboard: true);
        PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
        var command = tree.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Delete permanently…"));
        Assert.True(command.IsEnabled);
        tree.ContextMenu.IsOpen = false;
        RespondToDialogs(approve: true, expectRecycle: false, () => command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
        Assert.False(Directory.Exists(direct));
        Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(direct));
        Assert.Equal("keep", File.ReadAllText(keep));

        void Select(string target)
        {
            var current = Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan;
            var navigation = window.NavigateToEntryAsync(current.FindEntry(target)!.Value);
            PumpUntil(() => navigation.IsCompleted, TimeSpan.FromSeconds(10));
            navigation.GetAwaiter().GetResult();
            Assert.Equal(target, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
        }

        void RespondToDialogs(bool approve, bool expectRecycle, Action start)
        {
            Exception? failure = null;
            int recycleDialogs = 0, permanentDialogs = 0;
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
            {
                var dialog = app.Windows.OfType<RecycleFolderWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (dialog is null) return;
                try
                {
                    var button = (Button)dialog.FindName("RecycleButton");
                    if (Equals(button.Content, "Move to Recycle Bin"))
                    {
                        recycleDialogs++;
                        Assert.True(expectRecycle);
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        return;
                    }
                    permanentDialogs++;
                    string path = expectRecycle ? large : direct;
                    Assert.Equal(path, ((TextBox)dialog.FindName("TargetPath")).Text);
                    Assert.True(Directory.Exists(path));
                    var cancel = (Button)dialog.FindName("KeepButton");
                    Assert.True(cancel.IsDefault);
                    Assert.True(cancel.IsCancel);
                    Assert.False(button.IsDefault);
                    var warning = ((TextBlock)dialog.FindName("Consequence")).Text;
                    Assert.Equal(expectRecycle, warning.Contains("Windows can’t move"));
                    Assert.Contains("This can’t be undone", warning);
                    if (expectRecycle && !approve)
                        RenderMenu((FrameworkElement)dialog.Content, Path.Join(artifactRoot, "artifacts", "ui", "permanent-folder-deletion.png"), dialog.Background);
                    if (approve) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
                catch (Exception ex) { failure = ex; dialog.Close(); }
            }, Dispatcher.CurrentDispatcher);
            try
            {
                start();
                PumpUntil(() => scanButton.IsEnabled, TimeSpan.FromSeconds(12));
            }
            finally { timer.Stop(); }
            if (failure is not null) throw failure;
            Assert.Equal(expectRecycle ? 1 : 0, recycleDialogs);
            Assert.True(permanentDialogs == 1, $"Expected one permanent deletion warning; got {permanentDialogs}. Status: {status.Text}");
        }
    }
}
