using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.IO;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal sealed record RecycleSelectionSkip(string Path, string Reason);
internal sealed record RecycleSelectionReview(FolderDeletionTarget[] Targets, RecycleSelectionSkip[] Skipped, int NestedSelections);

public partial class MainWindow
{
    private sealed class RecycleJob(FolderDeletionTarget target)
    {
        public FolderDeletionTarget Target { get; } = target;
        public string FolderName => Path.GetFileName(Target.Path);
        internal TaskCompletionSource<RecycleResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record RecycleAttempt(RecycleJob Job, RecycleResult? Result, string? Error, bool CancelledBeforeMove = false);

    private readonly ObservableCollection<RecycleJob> _queuedRecycles = [];
    private RecycleJob? _activeRecycle;
    private Task? _recycleTask;
    private CancellationTokenSource? _recycleWork;
    private CancellationTokenSource? _recycleRefreshWork;
    private TaskCompletionSource? _recycleStateChanged;
    private bool _scanStale;
    private int _queuedRecyclesCancelled, _queuedRecyclesRemoved, _foregroundWorkVersion;
    private (string Status, string? Path)? _recycleCheck;
    private string? _recycleSummary, _recycleDetails;

    private async void Explore_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None || e.IsRepeat
            || ExploreTab.IsChecked != true) return;
        // Delete in a path field retains normal text-selection behavior.
        for (DependencyObject? source = e.OriginalSource as DependencyObject; source is not null;
             source = VisualTreeHelper.GetParent(source))
            if (source is TextBoxBase) return;
        e.Handled = true;
        await RecycleSelectedFoldersAsync();
    }

    internal async Task<RecycleResult?> DeleteSelectedFolderAsync() => (await RecycleSelectedFoldersAsync()).FirstOrDefault();

    internal Task<RecycleResult?[]> RecycleSelectedFoldersAsync()
    {
        if (_snapshot is not { } scan || !CanUseFolderMenu(scan)) return Task.FromResult<RecycleResult?[]>([]);
        if (_selectionIsGroup)
        {
            StatusText.Text = "Grouped entries cannot be deleted. Select an exact folder in the tree.";
            return Task.FromResult<RecycleResult?[]>([]);
        }
        var links = RecycleLinksInTreeOrder(SelectedTreeRows().Where(row => ReferenceEquals(row.Scan, scan)));
        if (links.Length > 0) return DeleteFoldersAsync(scan, links);
        StatusText.Text = "Select folders in Explore to recycle them.";
        return Task.FromResult<RecycleResult?[]>([]);
    }

    private static int[] RecycleLinksInTreeOrder(IEnumerable<ExplorerRow> rows) => rows.OrderBy(row => row.VisibleIndex)
        .Select(row => row.Entry.LinkIndex).ToArray();

    private string? RecycleBlockReason(ScanSnapshot scan, int link)
    {
        if (_recycleCheck is not null) return "Finish the current recycle confirmation first.";
        if (FolderDeletionTarget.SelectionBlockReason(scan, link) is { } blocked) return blocked;
        string path = scan.EntryPath(link)!;
        return PendingRecyclePaths().Any(pending => RecyclePathsOverlap(pending, path))
            ? "This folder overlaps a pending recycle request." : null;
    }

    private IEnumerable<string> PendingRecyclePaths()
    {
        if (_activeRecycle is { } active) yield return active.Target.Path;
        foreach (var job in _queuedRecycles) yield return job.Target.Path;
    }

    private static bool RecyclePathsOverlap(string first, string second) => CoversRecyclePath(first, second) || CoversRecyclePath(second, first);

    private async Task<RecycleResult?[]> DeleteFoldersAsync(ScanSnapshot scan, int[] links)
    {
        if (!CanUseFolderMenu(scan) || links.Length == 0) return [];
        if (_recycleCheck is not null)
        {
            StatusText.Text = "Finish the current recycle confirmation first.";
            return [];
        }
        if (links.Length == 1 && RecycleBlockReason(scan, links[0]) is { } blocked)
        {
            StatusText.Text = blocked;
            return [];
        }

        string? singlePath = links.Length == 1 ? scan.EntryPath(links[0]) : null;
        _recycleCheck = singlePath is not null ? ($"Checking {Path.GetFileName(singlePath)}…", singlePath)
            : ($"Checking {links.Length:N0} selected entries…", null);
        int workVersion = _foregroundWorkVersion;
        UpdateRecycleActivity();
        RecycleSelectionReview review;
        try
        {
            var pending = PendingRecyclePaths().ToArray();
            var files = Operations.Files;
            var token = _extensionLifetime.Token;
            review = await Task.Run(() => PrepareRecycleSelection(scan, links, pending, files, token), token);
            // A refresh can replace the inventory while the live identity check
            // runs. Each requested target is already tied to the original row.
            if (_closing || workVersion != _foregroundWorkVersion)
            {
                if (_work is null && !_closing) StatusText.Text = "Select the folders again to recycle them.";
                return [];
            }
            if (review.Targets.Length == 0)
            {
                StatusText.Text = links.Length == 1 ? $"Could not check the folder. {review.Skipped[0].Reason}"
                    : $"No selected folders can be recycled. {review.Skipped[0].Reason}";
                return [];
            }
            var confirmation = links.Length == 1 ? new RecycleFolderWindow(review.Targets[0]) : new RecycleFolderWindow(review);
            confirmation.Owner = this;
            if (confirmation.ShowDialog() != true || _closing)
                return [];
        }
        catch (OperationCanceledException) when (_closing) { return []; }
        catch (Exception error) when (IsRecycleError(error))
        {
            if (!_closing) StatusText.Text = $"Could not review the selected folders. {error.Message}";
            return [];
        }
        finally
        {
            _recycleCheck = null;
            if (!_closing) UpdateRecycleActivity();
        }

        var jobs = review.Targets.Select(target => new RecycleJob(target)).ToArray();
        foreach (var job in jobs) _queuedRecycles.Add(job);
        _recycleRefreshWork?.Cancel();
        SignalRecycleStateChange();
        if (_recycleTask is null)
        {
            _recycleTask = RunRecycleQueueAsync();
            UpdateUpdateButton();
        }
        UpdateRecycleActivity();
        return await Task.WhenAll(jobs.Select(job => job.Completion.Task));
    }

    private static RecycleSelectionReview PrepareRecycleSelection(ScanSnapshot scan, int[] links,
        string[] pending, DiskAccess files, CancellationToken token)
    {
        var candidates = links.Distinct().Select(link => (Link: link, Path: scan.EntryPath(link))).ToArray();
        var checkedTargets = new Dictionary<string, FolderDeletionTarget>(StringComparer.OrdinalIgnoreCase);
        var accepted = new Dictionary<int, FolderDeletionTarget>();
        var skipped = new List<RecycleSelectionSkip>();
        int nested = 0;
        foreach (var (link, path) in candidates.OrderBy(candidate => candidate.Path?.Length ?? 0))
        {
            token.ThrowIfCancellationRequested();
            string? parent = path is not null ? Path.GetDirectoryName(path) : null;
            while (parent is not null && !checkedTargets.ContainsKey(parent)) parent = Path.GetDirectoryName(parent);
            if (parent is not null && checkedTargets[parent].Path == parent)
            {
                nested++;
                continue;
            }

            string? reason = FolderDeletionTarget.SelectionBlockReason(scan, link);
            if (reason is null && (parent is not null || checkedTargets.ContainsKey(path!)))
                reason = "This path overlaps another selected folder.";
            if (reason is null && pending.Any(queued => RecyclePathsOverlap(queued, path!)))
                reason = "This folder overlaps a pending recycle request.";
            if (reason is null)
            {
                try
                {
                    var target = FolderDeletionTarget.FromScan(scan, link, files);
                    checkedTargets.Add(target.Path, target);
                    accepted.Add(link, target);
                    continue;
                }
                catch (Exception error) when (IsRecycleError(error)) { reason = error.Message; }
            }
            skipped.Add(new(path ?? scan.Entry(link).Name, reason));
        }
        return new(candidates.Where(candidate => accepted.ContainsKey(candidate.Link)).Select(candidate => accepted[candidate.Link]).ToArray(),
            skipped.ToArray(), nested);
    }

    private async Task RunRecycleQueueAsync()
    {
        // Let the confirmation close and paint the activity row before starting work.
        await Dispatcher.Yield(DispatcherPriority.Background);
        var attempts = new List<RecycleAttempt>();
        try
        {
            using var log = await Task.Run(() => new ScanLog(LogDirectory));
            while (!_closing)
            {
                await MoveQueuedFoldersAsync(attempts, log);
                if (_closing) return;
                UpdateRecycleActivity();

                // Keep one refresh in flight. A confirmed move can run while a
                // superseded refresh is still unwinding on a worker thread.
                var checkedAttempts = attempts.ToArray();
                using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_extensionLifetime.Token);
                _recycleRefreshWork = refresh;
                var reconciliation = RefreshRecycledFoldersAsync(checkedAttempts, log, refresh.Token);
                bool refreshed = false;
                try
                {
                    while (!_closing && !reconciliation.IsCompleted)
                    {
                        if (_queuedRecycles.Count == 0)
                            await Task.WhenAny(reconciliation, WaitForRecycleStateChangeAsync());
                        if (_queuedRecycles.Count > 0)
                        {
                            refresh.Cancel();
                            await MoveQueuedFoldersAsync(attempts, log);
                        }
                    }
                }
                finally
                {
                    try { await reconciliation; refreshed = true; }
                    catch (OperationCanceledException) when (refresh.IsCancellationRequested) { }
                    finally { _recycleRefreshWork = null; }
                }
                if (refreshed && attempts.Count == checkedAttempts.Length && _queuedRecycles.Count == 0) break;
            }

            if (_closing) return;
            SummarizeRecycling(attempts);
            if (_startupOffered) _ = RefreshRecycleBinAsync();
            if (_work is null) ShowActionLog(log);
            ShowActionLogError(log);
            foreach (var attempt in attempts) attempt.Job.Completion.TrySetResult(attempt.Result);
        }
        catch (OperationCanceledException) when (_closing) { }
        finally
        {
            foreach (var attempt in attempts) attempt.Job.Completion.TrySetResult(null);
            DiscardWaitingRecycles();
            _recycleTask = null;
            _queuedRecyclesCancelled = _queuedRecyclesRemoved = 0;
            SignalRecycleStateChange();
            if (!_closing) { UpdateRecycleActivity(); UpdateUpdateButton(); RefreshDeferredRecycleGroups(); }
        }
    }

    private async Task MoveQueuedFoldersAsync(List<RecycleAttempt> attempts, ScanLog log)
    {
        while (!_closing && _queuedRecycles.Count > 0)
        {
            var job = _queuedRecycles[0];
            using var work = new CancellationTokenSource();
            _activeRecycle = job;
            _recycleWork = work;
            try
            {
                RemoveWaitingRecycleAt(0);
                UpdateRecycleActivity();
                attempts.Add(await RecycleOneAsync(job, work.Token, log));
            }
            catch { job.Completion.TrySetResult(null); throw; }
            finally { _activeRecycle = null; _recycleWork = null; }
            if (!_closing) UpdateRecycleActivity();
        }
    }

    private void SummarizeRecycling(IReadOnlyList<RecycleAttempt> attempts)
    {
        var summary = new List<string>();
        _recycleDetails = null;
        int moved = attempts.Count(attempt => attempt.Result is { Cancelled: false });
        if (moved > 0) summary.Add($"{FolderCount(moved)} moved to the Recycle Bin");
        if (attempts.Any(attempt => attempt.Result is { Cancelled: true } && !attempt.CancelledBeforeMove))
            summary.Add("Move stopped. Some contents may have moved");
        int skipped = _queuedRecyclesCancelled + attempts.Count(attempt => attempt.CancelledBeforeMove);
        if (skipped > 0) summary.Add($"{FolderCount(skipped)} cancelled before moving");
        if (_queuedRecyclesRemoved > 0) summary.Add($"{FolderCount(_queuedRecyclesRemoved)} removed from the queue");
        int failed = attempts.Count(attempt => attempt.Error is not null);
        if (attempts.FirstOrDefault(attempt => attempt.Error is not null) is { } failure)
        {
            summary.Add(failed == 1 ? $"Could not recycle {Path.GetFileName(failure.Job.Target.Path)}" : $"{FolderCount(failed)} failed");
            _recycleDetails = $"{failure.Job.Target.Path}\n{failure.Error}";
        }
        _recycleSummary = string.Join(" · ", summary);
        RecycleDiagnosticMessage.Text = _recycleDetails ?? "";
        RecycleDiagnosticMessage.Visibility = _recycleDetails is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task<RecycleAttempt> RecycleOneAsync(RecycleJob job, CancellationToken token, ScanLog log)
    {
        var target = job.Target;
        try
        {
            await Task.Run(() => log.Record("file-action-start", new { Action = "recycle", target.Path }));
            var result = await Operations.RecycleAsync(target, new WindowInteropHelper(this).Handle, token, diagnostic: log.Record);
            await Task.Run(() => log.Record("recycle-result", result));
            return new(job, result, null);
        }
        catch (OperationCanceledException) { return new(job, new(true, null), null, CancelledBeforeMove: true); }
        catch (Exception ex) when (IsRecycleError(ex))
        {
            await Task.Run(() => log.Record("recycle-failed", new { target.Path, Error = ex.ToString() }));
            return new(job, null, ex.Message);
        }
    }

    private async Task RefreshRecycledFoldersAsync(IReadOnlyList<RecycleAttempt> attempts, ScanLog log, CancellationToken token)
    {
        if (attempts.All(attempt => attempt.CancelledBeforeMove)) return;
        while (!_closing)
        {
            await WaitForRecycleRefreshAsync(token);
            var scan = _snapshot;
            string root = scan?.RootPath ?? _currentPath;
            var (requests, survivingParent) = await Task.Run(() => PlanRecycleRefresh(scan, root, attempts), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_snapshot, scan) || scan is null && _currentPath != root) continue;
            if (survivingParent is not null)
            {
                // As with normal publication, leave any open folder menu usable.
                if (RecycleRefreshBlocked(publishing: true))
                {
                    await WaitForRecycleRefreshAsync(token, publishing: true);
                    continue;
                }
                SetScanTarget(survivingParent);
                StatusText.Text = _scanStatus = "Scan this parent folder to continue.";
                return;
            }
            if (scan is null || requests.Length == 0) return;
            try
            {
                if (await RefreshAffectedAsync(scan, requests, "Recycled folders checked.", token, log, background: true)) return;
            }
            catch (Exception ex) when (IsRecycleError(ex))
            {
                await Task.Run(() => log.Record("recycle-refresh-failed", new { Error = ex.ToString() }));
                await WaitForRecycleRefreshAsync(token);
                if (!ReferenceEquals(_snapshot, scan)) continue;
                MarkScanStale("Scan out of date. Recheck or rescan to update results.", requests);
                return;
            }
        }
    }

    private (ScanRecheckRequest[] Requests, string? SurvivingParent) PlanRecycleRefresh(ScanSnapshot? scan, string root,
        IReadOnlyList<RecycleAttempt> attempts)
    {
        var files = Operations.Files;
        if (attempts.Any(attempt => attempt.Result is { Cancelled: false } && CoversRecyclePath(attempt.Job.Target.Path, root))
            && !files.DirectoryExists(root))
        {
            for (string? parent = Path.GetDirectoryName(root); parent is not null; parent = Path.GetDirectoryName(parent))
                if (files.DirectoryExists(parent)) return ([], parent);
        }

        if (scan is null) return ([], null);
        var scopes = new Dictionary<string, bool>(StringComparer.Ordinal);
        void Add(string path, bool recursive = true)
        {
            if (CoversRecyclePath(scan.RootPath, path)) scopes[path] = recursive || scopes.GetValueOrDefault(path);
        }

        void AddParent(string path)
        {
            for (string? parent = Path.GetDirectoryName(path); parent is not null && CoversRecyclePath(scan.RootPath, parent);
                parent = Path.GetDirectoryName(parent))
            {
                if (scan.FindEntry(parent) is null || !files.DirectoryExists(parent)) continue;
                Add(parent, false);
                break;
            }
        }

        foreach (var attempt in attempts)
        {
            if (attempt.CancelledBeforeMove) continue;
            string path = attempt.Job.Target.Path;
            if (CoversRecyclePath(path, scan.RootPath)) Add(scan.RootPath);
            else if (CoversRecyclePath(scan.RootPath, path))
            {
                AddParent(path);
                if (scan.FindEntry(path) is not null && files.DirectoryExists(path)) Add(path);
            }

            string bin = Path.Combine(Path.GetPathRoot(path)!, "$Recycle.Bin");
            if (!CoversRecyclePath(scan.RootPath, bin) && !CoversRecyclePath(bin, scan.RootPath)) continue;
            if (attempt.Result?.RecycledPath is { } destination)
            {
                if (files.DirectoryExists(bin)) Add(bin, false);
                else AddParent(bin);
                string parent = Path.GetDirectoryName(destination)!;
                if (files.DirectoryExists(parent)) Add(parent, false);
                if (files.DirectoryExists(destination)) Add(destination);
                if (CoversRecyclePath(destination, scan.RootPath)) Add(scan.RootPath);
            }
            else if (CoversRecyclePath(bin, scan.RootPath)) Add(scan.RootPath);
            else if (files.DirectoryExists(bin)) Add(bin);
            else AddParent(bin);
        }

        // A shallow check can introduce a new parent needed by a later request.
        return (scopes.OrderBy(scope => scope.Key.Length).Select(scope => new ScanRecheckRequest(scope.Key, scope.Value)).ToArray(), null);
    }

    private static bool CoversRecyclePath(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsRecycleError(Exception ex) => ex is IOException or InvalidDataException or OverflowException
        or UnauthorizedAccessException or Win32Exception or COMException or InvalidOperationException or ArgumentException
        or DllNotFoundException or EntryPointNotFoundException;

    private bool RecycleRefreshBlocked(bool publishing) => _work is not null || _browsing
        || publishing && _activeFolderMenu?.IsOpen == true;

    private async Task WaitForRecycleRefreshAsync(CancellationToken token, bool publishing = false)
    {
        while (RecycleRefreshBlocked(publishing))
        {
            await WaitForRecycleStateChangeAsync().WaitAsync(token);
        }
        token.ThrowIfCancellationRequested();
    }

    private Task WaitForRecycleStateChangeAsync() => (_recycleStateChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private void SignalRecycleStateChange()
    {
        _recycleStateChanged?.TrySetResult();
        _recycleStateChanged = null;
    }

    private static string FolderCount(int count) => $"{count:N0} {(count == 1 ? "folder" : "folders")}";

    private void UpdateRecycleActivity()
    {
        int queued = _queuedRecycles.Count;
        bool canCancel = queued > 0 || _recycleWork is { IsCancellationRequested: false };
        string? path = _recycleTask is null ? _recycleCheck?.Path : _activeRecycle?.Target.Path;
        string? status;
        if (_recycleTask is not null)
        {
            status = path is not null ? $"{(_recycleWork?.IsCancellationRequested == true ? "Stopping" : "Recycling")} {Path.GetFileName(path)}…"
                : queued > 0 ? $"{FolderCount(queued)} queued" : "Updating scan…";
            if (path is not null && queued > 0) status += $" · {queued:N0} queued";
        }
        else status = _recycleCheck?.Status ?? _recycleSummary;

        CancelRecycleButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        RecycleQueueButton.Visibility = queued > 0 || RecycleQueuePopup.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        RecycleStatusText.Text = status ?? "";
        RecycleStatusText.ToolTip = path ?? (_recycleTask is null ? _recycleDetails : null);
        RecycleActivity.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CancelRecycle_Click(object sender, RoutedEventArgs e)
    {
        _recycleWork?.Cancel();
        _queuedRecyclesCancelled += _queuedRecycles.Count;
        DiscardWaitingRecycles();
        UpdateRecycleActivity();
    }

    private void DiscardWaitingRecycles()
    {
        if (_queuedRecycles.Count == 0) return;
        bool restoreFocus = RecycleQueueList.IsKeyboardFocusWithin;
        foreach (var job in _queuedRecycles) job.Completion.TrySetResult(null);
        _queuedRecycles.Clear();
        if (restoreFocus) FocusRecycleQueue();
    }

    private void ShowRecycleQueue_Click(object sender, RoutedEventArgs e)
    {
        if (!_closing && _queuedRecycles.Count > 0) RecycleQueuePopup.IsOpen = true;
    }

    private void RecycleQueue_Opened(object? sender, EventArgs e)
    {
        RecycleQueueList.ItemsSource = _queuedRecycles;
        FocusRecycleQueue();
    }

    private void RecycleQueue_Closed(object? sender, EventArgs e)
    {
        RecycleQueueList.ItemsSource = null;
        if (!_closing) UpdateRecycleActivity();
    }

    private void RecycleQueue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        RecycleQueuePopup.IsOpen = false;
        if (_queuedRecycles.Count > 0 && RecycleQueueButton.IsVisible) RecycleQueueButton.Focus();
        else if (ExploreTab.IsChecked == true && ExplorePanel.IsEnabled) RestoreExplorerFocus(new(null));
        else DiagnosticsToggle.Focus();
    }

    private void RemoveQueuedRecycle_Click(object sender, RoutedEventArgs e)
    {
        // A click can outlive its row. Match the specific waiting job, so an
        // item that has started or has since been requeued is unaffected.
        if (_closing || sender is not Button { DataContext: RecycleJob job }) return;
        int index = _queuedRecycles.IndexOf(job);
        if (index < 0) return;
        RemoveWaitingRecycleAt(index);
        _queuedRecyclesRemoved++;
        job.Completion.TrySetResult(null);
        UpdateRecycleActivity();
    }

    private void RemoveWaitingRecycleAt(int index)
    {
        bool restoreFocus = RecycleQueueList.IsKeyboardFocusWithin && Keyboard.FocusedElement is DependencyObject focused
            && ItemsControl.ContainerFromElement(RecycleQueueList, focused) is ListBoxItem { DataContext: RecycleJob focusedJob }
            && ReferenceEquals(focusedJob, _queuedRecycles[index]);
        _queuedRecycles.RemoveAt(index);
        if (restoreFocus) FocusRecycleQueue();
    }

    private void FocusRecycleQueue()
    {
        if (!RecycleQueuePopup.IsOpen || _closing) return;
        if (_queuedRecycles.Count > 0) RecycleQueueList.Focus();
        else RecycleQueuePanel.Focus();
    }

    internal void MarkScanStale(string message, IEnumerable<ScanRecheckRequest>? scopes = null)
    {
        if (scopes is not null) _staleScopes.UnionWith(scopes);
        else if (_staleScopes.Count == 0) _staleScopes.Add(new(_snapshot!.RootPath));
        _scanStale = true;
        _scanStatus = message;
        StatusText.Text = message;
        TreeNote.Text = "OUT OF DATE · Recheck or rescan";
        UpdateExplorerSizeLabels();
        ScannedSizeLabel.Text = "PREVIOUS SCAN";
        CandidateCaption.Text = "Out of date";
        CandidateText.Text = "—";
        OpportunitiesTab.Content = "Opportunities · Out of date";
        UpdateSelection();
    }
}
