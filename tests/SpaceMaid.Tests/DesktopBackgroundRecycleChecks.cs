using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckAdministratorRecycle(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        var parent = new DirectoryInfo(fixture.Path);
        var folder = parent.CreateSubdirectory("permissions-fixture");
        var second = parent.CreateSubdirectory("second-permissions-fixture");
        string payload = Path.Join(folder.FullName, "payload.txt");
        const string contents = "Owned permission retry fixture";
        File.WriteAllText(payload, contents);
        File.WriteAllText(Path.Join(second.FullName, "payload.txt"), contents);
        using (var firstPermissions = new RecyclePermissionFixture(folder.FullName))
        using (var secondPermissions = new RecyclePermissionFixture(second.FullName))
        {
            window.SetScanTarget(fixture.Path);
            ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
            Assert.True(Finish(window.ScanCurrentPathAsync()));
            var rows = Assert.IsType<ExplorerRows>(((ListBox)window.FindName("SpaceTree")).ItemsSource);
            FinishTask(window.NavigateToEntryAsync(rows.Scan.FindEntry(folder.FullName)!.Value));

            var retry = (Button)window.FindName("RetryRecycleButton");
            var status = (TextBlock)window.FindName("RecycleStatusText");
            Assert.False(retry.IsVisible);
            Assert.Null(Review(window.DeleteSelectedFolderAsync, approve: true, administrator: false));
            Assert.True(retry.IsVisible, status.ToolTip as string);
            Assert.Equal(folder.FullName, retry.ToolTip);
            Assert.Contains("Could not recycle", status.Text);
            Assert.Equal(contents, File.ReadAllText(payload));
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "administrator-recycle-retry.png"), 1020, 790);

            Assert.Null(Review(window.RetryRecycleWithAdministratorAsync, approve: false, administrator: true));
            Assert.True(retry.IsVisible);
            Assert.Equal(contents, File.ReadAllText(payload));

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = CleanupMutationLease.RunAsync(async () => { entered.SetResult(); await release.Task; });
            try
            {
                FinishTask(entered.Task);
                var result = Review(window.RetryRecycleWithAdministratorAsync, approve: true, administrator: true, cancelQueued: true);
                Assert.Equal(new RecycleResult(true, null), result);
                Assert.Equal("Administrator retry cancelled", status.Text);
                Assert.Equal(contents, File.ReadAllText(payload));
            }
            finally { release.TrySetResult(); FinishTask(held); }

            Assert.Null(Review(window.DeleteSelectedFolderAsync, approve: true, administrator: false));
            Assert.True(retry.IsVisible, status.ToolTip as string);
            // A new directory at the original path must never be handed to the
            // elevated Shell retry, even though the user approved that path.
            secondPermissions.Dispose();
            firstPermissions.Dispose();
            string original = Path.Join(parent.FullName, "original");
            Directory.Move(folder.FullName, original);
            Directory.CreateDirectory(folder.FullName);
            File.WriteAllText(payload, "Replacement");
            Assert.Null(Review(window.RetryRecycleWithAdministratorAsync, approve: true, administrator: true));
            Assert.False(retry.IsVisible);
            Assert.Contains("replaced since confirmation", status.ToolTip as string);
            Assert.Equal("Replacement", File.ReadAllText(payload));
            Assert.Equal(contents, File.ReadAllText(Path.Join(original, "payload.txt")));

            var reviewTarget = FolderDeletionTarget.CaptureCurrent(folder.FullName, 11);
            var repairTarget = window.Operations.Files.CaptureRecyclePermissionTarget(reviewTarget.ExportCapturedIdentity(), payload);
            var repairDialog = new RecycleFolderWindow(reviewTarget, repairTarget) { Owner = window };
            repairDialog.ContentRendered += (_, _) => repairDialog.Close();
            Render(repairDialog, Path.Join(artifactRoot, "artifacts", "ui", "recycle-permission-repair.png"), 520, 340);
            Assert.Equal(payload, ((TextBox)repairDialog.FindName("TargetPath")).Text);
            Assert.Contains(folder.FullName, ((TextBlock)repairDialog.FindName("SelectionNote")).Text);
            Assert.Equal("Replacement", File.ReadAllText(payload));

            using var replacedPermissions = new RecyclePermissionFixture(folder.FullName);
            using var otherPermissions = new RecyclePermissionFixture(second.FullName);
            var tree = (ListBox)window.FindName("SpaceTree");
            rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
            FinishTask(window.NavigateToEntryAsync(rows.Scan.FindEntry(folder.FullName)!.Value));
            rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
            tree.SelectedItems.Add(rows.Row(rows.Find(rows.Scan.FindEntry(second.FullName)!.Value)));
            Assert.Null(Review(window.DeleteSelectedFolderAsync, approve: true, administrator: false,
                expectedPaths: [folder.FullName, second.FullName]));
            rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
            FinishTask(window.NavigateToEntryAsync(rows.Scan.FindEntry(second.FullName)!.Value));
            string firstRetryPath = Assert.IsType<string>(retry.ToolTip);
            Assert.Equal(second.FullName, firstRetryPath);
            otherPermissions.Dispose();
            replacedPermissions.Dispose();
            Directory.Move(firstRetryPath, firstRetryPath + "-original");
            Directory.CreateDirectory(firstRetryPath);
            Assert.Null(Review(window.RetryRecycleWithAdministratorAsync, approve: true, administrator: true, expectedPaths: [firstRetryPath]));
            Assert.True(retry.IsVisible);
            Assert.Equal(folder.FullName, retry.ToolTip);
            Assert.True(File.Exists(Path.Join(firstRetryPath + "-original", "payload.txt")));
            Assert.Equal(contents, File.ReadAllText(Path.Join(second.FullName + "-original", "payload.txt")));
        }

        RecycleResult? Review(Func<Task<RecycleResult?>> request, bool approve, bool administrator, bool cancelQueued = false, string[]? expectedPaths = null)
        {
            bool reviewed = false;
            Exception? failure = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                var dialog = app.Windows.OfType<RecycleFolderWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (dialog is null) return;
                timer.Stop();
                try
                {
                    Assert.Equal((expectedPaths ?? [folder.FullName]).Order(),
                        ((TextBox)dialog.FindName("TargetPath")).Text.Split(Environment.NewLine).Order());
                    var confirm = (Button)dialog.FindName("RecycleButton");
                    if (administrator)
                        Assert.Equal("Retry with administrator permission", confirm.Content);
                    reviewed = true;
                    if (approve) confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            };
            timer.Start();
            RecycleResult? result;
            try
            {
                var task = request();
                if (cancelQueued)
                {
                    var cancel = (Button)window.FindName("CancelRecycleButton");
                    var status = (TextBlock)window.FindName("RecycleStatusText");
                    PumpUntil(() => cancel.IsVisible && status.Text.StartsWith("Recycling ", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                result = Finish(task);
            }
            finally { timer.Stop(); }
            if (failure is not null) throw failure;
            Assert.True(reviewed);
            return result;
        }
    }

    private static void CheckBackgroundRecycle(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string first = Folder("first", 8192);
        string second = Folder("second", 16384);
        string keep = Folder("keep", 4096);
        string waiting = Folder("waiting", 4096);
        string child = Directory.CreateDirectory(Path.Join(keep, "child")).FullName;
        File.WriteAllBytes(Path.Join(child, "payload.bin"), new byte[4096]);
        window.SetScanTarget(fixture.Path);
        ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        Assert.True(Finish(window.ScanCurrentPathAsync()));

        var tree = (ListBox)window.FindName("SpaceTree");
        var status = (TextBlock)window.FindName("RecycleStatusText");
        var cancel = (Button)window.FindName("CancelRecycleButton");
        ScanSnapshot Snapshot() => Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan;
        Task? held = null;
        TaskCompletionSource? release = null;
        var jobs = new List<(string Path, Task<RecycleResult?> Task)>();
        try
        {
            HoldMutationLease();
            var firstJob = Queue(first);
            PumpUntil(() => status.Text.Contains("Recycling", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            Assert.False(firstJob.IsCompleted);
            Assert.True(((Grid)window.FindName("ExplorePanel")).IsEnabled);
            Assert.True(((Button)window.FindName("ScanButton")).IsEnabled);
            Assert.True(((FrameworkElement)window.FindName("PathBar")).IsEnabled);
            Assert.Null(Finish(window.DeleteSelectedFolderAsync()));
            Assert.Contains("overlaps", ((TextBlock)window.FindName("StatusText")).Text);

            var secondJob = Queue(second);
            PumpUntil(() => status.Text == "Recycling first… · 1 queued", TimeSpan.FromSeconds(5));
            Assert.Equal(first, status.ToolTip);
            Assert.False(secondJob.IsCompleted);
            Assert.True(cancel.IsVisible);
            Browse(fixture.Path);
            var beforeRescan = Snapshot();
            Assert.True(Finish(window.ScanCurrentPathAsync()));
            Assert.NotSame(beforeRescan, Snapshot());
            Browse(child);
            FinishTask(window.SortTreeAsync(ScanSortColumn.Name));
            FinishTask(window.SetExplorerSizeBasisAsync(true));
            Assert.Equal(child, window.CurrentPath);
            Assert.Equal("Recycling first… · 1 queued", status.Text);
            ((RadioButton)window.FindName("MapViewButton")).IsChecked = true;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "background-recycle.png"), 1020, 790);
            window.Activate();
            PumpUntil(() => window.IsActive, TimeSpan.FromSeconds(5));
            var map = (TreemapPanel)window.FindName("SpaceMap");
            var tile = Assert.Single(map.Buttons);
            Assert.True(tile.Focus());

            ReleaseMutationLease();
            Assert.False(Assert.IsType<RecycleResult>(Finish(firstJob)).Cancelled);
            Assert.False(Assert.IsType<RecycleResult>(Finish(secondJob)).Cancelled);
            Assert.False(Directory.Exists(first));
            Assert.False(Directory.Exists(second));
            Assert.Null(Snapshot().FindEntry(first));
            Assert.Null(Snapshot().FindEntry(second));
            Assert.Equal(child, window.CurrentPath);
            Assert.Equal(child, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
            Assert.Same(Assert.Single(map.Buttons), Keyboard.FocusedElement);
            Assert.True(Assert.IsType<ExplorerRows>(tree.ItemsSource).IsExpanded(Snapshot().FindEntry(keep)!.Value));
            Assert.StartsWith("Name ", ((ButtonBase)window.FindName("NameHeader")).Content.ToString());
            Assert.False(Assert.IsType<ExplorerRows>(tree.ItemsSource).UseAllocation);
            const string moved = "2 folders moved to the Recycle Bin";
            Assert.Equal(moved, status.Text);
            Assert.Null(Finish(Queue(keep, approve: false)));
            Assert.Equal(moved, status.Text);

            HoldMutationLease();
            var cancelled = Queue(keep);
            var skipped = Queue(waiting);
            PumpUntil(() => cancel.IsVisible && status.Text == "Recycling keep… · 1 queued", TimeSpan.FromSeconds(5));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(Assert.IsType<RecycleResult>(Finish(cancelled)).Cancelled);
            Assert.Null(Finish(skipped));
            Assert.True(File.Exists(Path.Join(child, "payload.bin")));
            Assert.True(Directory.Exists(waiting));
            Assert.Equal("2 folders cancelled before moving", status.Text);
            ReleaseMutationLease();

            HoldMutationLease();
            var replaced = Queue(waiting);
            string original = Path.Join(fixture.Path, "original");
            Directory.Move(waiting, original);
            Folder("waiting", 2048);
            var columns = (Button)window.FindName("ColumnsButton");
            ((RadioButton)window.FindName("ListViewButton")).IsChecked = true;
            window.UpdateLayout();
            Assert.True(columns.Focus());
            ReleaseMutationLease();
            Assert.Null(Finish(replaced));
            Assert.True(Directory.Exists(waiting));
            Assert.True(Directory.Exists(original));
            Assert.Equal("Could not recycle waiting", status.Text);
            Assert.Same(columns, Keyboard.FocusedElement);
            var details = (ToggleButton)window.FindName("DiagnosticsToggle");
            details.IsChecked = true;
            var error = (TextBox)window.FindName("RecycleDiagnosticMessage");
            Assert.True(error.IsVisible);
            Assert.Contains(waiting, error.Text);
            Assert.Contains("replaced since confirmation", error.Text);
            details.IsChecked = false;

            FinishTask(window.SetExplorerSizeBasisAsync(false));
            CheckScanRootHandoff(keep, clearScan: false);
            CheckScanRootHandoff(waiting, clearScan: true);
            Assert.Equal(Visibility.Collapsed, error.Visibility);

            string branch = Folder("branch", 8192);
            string leaf = Directory.CreateDirectory(Path.Join(branch, "leaf")).FullName;
            File.WriteAllText(Path.Join(leaf, "payload.txt"), "Owned fixture");
            window.SetScanTarget(fixture.Path);
            Assert.True(Finish(window.ScanCurrentPathAsync()));
            var beforeRefresh = Snapshot();
            var pause = new PausedRecycleAnalysis(fixture.Path);
            var runtime = Assert.IsType<SpaceMaid.App.Extensions.ExtensionRuntime>(typeof(MainWindow)
                .GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            runtime.Opportunities.Register("test.recycle-refresh", pause);
            Task<RecycleResult?> leafJob, branchJob, stopped;
            try
            {
                leafJob = Queue(leaf);
                FinishTask(pause.Entered.Task);
                Assert.False(Directory.Exists(leaf));

                HoldMutationLease();
                stopped = Queue(original);
                PumpUntil(() => status.Text == "Recycling original…", TimeSpan.FromSeconds(5));
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                // The completed child no longer reserves its parent. The next
                // move can run while the cancelled analysis is still blocked.
                branchJob = Queue(branch);
                ReleaseMutationLease();
                PumpUntil(() => !Directory.Exists(branch), TimeSpan.FromSeconds(20));
                Assert.True(Directory.Exists(original));
                Assert.Same(beforeRefresh, Snapshot());
                Assert.False(branchJob.IsCompleted);
                Browse(original);
            }
            finally { pause.Release.TrySetResult(); }

            Assert.False(Assert.IsType<RecycleResult>(Finish(leafJob)).Cancelled);
            Assert.False(Assert.IsType<RecycleResult>(Finish(branchJob)).Cancelled);
            Assert.True(Assert.IsType<RecycleResult>(Finish(stopped)).Cancelled);
            Assert.Null(Snapshot().FindEntry(branch));
            Assert.Equal(original, window.CurrentPath);
            CheckTreePosition();
            CheckBatchSelection();
        }
        finally
        {
            ReleaseMutationLease();
            foreach (var job in jobs.OrderBy(job => job.Path.Length))
                if (Finish(job.Task)?.RecycledPath is { } destination && !Directory.Exists(job.Path))
                    DeletionTests.RestoreFixture(destination, job.Path);
            FinishTask(window.SetExplorerSizeBasisAsync(false));
        }

        string Folder(string name, int bytes)
        {
            string folder = Directory.CreateDirectory(Path.Join(fixture.Path, name)).FullName;
            File.WriteAllBytes(Path.Join(folder, "payload.bin"), new byte[bytes]);
            return folder;
        }

        void Browse(string path) => FinishTask(window.NavigateToEntryAsync(Snapshot().FindEntry(path)!.Value));

        void CheckBatchSelection()
        {
            string parent = Folder("batch-parent", 4096);
            string nested = Directory.CreateDirectory(Path.Join(parent, "nested")).FullName;
            string sibling = Folder("batch-sibling", 8192);
            string background = Folder("batch-background", 4096);
            string pending = Folder("batch-pending", 4096);
            string removed = Folder("batch-keep", 2048);
            string file = Path.Join(fixture.Path, "batch-file.txt");
            File.WriteAllText(file, "Keep this file");
            window.SetScanTarget(fixture.Path);
            Assert.True(Finish(window.ScanCurrentPathAsync()));
            Browse(nested); // Reveal the nested row.
            Assert.Equal(SelectionMode.Extended, tree.SelectionMode);

            HoldMutationLease();
            var refresh = Queue(background);
            Select(parent, nested, sibling, file);
            FinishTask(window.SortTreeAsync(ScanSortColumn.Name));
            AssertSelection(parent, nested, sibling, file);
            ReleaseMutationLease();
            Assert.False(Assert.IsType<RecycleResult>(Finish(refresh)).Cancelled);
            AssertSelection(parent, nested, sibling, file);

            var row = BringRowIntoView(sibling);
            RightClick(window, row);
            Assert.True(tree.ContextMenu.IsOpen);
            var menuAction = tree.ContextMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "Move selected folders to Recycle Bin…"));
            Assert.True(menuAction.IsEnabled);
            AssertSelection(parent, nested, sibling, file);
            tree.ContextMenu.IsOpen = false;
            ReviewBatch(() => menuAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)), approve: false, skipped: [file]);
            Assert.True(Directory.Exists(parent) && Directory.Exists(sibling));

            var delete = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(row), Environment.TickCount, Key.Delete)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            ReviewBatch(() => row.RaiseEvent(delete), approve: false, skipped: [file]);
            Assert.True(delete.Handled);
            Assert.True(Directory.Exists(parent) && Directory.Exists(sibling));
            Browse(parent);
            AssertSelection(parent);

            HoldMutationLease();
            var pendingJob = Queue(pending);
            Select(parent, nested, sibling, file, pending, removed);
            var batchPaths = BatchPathsInTreeOrder(parent, sibling, removed);
            Task<RecycleResult?[]>? batch = null;
            ReviewBatch(() =>
            {
                batch = window.RecycleSelectedFoldersAsync();
                for (int index = 0; index < batchPaths.Length; index++) jobs.Add((batchPaths[index], ResultAt(batch, index)));
            }, approve: true, skipped: [file, pending], expected: batchPaths);
            PumpUntil(() => status.Text == "Recycling batch-pending… · 3 queued", TimeSpan.FromSeconds(5));
            CheckQueueRemoval(batchPaths, removed);
            Assert.False(pendingJob.IsCompleted);
            Assert.Equal("Recycling batch-pending… · 2 queued", status.Text);
            Assert.True(((Grid)window.FindName("ExplorePanel")).IsEnabled);
            ReleaseMutationLease();
            Assert.False(Assert.IsType<RecycleResult>(Finish(pendingJob)).Cancelled);
            var results = Finish(batch!);
            Assert.Equal(3, results.Length);
            for (int index = 0; index < batchPaths.Length; index++)
                if (batchPaths[index] == removed) Assert.Null(results[index]);
                else Assert.False(Assert.IsType<RecycleResult>(results[index]).Cancelled);
            Assert.False(Directory.Exists(parent) || Directory.Exists(sibling));
            Assert.True(File.Exists(file));
            Assert.True(File.Exists(Path.Join(removed, "payload.bin")));
            Assert.Contains("1 folder removed from the queue", status.Text);
            AssertSelection(file, removed);

            void CheckQueueRemoval(string[] waitingPaths, string pathToRemove)
            {
                var queueButton = (Button)window.FindName("RecycleQueueButton");
                var popup = (Popup)window.FindName("RecycleQueuePopup");
                var panel = (Border)window.FindName("RecycleQueuePanel");
                var list = (ListBox)window.FindName("RecycleQueueList");
                Assert.True(queueButton.IsVisible);
                Assert.True(queueButton.Focus());
                queueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => popup.IsOpen && RemoveButtons().Length == waitingPaths.Length, TimeSpan.FromSeconds(5));
                Assert.Equal(waitingPaths.Select(RemoveName), RemoveButtons().Select(AutomationProperties.GetName));
                Assert.True(panel.IsKeyboardFocusWithin);
                RenderMenu(panel, Path.Join(artifactRoot, "artifacts", "ui", "recycle-waiting-queue.png"));

                var remove = RemoveButtons().Single(button => AutomationProperties.GetName(button) == RemoveName(pathToRemove));
                Assert.True(remove.Focus());
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(popup.IsOpen);
                Assert.True(panel.IsKeyboardFocusWithin);
                Assert.Equal(waitingPaths.Length - 1, list.Items.Count);
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // An obsolete row cannot remove another job.
                panel.UpdateLayout();
                Assert.Equal(waitingPaths.Where(path => path != pathToRemove).Select(RemoveName), RemoveButtons().Select(AutomationProperties.GetName));

                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                panel.RaiseEvent(escape);
                Assert.True(escape.Handled);
                Assert.False(popup.IsOpen);
                Assert.True(queueButton.IsKeyboardFocused);

                Button[] RemoveButtons() => Descendants(list).OfType<Button>().Where(button => Equals(button.Content, "Remove")).ToArray();
                static string RemoveName(string path) => $"Remove {path} from the recycle queue";
            }

            void Select(string first, params string[] others)
            {
                Browse(first);
                var rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                foreach (string path in others) tree.SelectedItems.Add(rows.Row(rows.Find(rows.Scan.FindEntry(path)!.Value)));
            }

            void AssertSelection(params string[] expected) => Assert.Equal(expected.Order(StringComparer.Ordinal),
                tree.SelectedItems.Cast<ExplorerRow>().Select(item => item.Path).Order(StringComparer.Ordinal));

            ListBoxItem BringRowIntoView(string path)
            {
                var rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                int index = rows.Find(rows.Scan.FindEntry(path)!.Value);
                tree.ScrollIntoView(rows.Row(index));
                window.UpdateLayout();
                return Assert.IsAssignableFrom<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(index));
            }

            void ReviewBatch(Action request, bool approve, string[] skipped, string[]? expected = null)
            {
                var targetPaths = expected ?? BatchPathsInTreeOrder(parent, sibling);
                int reviewed = 0;
                Exception? failure = null;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    var dialog = app.Windows.OfType<RecycleFolderWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                    if (dialog is null) return;
                    timer.Stop();
                    reviewed++;
                    try
                    {
                        Assert.Equal(targetPaths, ((TextBox)dialog.FindName("TargetPath")).Text.Split(Environment.NewLine));
                        Assert.Contains("1 included with selected parents", ((TextBlock)dialog.FindName("SelectionNote")).Text);
                        Assert.Contains($"{skipped.Length} skipped", ((TextBlock)dialog.FindName("SelectionNote")).Text);
                        foreach (string path in skipped) Assert.Contains(path, ((TextBox)dialog.FindName("SkippedEntries")).Text);
                        Assert.True(((Button)dialog.FindName("KeepButton")).IsDefault);
                        if (approve)
                        {
                            var queue = (Button)dialog.FindName("RecycleButton");
                            Assert.True(queue.IsVisible);
                            Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "recycle-selection-review.png"), 520, 360);
                            queue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        else dialog.Close();
                    }
                    catch (Exception error) { failure = error; dialog.Close(); }
                };
                timer.Start();
                try { request(); PumpUntil(() => reviewed > 0, TimeSpan.FromSeconds(5)); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                Assert.Equal(1, reviewed);
            }

            string[] BatchPathsInTreeOrder(params string[] paths)
            {
                var rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                return paths.OrderBy(path => rows.Find(rows.Scan.FindEntry(path)!.Value)).ToArray();
            }

            static async Task<RecycleResult?> ResultAt(Task<RecycleResult?[]> results, int index) => (await results).ElementAtOrDefault(index);
        }

        void CheckTreePosition()
        {
            // A full-width compact list can fit the default columns. Make this
            // fixture explicitly overflow to exercise horizontal restoration.
            ((GridViewColumn)window.FindName("NameColumn")).Width = tree.ActualWidth + 200;
            for (int index = 0; index < 48; index++) Folder($"folder-{index:D2}", 1);
            window.SetScanTarget(fixture.Path);
            Assert.True(Finish(window.ScanCurrentPathAsync()));
            var rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
            string earlier = rows.Row(5).Path, top = rows.Row(20).Path;
            string survivor = rows.Row(21).Path, focusedPath = rows.Row(22).Path;
            HoldMutationLease();
            var earlierJob = Queue(earlier);
            var topJob = Queue(top);
            Browse(focusedPath);

            var scroll = Descendants(tree).OfType<ScrollViewer>().First();
            FocusAt(top);
            Assert.True(scroll.ScrollableWidth > 0);
            scroll.ScrollToHorizontalOffset(Math.Min(32, scroll.ScrollableWidth));
            window.UpdateLayout();
            double horizontal = scroll.HorizontalOffset;
            Assert.True(horizontal > 0);

            ReleaseMutationLease();
            Assert.False(Assert.IsType<RecycleResult>(Finish(earlierJob)).Cancelled);
            Assert.False(Assert.IsType<RecycleResult>(Finish(topJob)).Cancelled);
            window.UpdateLayout();
            Assert.Equal(survivor, TopPath());
            Assert.Equal(horizontal, scroll.HorizontalOffset);
            var focusedRow = Assert.IsAssignableFrom<ListBoxItem>(Keyboard.FocusedElement);
            Assert.Equal(focusedPath, Assert.IsType<ExplorerRow>(focusedRow.DataContext).Path);
            Assert.Equal(focusedPath, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "recycle-preserved-tree-position.png"), 1020, 790);

            // The next Delete command still reaches the folder on which the
            // user was working. Dismiss this review without moving it yet.
            string? reviewed = null;
            var timer = WatchConfirmation(path => reviewed = path, approve: false);
            try
            {
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(focusedRow), Environment.TickCount, Key.Delete)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                focusedRow.RaiseEvent(key);
                Assert.True(key.Handled);
                PumpUntil(() => reviewed is not null, TimeSpan.FromSeconds(5));
                Assert.Equal(focusedPath, reviewed);
            }
            finally { timer.Stop(); }

            HoldMutationLease();
            var focusedJob = Queue(focusedPath);
            FocusAt(survivor);
            ReleaseMutationLease();
            Assert.False(Assert.IsType<RecycleResult>(Finish(focusedJob)).Cancelled);
            window.UpdateLayout();
            Assert.Equal(survivor, TopPath());
            Assert.True(tree.IsKeyboardFocused);

            string TopPath() => Assert.IsType<ExplorerRows>(tree.ItemsSource).Row((int)scroll.VerticalOffset).Path;

            void FocusAt(string topPath)
            {
                var visibleRows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                scroll.ScrollToVerticalOffset(visibleRows.Find(visibleRows.Scan.FindEntry(topPath)!.Value));
                window.UpdateLayout();
                PumpUntil(() => TopPath() == topPath, TimeSpan.FromSeconds(5));
                var row = Assert.IsAssignableFrom<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(tree.SelectedIndex));
                Assert.True(row.Focus());
                Assert.Equal(topPath, TopPath());
            }
        }

        void CheckScanRootHandoff(string path, bool clearScan)
        {
            window.SetScanTarget(path);
            Assert.True(Finish(window.ScanCurrentPathAsync()));
            HoldMutationLease();
            var job = Queue(path);
            if (clearScan) window.SetScanTarget(path); // Same empty view as after a failed rescan.
            ReleaseMutationLease();
            Assert.False(Assert.IsType<RecycleResult>(Finish(job)).Cancelled);
            Assert.False(Directory.Exists(path));
            Assert.Equal(fixture.Path, window.CurrentPath);
            Assert.Null(tree.ItemsSource);
            Assert.True(((Button)window.FindName("ScanButton")).IsEnabled);
            Assert.Contains("Scan this parent folder", ((TextBlock)window.FindName("StatusText")).Text);
        }

        Task<RecycleResult?> Queue(string path, bool approve = true)
        {
            Browse(path);
            string? reviewedPath = null;
            var timer = WatchConfirmation(reviewed => reviewedPath = reviewed, approve);
            var task = window.DeleteSelectedFolderAsync();
            jobs.Add((path, task));
            try { PumpUntil(() => reviewedPath is not null || task.IsCompleted, TimeSpan.FromSeconds(5)); }
            finally { timer.Stop(); }
            Assert.Equal(path, reviewedPath);
            return task;
        }

        DispatcherTimer WatchConfirmation(Action<string> review, bool approve)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                var dialog = app.Windows.OfType<RecycleFolderWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (dialog is null) return;
                review(((TextBox)dialog.FindName("TargetPath")).Text);
                if (approve) ((Button)dialog.FindName("RecycleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else dialog.Close();
            };
            timer.Start();
            return timer;
        }

        void HoldMutationLease()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release = gate;
            held = CleanupMutationLease.RunAsync(async () => { entered.SetResult(); await gate.Task; });
            FinishTask(entered.Task);
        }

        void ReleaseMutationLease()
        {
            release?.TrySetResult();
            if (held is not null) FinishTask(held);
            release = null;
            held = null;
        }

    }

    private static void FinishTask(Task task)
    {
        PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(20));
        task.GetAwaiter().GetResult();
    }

    private static T Finish<T>(Task<T> task)
    {
        FinishTask(task);
        return task.GetAwaiter().GetResult();
    }

    private sealed class PausedRecycleAnalysis(string rootPath) : IOpportunityExtension
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OpportunityAnalysis Analyze(IOpportunityInventory inventory, CancellationToken token)
        {
            // Simulate a provider that cannot return immediately on cancellation.
            if (inventory.RootPath == rootPath && Entered.TrySetResult()) Release.Task.GetAwaiter().GetResult();
            return new([], new([], []));
        }
    }
}
