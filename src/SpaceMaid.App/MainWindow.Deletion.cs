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
    private bool _recycleReview, _scanStale;
    private int _recycleProcessed, _foregroundWorkVersion;
    private string _recyclePhase = "";

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
        if (_recycleReview) return "Finish the current recycle confirmation first.";
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

        _recycleReview = true;
        int workVersion = _foregroundWorkVersion;
        if (_recycleTask is null) ShowRecycleActivity($"Checking {scan.EntryPath(link)}…");
        RecycleJob job;
        try
        {
            var target = await Operations.CaptureRecycleTargetAsync(scan, link, _extensionLifetime.Token);
            // A refresh can replace the inventory while the live identity check
            // runs. The requested target is already tied to the original row.
            if (_closing || workVersion != _foregroundWorkVersion)
            {
                if (_recycleTask is null && !_closing) ShowRecycleActivity("Folder check ended. Select the folder again to recycle it.");
                return null;
            }
            var confirmation = new RecycleFolderWindow(target) { Owner = this };
            if (confirmation.ShowDialog() != true || _closing || _recycleWork?.IsCancellationRequested == true)
            {
                if (_recycleTask is null && !_closing) ShowRecycleActivity("");
                return null;
            }
            job = new(target);
        }
        catch (OperationCanceledException) when (_closing) { return null; }
        catch (Exception ex) when (IsRecycleError(ex))
        {
            if (!_closing) StatusText.Text = $"Could not check the folder: {ex.Message}";
            if (_recycleTask is null && !_closing) ShowRecycleActivity("");
            return null;
        }
        finally { _recycleReview = false; }

        _recycles.Add(job);
        _recycleRefreshWork?.Cancel();
        if (_recycleTask is null)
        {
            _recycleWork = new();
            _recycleTask = RunRecycleQueueAsync(_recycleWork);
            UpdateUpdateButton();
        }
        ShowRecycleActivity(_recyclePhase);
        return await job.Completion.Task;
    }

    private async Task RunRecycleQueueAsync(CancellationTokenSource work)
    {
        _recyclePhase = "Starting recycling…";
        // Let the confirmation close and paint the activity row before starting work.
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            using var log = await Task.Run(() => new ScanLog(LogDirectory));
            var attempts = new List<RecycleAttempt>();
            bool refreshed = true;
            while (!_closing)
            {
                while (attempts.Count < _recycles.Count && !work.IsCancellationRequested)
                {
                    var job = _recycles[attempts.Count];
                    _recyclePhase = $"Recycling {job.Target.Path}";
                    ShowRecycleActivity(_recyclePhase);
                    attempts.Add(await RecycleOneAsync(job, work.Token, log));
                    _recycleProcessed = attempts.Count;
                }

                if (_closing) return;
                _recyclePhase = "Updating scan after recycling…";
                ShowRecycleActivity(_recyclePhase);
                using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_extensionLifetime.Token);
                _recycleRefreshWork = refresh;
                try
                {
                    refreshed = await RefreshRecycledFoldersAsync(attempts, log, refresh.Token);
                    if (attempts.Count == _recycles.Count || work.IsCancellationRequested) break;
                }
                // New moves and foreground work can supersede a slow recheck.
                // The next refresh still reconciles every completed attempt.
                catch (OperationCanceledException) when (refresh.IsCancellationRequested && !_closing) { }
                finally { _recycleRefreshWork = null; }
            }

            if (_closing) return;
            int moved = attempts.Count(attempt => attempt.Result is { Cancelled: false });
            string summary = moved == 0 ? "Recycling finished." : $"{moved:N0} {(moved == 1 ? "folder" : "folders")} moved to the Recycle Bin.";
            if (attempts.Any(attempt => attempt.Result is { Cancelled: true } && !attempt.CancelledBeforeMove))
                summary += " Recycling cancelled. Some contents may already have moved.";
            if (_recycles.Count > attempts.Count || attempts.Any(attempt => attempt.CancelledBeforeMove)) summary += " Queued folders cancelled.";
            if (attempts.FirstOrDefault(attempt => attempt.Error is not null) is { } failure)
                summary += $" {attempts.Count(attempt => attempt.Error is not null):N0} failed. {failure.Job.Target.Path}: {failure.Error}";
            ShowRecycleActivity(refreshed ? summary : summary + " Recheck or rescan to update results.", finished: true);
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
            _recycleProcessed = 0;
            work.Dispose();
            if (!_closing) { UpdateUpdateButton(); RefreshDeferredRecycleGroups(); }
        }
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

    private async Task<bool> RefreshRecycledFoldersAsync(IReadOnlyList<RecycleAttempt> attempts, ScanLog log, CancellationToken token)
    {
        if (attempts.All(attempt => attempt.CancelledBeforeMove)) return true;
        while (!_closing)
        {
            await WaitForRecycleRefreshAsync(token);
            if (_snapshot is not { } scan) return true;
            int version = _foregroundWorkVersion;
            var requests = await Task.Run(() => RecycleRefreshRequests(scan, attempts), token);
            token.ThrowIfCancellationRequested();
            if (version != _foregroundWorkVersion || !ReferenceEquals(_snapshot, scan)) continue;
            if (requests.Length == 0) return true;
            try
            {
                if (await RefreshAffectedAsync(scan, requests, "Recycled folders checked.", token, log, background: true)) return true;
            }
            catch (Exception ex) when (IsRecycleError(ex))
            {
                await Task.Run(() => log.Record("recycle-refresh-failed", new { Error = ex.ToString() }));
                await WaitForRecycleRefreshAsync(token);
                if (version != _foregroundWorkVersion || !ReferenceEquals(_snapshot, scan)) continue;
                MarkScanStale("Recycling finished. Recheck or rescan to update results.", requests);
                return false;
            }
        }
        return false;
    }

    private ScanRecheckRequest[] RecycleRefreshRequests(ScanSnapshot scan, IReadOnlyList<RecycleAttempt> attempts)
    {
        var scopes = new Dictionary<string, bool>(StringComparer.Ordinal);
        var files = Operations.Files;
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
        return scopes.OrderBy(scope => scope.Key.Length).Select(scope => new ScanRecheckRequest(scope.Key, scope.Value)).ToArray();
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

    private void ShowRecycleActivity(string message, bool finished = false)
    {
        int pending = _recycles.Count - _recycleProcessed;
        bool active = !finished && pending > 0 && _recycleWork is { IsCancellationRequested: false };
        CancelRecycleButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        RecycleStatusText.Text = active && pending > 1 ? $"{pending:N0} pending · {message}" : message;
        RecycleActivity.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CancelRecycle_Click(object sender, RoutedEventArgs e)
    {
        _recycleWork?.Cancel();
        ShowRecycleActivity("Stopping recycling…");
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
