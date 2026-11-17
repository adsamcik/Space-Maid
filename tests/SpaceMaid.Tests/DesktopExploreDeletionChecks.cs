using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SpaceMaid.App;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckExploreDeletion(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string large = Directory.CreateDirectory(Path.Join(fixture.Path, "Camera originals")).FullName;
        string sparse = Path.Join(large, "owned-sparse.bin");
        long length = SilentRecyclingTests.CreateOversizedFixtureFile(sparse, "fixture"u8.ToArray());
        string direct = Directory.CreateDirectory(Path.Join(fixture.Path, "Old exports")).FullName;
        File.WriteAllText(Path.Join(direct, "nested.txt"), "only delete this folder");
        string keep = Path.Join(fixture.Path, "keep.txt");
        File.WriteAllText(keep, "keep");
        string largeFile = Path.Join(fixture.Path, "Camera archive.zip");
        long fileLength = SilentRecyclingTests.CreateOversizedFixtureFile(largeFile, "archive"u8.ToArray());
        string recycleFile = Path.Join(fixture.Path, "draft.txt");
        File.WriteAllText(recycleFile, "recycle this file");
        string directFile = Path.Join(fixture.Path, "old-export.txt");
        File.WriteAllText(directFile, "delete this file");
        string enteredPath = Path.Join(Path.GetDirectoryName(fixture.Path), Path.GetFileName(fixture.Path).ToUpperInvariant());
        var scan = window.Operations.MeasureFolderAsync(enteredPath).GetAwaiter().GetResult();
        Assert.Equal(enteredPath, scan.RootPath);
        ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        var initialized = window.InitializeExplorerAsync(scan, [], default);
        PumpUntil(() => initialized.IsCompleted, TimeSpan.FromSeconds(10));
        initialized.GetAwaiter().GetResult();
        var tree = (ListView)window.FindName("SpaceTree");
        var status = (TextBlock)window.FindName("StatusText");
        var scanButton = (Button)window.FindName("ScanButton");

        Select(large);
        RespondToDialogs(large, approve: false, expectRecycle: true, () => _ = window.DeleteSelectedEntryAsync());
        Assert.Equal(length, new FileInfo(sparse).Length);
        Assert.True(status.Text is "Folder unchanged." or "Recycling stopped. Some contents may already have moved. Recheck the folder.",
            status.Text);
        Assert.False(Assert.IsType<ExplorerRow>(tree.SelectedItem).IsRemoved);

        RespondToDialogs(large, approve: true, expectRecycle: true, () => _ = window.DeleteSelectedEntryAsync());
        Assert.False(Directory.Exists(large));
        Assert.Contains("Permanently deleted Camera originals.", status.Text);
        Assert.DoesNotContain("OUT OF DATE", ((TextBlock)window.FindName("TreeNote")).Text);
        Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(large));
        Assert.Equal("keep", File.ReadAllText(keep));

        Select(direct);
        RespondToDialogs(direct, approve: true, expectRecycle: false, MenuAction("Delete permanently…"));
        Assert.False(Directory.Exists(direct));
        Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(direct));

        Select(largeFile);
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tree), Environment.TickCount, Key.Delete)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        RespondToDialogs(largeFile, approve: false, expectRecycle: true, () => tree.RaiseEvent(key));
        Assert.True(key.Handled);
        Assert.Equal(fileLength, new FileInfo(largeFile).Length);
        Assert.Equal(Path.Join(enteredPath, Path.GetFileName(largeFile)), Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
        RespondToDialogs(largeFile, approve: true, expectRecycle: true, MenuAction("Move to Recycle Bin"));
        Assert.False(File.Exists(largeFile));
        Assert.Contains("Permanently deleted Camera archive.zip.", status.Text);
        Assert.DoesNotContain("OUT OF DATE", ((TextBlock)window.FindName("TreeNote")).Text);
        Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(largeFile));

        Select(directFile);
        RespondToDialogs(directFile, approve: true, expectRecycle: false, MenuAction("Delete permanently…"));
        Assert.False(File.Exists(directFile));
        Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(directFile));

        Select(recycleFile);
        Task<RecycleResult?>? recycle = null;
        RespondToDialogs(recycleFile, approve: true, expectRecycle: true, () => recycle = window.DeleteSelectedEntryAsync(),
            expectPermanent: false);
        var recycled = Assert.IsType<string>(recycle!.GetAwaiter().GetResult()?.RecycledPath);
        try
        {
            Assert.False(File.Exists(recycleFile));
            Assert.Equal("recycle this file", File.ReadAllText(recycled));
            Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(recycleFile));
            Assert.Contains("Moved draft.txt to the Recycle Bin.", status.Text);
        }
        finally { DeletionTests.RestoreFixture(recycled, recycleFile); }
        Assert.Equal("keep", File.ReadAllText(keep));

        Action MenuAction(string header)
        {
            RightClick(window, tree, keyboard: true);
            PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
            var command = tree.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header));
            Assert.True(command.IsEnabled);
            tree.ContextMenu.IsOpen = false;
            return () => command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }

        void Select(string target)
        {
            var current = Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan;
            var navigation = window.NavigateToEntryAsync(current.FindEntry(target)!.Value);
            PumpUntil(() => navigation.IsCompleted, TimeSpan.FromSeconds(10));
            navigation.GetAwaiter().GetResult();
            Assert.Equal(Path.Join(enteredPath, Path.GetFileName(target)), Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
        }

        void RespondToDialogs(string path, bool approve, bool expectRecycle, Action start, bool expectPermanent = true)
        {
            bool isDirectory = Directory.Exists(path);
            string kind = isDirectory ? "folder" : "file";
            Exception? failure = null;
            int recycleDialogs = 0, permanentDialogs = 0;
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
            {
                var dialog = app.Windows.OfType<RecycleEntryWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (dialog is null) return;
                try
                {
                    Assert.Equal(Path.Join(enteredPath, Path.GetFileName(path)), ((TextBox)dialog.FindName("TargetPath")).Text);
                    Assert.True(isDirectory ? Directory.Exists(path) : File.Exists(path));
                    var button = (Button)dialog.FindName("RecycleButton");
                    if (Equals(button.Content, "Move to Recycle Bin"))
                    {
                        recycleDialogs++;
                        Assert.True(expectRecycle);
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        return;
                    }
                    permanentDialogs++;
                    Assert.Equal($"Delete this {kind} permanently?", ((TextBlock)dialog.FindName("Heading")).Text);
                    var cancel = (Button)dialog.FindName("KeepButton");
                    Assert.True(cancel.IsDefault);
                    Assert.True(cancel.IsCancel);
                    Assert.False(button.IsDefault);
                    var warning = ((TextBlock)dialog.FindName("Consequence")).Text;
                    Assert.Equal(expectRecycle, warning.Contains($"Windows can’t move this {kind} to the Recycle Bin."));
                    Assert.Contains("This can’t be undone", warning);
                    if (expectRecycle && !approve)
                        RenderMenu((FrameworkElement)dialog.Content, Path.Join(artifactRoot, "artifacts", "ui", $"permanent-{kind}-deletion.png"), dialog.Background);
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
            if (permanentDialogs != (expectPermanent ? 1 : 0))
            {
                string? log = ((Button)window.FindName("OpenLogButton")).ToolTip as string;
                string details = log is not null ? File.ReadAllText(log) : "";
                Assert.Fail($"Unexpected number of permanent deletion warnings ({permanentDialogs}). Status: {status.Text}\n{details}");
            }
        }
    }
}
