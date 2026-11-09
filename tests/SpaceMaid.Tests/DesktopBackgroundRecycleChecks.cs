using System.IO;
using System.Reflection;
using System.Windows;
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

        void CheckTreePosition()
        {
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

        static void FinishTask(Task task)
        {
            PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(20));
            task.GetAwaiter().GetResult();
        }

        static T Finish<T>(Task<T> task)
        {
            FinishTask(task);
            return task.GetAwaiter().GetResult();
        }
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
