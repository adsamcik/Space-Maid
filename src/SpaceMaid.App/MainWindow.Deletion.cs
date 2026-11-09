using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private sealed record RecycleJob(FolderDeletionTarget Target)
    {
        internal TaskCompletionSource<RecycleResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record RecycleAttempt(RecycleJob Job, RecycleResult? Result, string? Error, bool CancelledBeforeMove = false);

    private readonly Queue<RecycleJob> _queuedRecycles = new();
    private RecycleJob? _activeRecycle;
    private Task? _recycleTask;
    private CancellationTokenSource? _recycleWork;
    private CancellationTokenSource? _recycleRefreshWork;
    private TaskCompletionSource? _recycleStateChanged;
    private bool _scanStale;
    private int _queuedRecyclesCancelled, _foregroundWorkVersion;
    private string? _recycleCheckPath;
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
        await DeleteSelectedFolderAsync();
    }

    internal Task<RecycleResult?> DeleteSelectedFolderAsync()
    {
        if (_snapshot is not { } scan || !CanUseFolderMenu(scan)) return Task.FromResult<RecycleResult?>(null);
        if (_selectionIsGroup)
        {
            StatusText.Text = "Grouped entries cannot be deleted. Select an exact folder in the tree.";
            return Task.FromResult<RecycleResult?>(null);
        }
        return DeleteFolderAsync(scan, _selectedLink);
    }

    private string? RecycleBlockReason(ScanSnapshot scan, int link)
    {
        if (_recycleCheckPath is not null) return "Finish the current recycle confirmation first.";
        if (FolderDeletionTarget.SelectionBlockReason(scan, link) is { } blocked) return blocked;
        string path = scan.EntryPath(link)!;
        bool Overlaps(RecycleJob job) => CoversRecyclePath(job.Target.Path, path) || CoversRecyclePath(path, job.Target.Path);
        return (_activeRecycle is { } active && Overlaps(active)) || _queuedRecycles.Any(Overlaps)
            ? "This folder overlaps a pending recycle request." : null;
    }

    private async Task<RecycleResult?> DeleteFolderAsync(ScanSnapshot scan, int link)
    {
        if (!CanUseFolderMenu(scan)) return null;
        if (RecycleBlockReason(scan, link) is { } blocked)
        {
            StatusText.Text = blocked;
            return null;
        }

        _recycleCheckPath = scan.EntryPath(link)!;
        int workVersion = _foregroundWorkVersion;
        UpdateRecycleActivity();
        RecycleJob job;
        try
        {
            var target = await Operations.CaptureRecycleTargetAsync(scan, link, _extensionLifetime.Token);
            // A refresh can replace the inventory while the live identity check
            // runs. The requested target is already tied to the original row.
            if (_closing || workVersion != _foregroundWorkVersion)
            {
                if (_work is null && !_closing) StatusText.Text = "Select the folder again to recycle it.";
                return null;
            }
            var confirmation = new RecycleFolderWindow(target) { Owner = this };
            if (confirmation.ShowDialog() != true || _closing)
                return null;
            job = new(target);
        }
        catch (OperationCanceledException) when (_closing) { return null; }
        catch (Exception ex) when (IsRecycleError(ex))
        {
            if (!_closing) StatusText.Text = $"Could not check the folder: {ex.Message}";
            return null;
        }
        finally
        {
            _recycleCheckPath = null;
            if (!_closing) UpdateRecycleActivity();
        }

        _queuedRecycles.Enqueue(job);
        _recycleRefreshWork?.Cancel();
        SignalRecycleStateChange();
        if (_recycleTask is null)
        {
            _recycleTask = RunRecycleQueueAsync();
            UpdateUpdateButton();
        }
        UpdateRecycleActivity();
        return await job.Completion.Task;
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
            while (_queuedRecycles.TryDequeue(out var job)) job.Completion.TrySetResult(null);
            _recycleTask = null;
            _queuedRecyclesCancelled = 0;
            SignalRecycleStateChange();
            if (!_closing) { UpdateRecycleActivity(); UpdateUpdateButton(); RefreshDeferredRecycleGroups(); }
        }
    }

    private async Task MoveQueuedFoldersAsync(List<RecycleAttempt> attempts, ScanLog log)
    {
        while (!_closing && _queuedRecycles.TryDequeue(out var job))
        {
            using var work = new CancellationTokenSource();
            _activeRecycle = job;
            _recycleWork = work;
            try
            {
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
        string? path = _recycleTask is null ? _recycleCheckPath : _activeRecycle?.Target.Path;
        string? status;
        if (_recycleTask is not null)
        {
            status = path is not null ? $"{(_recycleWork?.IsCancellationRequested == true ? "Stopping" : "Recycling")} {Path.GetFileName(path)}…"
                : queued > 0 ? $"{FolderCount(queued)} queued" : "Updating scan…";
            if (path is not null && queued > 0) status += $" · {queued:N0} queued";
        }
        else status = path is not null ? $"Checking {Path.GetFileName(path)}…" : _recycleSummary;

        CancelRecycleButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        RecycleStatusText.Text = status ?? "";
        RecycleStatusText.ToolTip = path ?? (_recycleTask is null ? _recycleDetails : null);
        RecycleActivity.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CancelRecycle_Click(object sender, RoutedEventArgs e)
    {
        _recycleWork?.Cancel();
        _queuedRecyclesCancelled += _queuedRecycles.Count;
        while (_queuedRecycles.TryDequeue(out var job)) job.Completion.TrySetResult(null);
        UpdateRecycleActivity();
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
