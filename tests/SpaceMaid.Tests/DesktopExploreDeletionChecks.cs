using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
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
        var queueButton = (Button)window.FindName("DeletionQueueButton");

        Select(large);
        RunDeletion(large, approve: null, binUnavailable: false, () => _ = window.DeleteSelectedEntryAsync());
        var queue = OpenQueue();
        Assert.Equal(EntryDeletionState.RecycleRefused, Job(large).State);
        RunDeletion(large, approve: false, binUnavailable: true, JobAction(large));
        Assert.Equal(length, new FileInfo(sparse).Length);
        Assert.Equal(EntryDeletionState.RecycleRefused, Job(large).State);
        Assert.False(Assert.IsType<ExplorerRow>(tree.SelectedItem).IsRemoved);

        RunDeletion(large, approve: true, binUnavailable: true, JobAction(large));
        Assert.False(Directory.Exists(large));
        Assert.Contains("Permanently deleted Camera originals.", status.Text);
        Assert.DoesNotContain("OUT OF DATE", ((TextBlock)window.FindName("TreeNote")).Text);
        Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(large));
        Assert.Equal("keep", File.ReadAllText(keep));

        Select(direct);
        RunDeletion(direct, approve: true, binUnavailable: false, MenuAction("Delete permanently…"));
        Assert.False(Directory.Exists(direct));
        Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(direct));

        Select(largeFile);
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tree), Environment.TickCount, Key.Delete)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        RunDeletion(largeFile, approve: null, binUnavailable: false, () => tree.RaiseEvent(key));
        Assert.True(key.Handled);
        Assert.Equal(fileLength, new FileInfo(largeFile).Length);
        Assert.Equal(Path.Join(enteredPath, Path.GetFileName(largeFile)), Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
        Assert.Equal(EntryDeletionState.RecycleRefused, Job(largeFile).State);
        RunDeletion(largeFile, approve: true, binUnavailable: true, JobAction(largeFile));
        Assert.False(File.Exists(largeFile));
        Assert.Contains("Permanently deleted Camera archive.zip.", status.Text);
        Assert.DoesNotContain("OUT OF DATE", ((TextBlock)window.FindName("TreeNote")).Text);
        Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(largeFile));

        Select(directFile);
        RunDeletion(directFile, approve: true, binUnavailable: false, MenuAction("Delete permanently…"));
        Assert.False(File.Exists(directFile));
        Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(directFile));

        Select(recycleFile);
        var header = (GridViewColumnHeader)window.FindName("NameHeader");
        var headerDelete = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(header), Environment.TickCount, Key.Delete)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        header.RaiseEvent(headerDelete);
        Assert.False(headerDelete.Handled);
        Assert.True(File.Exists(recycleFile));
        Task<RecycleResult?>? recycle = null;
        RunDeletion(recycleFile, approve: null, binUnavailable: false, () => recycle = window.DeleteSelectedEntryAsync());
        var recycled = Assert.IsType<string>(recycle!.GetAwaiter().GetResult()?.RecycledPath);
        try
        {
            Assert.False(File.Exists(recycleFile));
            Assert.Equal("recycle this file", File.ReadAllText(recycled));
            Assert.Null(Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan.FindEntry(recycleFile));
            Assert.Contains("Moved draft.txt to the Recycle Bin.", status.Text);
            Assert.Contains("You can restore it", status.Text);
        }
        finally { DeletionTests.RestoreFixture(recycled, recycleFile); }
        Assert.Equal("keep", File.ReadAllText(keep));
        queue.Close();
        CheckMixedQueue();

        void CheckMixedQueue()
        {
            string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "Batch folder")).FullName;
            string child = Path.Join(folder, "child.txt");
            string forever = Path.Join(fixture.Path, "batch-delete.txt");
            string cancelled = Path.Join(fixture.Path, "batch-cancel.txt");
            string replaced = Path.Join(fixture.Path, "batch-replaced.txt");
            string stopped = Path.Join(fixture.Path, "batch-stop.txt");
            foreach (string path in new[] { child, forever, cancelled, replaced, stopped }) File.WriteAllText(path, "owned");
            var refreshed = window.InitializeExplorerAsync(window.Operations.ScanAsync(enteredPath).GetAwaiter().GetResult(), [], default);
            PumpUntil(() => refreshed.IsCompleted, TimeSpan.FromSeconds(10));
            refreshed.GetAwaiter().GetResult();

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = CleanupMutationLease.RunAsync(async () => { entered.SetResult(); await release.Task; });
            Task<RecycleResult?>? recycleFolder = null;
            try
            {
                PumpUntil(() => entered.Task.IsCompleted, TimeSpan.FromSeconds(5));
                queue = OpenQueue();
                Select(stopped);
                _ = window.DeleteSelectedEntryAsync();
                Assert.NotEmpty(((TextBlock)window.FindName("SelectedDeletionStatus")).Text);
                PumpUntil(() => HasState(stopped, EntryDeletionState.Running), TimeSpan.FromSeconds(5));
                AssertDeletionFeedback("Moving to Recycle Bin…", "Recycling…");
                JobAction(stopped)();
                PumpUntil(() => HasState(stopped, EntryDeletionState.Cancelled), TimeSpan.FromSeconds(5));
                AssertDeletionFeedback("");
                Assert.Equal("owned", File.ReadAllText(stopped));
                Select(folder);
                recycleFolder = window.DeleteSelectedEntryAsync();
                PumpUntil(() => HasState(folder, EntryDeletionState.Running), TimeSpan.FromSeconds(5));
                Assert.True(scanButton.IsEnabled);
                Assert.True(tree.IsEnabled);
                Select(child);
                Assert.Null(window.DeleteSelectedEntryAsync().GetAwaiter().GetResult());
                Assert.Contains("covered by the queued folder", status.Text);

                Select(replaced);
                _ = window.DeleteSelectedEntryAsync();
                PumpUntil(() => HasState(replaced, EntryDeletionState.Queued), TimeSpan.FromSeconds(5));
                AssertDeletionFeedback("Recycle Bin move queued", "Recycle queued");
                File.Move(replaced, replaced + ".original");
                File.WriteAllText(replaced, "replacement");

                Select(forever);
                RunDeletion(forever, approve: true, binUnavailable: false, () => _ = window.DeleteSelectedEntryAsync(permanent: true),
                    () => HasState(forever, EntryDeletionState.Queued));
                AssertDeletionFeedback("Permanent deletion queued", "Delete queued");
                Assert.True(File.Exists(forever));
                Assert.Null(window.DeleteSelectedEntryAsync(permanent: true).GetAwaiter().GetResult());
                Assert.Contains("already queued for permanent deletion", status.Text);
                Select(cancelled);
                _ = window.DeleteSelectedEntryAsync();
                PumpUntil(() => HasState(cancelled, EntryDeletionState.Queued), TimeSpan.FromSeconds(5));
                Assert.Same(Job(folder), ((ListBox)queue.FindName("Jobs")).Items[0]);
                RenderMenu((FrameworkElement)queue.Content, Path.Join(artifactRoot, "artifacts", "ui", "deletion-queue.png"), queue.Background);
                RenderMenu((FrameworkElement)window.FindName("ExplorePanel"),
                    Path.Join(artifactRoot, "artifacts", "ui", "explore-deletion-feedback.png"), window.Background);
                JobAction(cancelled)();
                Assert.Equal(EntryDeletionState.Cancelled, Job(cancelled).State);
                AssertDeletionFeedback("");
                var rescan = window.NavigatePathAsync(enteredPath, forceScan: true);
                PumpUntil(() => rescan.IsCompleted, TimeSpan.FromSeconds(15));
                Assert.True(rescan.GetAwaiter().GetResult());
                Select(forever);
                AssertDeletionFeedback("Permanent deletion queued", "Delete queued");
                Select(replaced);
                AssertDeletionFeedback("");
                Select(keep);
                AssertDeletionFeedback("");
                Assert.True(scanButton.IsEnabled);
            }
            finally
            {
                release.TrySetResult();
                PumpUntil(() => window.ExploreDeletionsIdle, TimeSpan.FromSeconds(15));
                blocker.GetAwaiter().GetResult();
            }
            var recycledFolder = Assert.IsType<string>(recycleFolder!.GetAwaiter().GetResult()?.RecycledPath);
            try
            {
                Assert.Equal("owned", File.ReadAllText(Path.Join(recycledFolder, "child.txt")));
                Assert.False(File.Exists(forever));
                Assert.Equal(EntryDeletionState.Completed, Job(forever).State);
                Assert.Equal(EntryDeletionState.Failed, Job(replaced).State);
                Assert.Equal("replacement", File.ReadAllText(replaced));
                Assert.Equal("owned", File.ReadAllText(cancelled));
                Assert.Equal(keep, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path, ignoreCase: true);
                var current = Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan;
                Assert.Null(current.FindEntry(folder));
                Assert.Null(current.FindEntry(forever));
                Assert.NotNull(current.FindEntry(cancelled));
            }
            finally { DeletionTests.RestoreFixture(recycledFolder, folder); queue.Close(); }
        }

        void AssertDeletionFeedback(string expected, string? label = null)
        {
            window.UpdateLayout();
            var row = Assert.IsType<ExplorerRow>(tree.SelectedItem);
            Assert.False(row.IsRemoved);
            Assert.Equal(expected, ((TextBlock)window.FindName("SelectedDeletionStatus")).Text);
            var item = Assert.IsType<ListViewItem>(tree.ItemContainerGenerator.ContainerFromItem(row));
            Assert.Equal(expected, AutomationProperties.GetItemStatus(item));
            if (label is not null)
                Assert.Contains(Descendants(item).OfType<TextBlock>(), text => text.IsVisible && text.Text == label);
        }

        DeletionQueueWindow OpenQueue()
        {
            queueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return app.Windows.OfType<DeletionQueueWindow>().Single();
        }

        EntryDeletionJob Job(string path) => ((ListBox)queue.FindName("Jobs")).Items.OfType<EntryDeletionJob>()
            .First(job => string.Equals(job.Path, path, StringComparison.OrdinalIgnoreCase));

        bool HasState(string path, EntryDeletionState state) => ((ListBox)queue.FindName("Jobs")).Items.OfType<EntryDeletionJob>()
            .Any(job => string.Equals(job.Path, path, StringComparison.OrdinalIgnoreCase) && job.State == state);

        Action JobAction(string path)
        {
            var list = (ListBox)queue.FindName("Jobs");
            var job = Job(path);
            list.ScrollIntoView(job);
            queue.UpdateLayout();
            var row = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(job));
            var button = Descendants(row).OfType<Button>().Single();
            return () => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

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
            Assert.Equal(Path.Join(enteredPath, Path.GetRelativePath(fixture.Path, target)),
                Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
        }

        void RunDeletion(string path, bool? approve, bool binUnavailable, Action start, Func<bool>? finished = null)
        {
            bool isDirectory = Directory.Exists(path);
            string kind = isDirectory ? "folder" : "file";
            Exception? failure = null;
            int warnings = 0;
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
            {
                var dialog = app.Windows.OfType<PermanentDeleteWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (dialog is null) return;
                warnings++;
                try
                {
                    Assert.Equal(Path.Join(enteredPath, Path.GetFileName(path)), ((TextBox)dialog.FindName("TargetPath")).Text);
                    Assert.True(isDirectory ? Directory.Exists(path) : File.Exists(path));
                    Assert.Equal($"Delete this {kind} permanently?", ((TextBlock)dialog.FindName("Heading")).Text);
                    var cancel = (Button)dialog.FindName("CancelButton");
                    var button = (Button)dialog.FindName("DeleteButton");
                    Assert.True(cancel.IsDefault);
                    Assert.True(cancel.IsCancel);
                    Assert.False(button.IsDefault);
                    var warning = (TextBlock)dialog.FindName("RecycleWarning");
                    Assert.Equal(binUnavailable ? Visibility.Visible : Visibility.Collapsed, warning.Visibility);
                    if (binUnavailable) Assert.Contains($"Windows can’t move this {kind} to the Recycle Bin.", warning.Text);
                    Assert.Contains("This can’t be undone", ((TextBlock)dialog.FindName("Consequence")).Text);
                    if (binUnavailable && approve == false)
                        RenderMenu((FrameworkElement)dialog.Content, Path.Join(artifactRoot, "artifacts", "ui", $"permanent-{kind}-deletion.png"), dialog.Background);
                    if (approve == true) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
                catch (Exception ex) { failure = ex; dialog.Close(); }
            }, Dispatcher.CurrentDispatcher);
            try
            {
                start();
                PumpUntil(finished ?? (() => window.ExploreDeletionsIdle), TimeSpan.FromSeconds(12));
            }
            finally { timer.Stop(); }
            if (failure is not null) throw failure;
            if (warnings != (approve.HasValue ? 1 : 0))
            {
                Assert.Fail($"Unexpected number of permanent deletion warnings ({warnings}). Status: {status.Text}");
            }
        }
    }
}
