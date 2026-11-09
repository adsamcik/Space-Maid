using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
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

            ReleaseMutationLease();
            Assert.False(Assert.IsType<RecycleResult>(Finish(firstJob)).Cancelled);
            Assert.False(Assert.IsType<RecycleResult>(Finish(secondJob)).Cancelled);
            Assert.False(Directory.Exists(first));
            Assert.False(Directory.Exists(second));
            Assert.Null(Snapshot().FindEntry(first));
            Assert.Null(Snapshot().FindEntry(second));
            Assert.Equal(child, window.CurrentPath);
            Assert.Equal(child, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
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
            ReleaseMutationLease();
            Assert.Null(Finish(replaced));
            Assert.True(Directory.Exists(waiting));
            Assert.True(Directory.Exists(original));
            Assert.Equal("Could not recycle waiting", status.Text);
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
        }
        finally
        {
            ReleaseMutationLease();
            foreach (var job in jobs)
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
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                var dialog = app.Windows.OfType<RecycleFolderWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (dialog is null) return;
                reviewedPath = ((TextBox)dialog.FindName("TargetPath")).Text;
                if (approve) ((Button)dialog.FindName("RecycleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else dialog.Close();
            };
            timer.Start();
            var task = window.DeleteSelectedFolderAsync();
            jobs.Add((path, task));
            try { PumpUntil(() => reviewedPath is not null || task.IsCompleted, TimeSpan.FromSeconds(5)); }
            finally { timer.Stop(); }
            Assert.Equal(path, reviewedPath);
            return task;
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
}
