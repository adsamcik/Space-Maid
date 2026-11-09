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

    private readonly List<RecycleJob> _recycles = [];
    private Task? _recycleTask;
    private CancellationTokenSource? _recycleWork;
    private CancellationTokenSource? _recycleRefreshWork;
    private TaskCompletionSource? _recycleRefreshIdle;
    private bool _scanStale;
    private int _recycleProcessed, _foregroundWorkVersion;
    private string? _recycleCheckPath, _activeRecyclePath;
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
        if (_recycleWork?.IsCancellationRequested == true) return "Recycling is stopping.";
        if (FolderDeletionTarget.SelectionBlockReason(scan, link) is { } blocked) return blocked;
        string path = scan.EntryPath(link)!;
        return _recycles.Any(job => CoversRecyclePath(job.Target.Path, path) || CoversRecyclePath(path, job.Target.Path))
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
            if (confirmation.ShowDialog() != true || _closing || _recycleWork?.IsCancellationRequested == true)
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

        _recycles.Add(job);
        _recycleRefreshWork?.Cancel();
        if (_recycleTask is null)
        {
            _recycleWork = new();
            _recycleTask = RunRecycleQueueAsync(_recycleWork);
            UpdateUpdateButton();
        }
        UpdateRecycleActivity();
        return await job.Completion.Task;
    }

    private async Task RunRecycleQueueAsync(CancellationTokenSource work)
    {
        // Let the confirmation close and paint the activity row before starting work.
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            using var log = await Task.Run(() => new ScanLog(LogDirectory));
            var attempts = new List<RecycleAttempt>();
            while (!_closing)
            {
                while (attempts.Count < _recycles.Count && !work.IsCancellationRequested)
                {
                    var job = _recycles[attempts.Count];
                    _activeRecyclePath = job.Target.Path;
                    UpdateRecycleActivity();
                    attempts.Add(await RecycleOneAsync(job, work.Token, log));
                    _recycleProcessed = attempts.Count;
                }

                if (_closing) return;
                _activeRecyclePath = null;
                UpdateRecycleActivity();
                using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_extensionLifetime.Token);
                _recycleRefreshWork = refresh;
                try
                {
                    await RefreshRecycledFoldersAsync(attempts, log, refresh.Token);
                    if (attempts.Count == _recycles.Count || work.IsCancellationRequested) break;
                }
                // New moves and foreground work can supersede a slow recheck.
                // The next refresh still reconciles every completed attempt.
                catch (OperationCanceledException) when (refresh.IsCancellationRequested && !_closing) { }
                finally { _recycleRefreshWork = null; }
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
            foreach (var job in _recycles) job.Completion.TrySetResult(null);
            _recycles.Clear();
            _recycleWork = null;
            _recycleTask = null;
            _activeRecyclePath = null;
            _recycleProcessed = 0;
            work.Dispose();
            if (!_closing) { UpdateRecycleActivity(); UpdateUpdateButton(); RefreshDeferredRecycleGroups(); }
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
        int skipped = _recycles.Count - attempts.Count + attempts.Count(attempt => attempt.CancelledBeforeMove);
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
            _recycleRefreshIdle ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            await _recycleRefreshIdle.Task.WaitAsync(token);
        }
        token.ThrowIfCancellationRequested();
    }

    private void WakeRecycleRefresh()
    {
        _recycleRefreshIdle?.TrySetResult();
        _recycleRefreshIdle = null;
    }

    private static string FolderCount(int count) => $"{count:N0} {(count == 1 ? "folder" : "folders")}";

    private void UpdateRecycleActivity()
    {
        int pending = _recycles.Count - _recycleProcessed;
        bool canCancel = pending > 0 && _recycleWork is { IsCancellationRequested: false };
        string? path = _recycleWork is null ? _recycleCheckPath : _activeRecyclePath;
        string? status;
        if (_recycleWork is { } work)
        {
            status = path is not null ? $"{(work.IsCancellationRequested ? "Stopping" : "Recycling")} {Path.GetFileName(path)}…"
                : canCancel ? $"{FolderCount(pending)} queued" : "Updating scan…";
            if (path is not null && canCancel && pending > 1) status += $" · {pending - 1:N0} queued";
        }
        else status = path is not null ? $"Checking {Path.GetFileName(path)}…" : _recycleSummary;

        CancelRecycleButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        RecycleStatusText.Text = status ?? "";
        RecycleStatusText.ToolTip = path ?? (_recycleWork is null ? _recycleDetails : null);
        RecycleActivity.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CancelRecycle_Click(object sender, RoutedEventArgs e)
    {
        _recycleWork?.Cancel();
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
