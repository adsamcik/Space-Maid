using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private readonly ObservableCollection<EntryDeletionJob> _deletionJobs = [];
    private readonly LinkedList<EntryDeletionJob> _pendingDeletions = [];
    private readonly CancellationTokenSource _deletionLifetime = new();
    private readonly SemaphoreSlim _deletionConfirmation = new(1);
    private readonly List<RemovalChange> _removalChanges = [];
    private EntryDeletionJob? _activeDeletion;
    private CancellationTokenSource? _activeDeletionCancellation, _deletionRefreshCancellation;
    private Task? _deletionWorker, _deletionRefresh;
    private DeletionQueueWindow? _deletionWindow;
    private int _deletionAdmissions;
    private bool _scanStale;

    private int RunningAndWaitingDeletions => _pendingDeletions.Count + (_activeDeletion is null ? 0 : 1);
    private bool HasDeletionWork => _deletionAdmissions > 0 || RunningAndWaitingDeletions > 0;
    internal bool ExploreDeletionsIdle => !HasDeletionWork && _deletionWorker is null && _deletionRefresh is null
        && _removalChanges.Count == 0;

    internal Task<RecycleResult?> DeleteSelectedEntryAsync(bool permanent = false)
    {
        if (_snapshot is not { } scan || !CanOpenEntryMenu(scan)) return Task.FromResult<RecycleResult?>(null);
        if (_selectionIsGroup)
        {
            StatusText.Text = "Grouped entries cannot be deleted. Select an individual file or folder in the tree.";
            return Task.FromResult<RecycleResult?>(null);
        }
        return DeleteEntryAsync(scan, _selectedLink, permanent);
    }

    private Task<RecycleResult?> DeleteEntryAsync(ScanSnapshot scan, int link, bool permanent = false) =>
        AwaitDeletionAsync(PrepareDeletionAsync(scan, link, permanent));

    private static async Task<RecycleResult?> AwaitDeletionAsync(Task<EntryDeletionJob?> admission)
    {
        var job = await admission;
        return job is null ? null : await job.Completion.Task;
    }

    private async Task<EntryDeletionJob?> PrepareDeletionAsync(ScanSnapshot scan, int link, bool permanent)
    {
        if (!CanUseEntryMenu(scan, link) || scan.EntryPath(link) is not { } path) return null;
        if (DeletionAlreadyQueued(path, permanent)) return null;
        _deletionAdmissions++;
        UpdateDeletionQueue();
        try
        {
            var target = await Operations.PrepareExploreDeletionAsync(scan, link, _deletionLifetime.Token);
            if (_closing || DeletionAlreadyQueued(target.Path, permanent)) return null;
            if (permanent && !await ConfirmQueuedDeletionAsync(target)) return null;
            if (_closing || DeletionAlreadyQueued(target.Path, permanent)) return null;
            return EnqueueDeletion(target, permanent, new(scan));
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) when (CleanupBatch.IsExpected(ex))
        {
            if (!_closing)
            {
                var failed = new EntryDeletionJob(path, permanent);
                failed.SetState(EntryDeletionState.Failed, ex.Message);
                _deletionJobs.Insert(RunningAndWaitingDeletions, failed);
            }
        }
        finally { _deletionAdmissions--; UpdateDeletionQueue(); }
        return null;
    }

    private bool DeletionAlreadyQueued(string path, bool permanent)
    {
        var covered = _pendingDeletions.Prepend(_activeDeletion).FirstOrDefault(job =>
            job is not null && (job.Path == path || job.Target!.IsDirectory && IsWithinDeletionPath(path, job.Path)));
        if (covered is null) return false;
        string action = covered.Permanent ? "permanent deletion" : "the Recycle Bin";
        string message = covered.Path == path ? $"This item is already queued for {action}."
            : $"This item is covered by the queued folder {covered.Name}, which is set for {action}.";
        if (permanent != covered.Permanent) message += " Cancel the queued request to change its action.";
        if (_work is null) StatusText.Text = message;
        return true;
    }

    private static bool IsWithinDeletionPath(string path, string folder, StringComparison comparison = StringComparison.Ordinal) =>
        path.Equals(folder, comparison) || path.StartsWith(Path.EndsInDirectorySeparator(folder)
            ? folder : folder + Path.DirectorySeparatorChar, comparison);

    private async Task<bool> ConfirmQueuedDeletionAsync(DeletionTarget target, string? warning = null, Window? owner = null)
    {
        await _deletionConfirmation.WaitAsync(_deletionLifetime.Token);
        try
        {
            if (_closing || DeletionAlreadyQueued(target.Path, permanent: true)) return false;
            var confirmation = new PermanentDeleteWindow(target, warning) { Owner = owner?.IsVisible == true ? owner : this };
            return confirmation.ShowDialog() == true && !_closing;
        }
        finally { _deletionConfirmation.Release(); }
    }

    private EntryDeletionJob EnqueueDeletion(DeletionTarget target, bool permanent, WeakReference<ScanSnapshot>? source)
    {
        var job = new EntryDeletionJob(target.Path, permanent) { Target = target, Source = source };
        int index = RunningAndWaitingDeletions;
        _pendingDeletions.AddLast(job);
        _deletionJobs.Insert(index, job);
        _deletionRefreshCancellation?.Cancel();
        _deletionWorker ??= RunDeletionQueueAsync();
        UpdateDeletionQueue();
        return job;
    }

    private async Task RunDeletionQueueAsync()
    {
        // Publish the queue and let the initiating input event finish first.
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            while (!_closing && _pendingDeletions.First is { Value: var job })
            {
                _pendingDeletions.RemoveFirst();
                using var cancellation = new CancellationTokenSource();
                _activeDeletion = job;
                _activeDeletionCancellation = cancellation;
                job.SetState(EntryDeletionState.Running);
                UpdateDeletionQueue();
                var operations = Operations;
                var owner = new WindowInteropHelper(this).Handle;
                string logDirectory = LogDirectory;
                var result = await Task.Run(() => ExecuteDeletionAsync(operations, owner, logDirectory, job, cancellation.Token));
                job.SetState(result.State, result.Detail);
                if (!_closing && (result.Removed.HasValue || result.MayHaveChanged))
                    _removalChanges.Add(new(job.Path, job.Target!.IsDirectory, job.Permanent, job.Source, result.Removed, result.Recycle?.RecycledPath));
                job.Completion.TrySetResult(result.Recycle);
                _activeDeletionCancellation = null;
                _activeDeletion = null;
                MoveDeletionToResults(job);
                UpdateDeletionQueue();
            }
        }
        finally
        {
            _deletionWorker = null;
            TryRefreshAfterDeletions();
        }
    }

    private sealed record DeletionResult(EntryDeletionState State, string Detail, bool MayHaveChanged,
        EntryRemoval? Removed = null, RecycleResult? Recycle = null);

    private static async Task<DeletionResult> ExecuteDeletionAsync(FileOperations operations, nint owner,
        string logDirectory, EntryDeletionJob job, CancellationToken token)
    {
        var target = job.Target!;
        bool mayHaveChanged = false;
        using var log = new ScanLog(logDirectory);
        log.Record("file-action-start", new { Action = job.Action, job.Path });
        try
        {
            if (job.Permanent)
            {
                await operations.DeletePermanentlyAsync(target, token, () => mayHaveChanged = true);
                return Finish(EntryDeletionState.Completed, "Permanently deleted.", EntryRemoval.Deleted);
            }
            else
            {
                var recycle = await operations.RecycleAsync(target, owner, token, (name, data) =>
                {
                    if (name == "recycle-native-start") mayHaveChanged = true;
                    log.Record(name, data);
                });
                if (recycle.Cancelled)
                    return Finish(EntryDeletionState.Cancelled, RecheckAfterPartial("Recycling stopped."), recycle: recycle);
                return Finish(EntryDeletionState.Completed, "Moved to the Recycle Bin. You can restore it there.", EntryRemoval.Recycled, recycle);
            }
        }
        catch (RecycleUnavailableException error)
        {
            mayHaveChanged = error.MayHaveMoved;
            return Finish(EntryDeletionState.RecycleRefused, RecheckAfterPartial(error.Message));
        }
        catch (OperationCanceledException)
        {
            return Finish(EntryDeletionState.Cancelled, RecheckAfterPartial(job.Permanent ? "Deletion stopped." : "Recycling stopped."));
        }
        catch (Exception error)
        {
            log.Record("entry-action-failed", new { Error = error.ToString(), job.Permanent, mayHaveChanged });
            return Finish(EntryDeletionState.Failed, RecheckAfterPartial(error.Message));
        }

        DeletionResult Finish(EntryDeletionState state, string detail, EntryRemoval? removed = null, RecycleResult? recycle = null)
        {
            log.Record("entry-action-result", new { job.Path, State = state.ToString(), Detail = detail, recycle });
            return new(state, detail, mayHaveChanged, removed, recycle);
        }

        string RecheckAfterPartial(string message) => mayHaveChanged
            ? message + " Some changes may have occurred. Check this location before trying again." : message;
    }

    private void CancelQueuedDeletion(EntryDeletionJob job)
    {
        if (_pendingDeletions.Remove(job))
        {
            job.SetState(EntryDeletionState.Cancelled, "Cancelled before it started.");
            job.Completion.TrySetResult(null);
            MoveDeletionToResults(job);
        }
        else if (ReferenceEquals(job, _activeDeletion) && job.State == EntryDeletionState.Running)
        {
            job.SetState(EntryDeletionState.Stopping);
            _activeDeletionCancellation?.Cancel();
        }
        UpdateDeletionQueue();
    }

    private void MoveDeletionToResults(EntryDeletionJob job)
    {
        int index = _deletionJobs.IndexOf(job);
        if (index != RunningAndWaitingDeletions) _deletionJobs.Move(index, RunningAndWaitingDeletions);
    }

    private async Task ReviewRefusedDeletionAsync(EntryDeletionJob job)
    {
        if (_closing || job.State != EntryDeletionState.RecycleRefused || job.Target is not { } target) return;
        _deletionAdmissions++;
        try
        {
            job.SetState(EntryDeletionState.Reviewing, job.Detail);
            var token = _deletionLifetime.Token;
            await Task.Run(target.ValidateCurrent, token);
            if (await ConfirmQueuedDeletionAsync(target, job.Detail, _deletionWindow) && !DeletionAlreadyQueued(job.Path, permanent: true))
            {
                EnqueueDeletion(target, permanent: true, job.Source);
                _deletionJobs.Remove(job);
            }
            else job.SetState(EntryDeletionState.RecycleRefused, job.Detail);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        { job.SetState(EntryDeletionState.Failed, error.Message); }
        finally { _deletionAdmissions--; UpdateDeletionQueue(); }
    }

    private void DeletionQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_deletionWindow is not null) { _deletionWindow.Activate(); return; }
        _deletionWindow = new(_deletionJobs, CancelQueuedDeletion, ReviewRefusedDeletionAsync, ClearDeletionResults) { Owner = this };
        _deletionWindow.Closed += (_, _) => _deletionWindow = null;
        UpdateDeletionQueue();
        _deletionWindow.Show();
    }

    private void ClearDeletionResults()
    {
        foreach (var job in _deletionJobs.Where(job => job.CanClear).ToArray()) _deletionJobs.Remove(job);
        UpdateDeletionQueue();
    }

    private void UpdateDeletionQueue()
    {
        if (_closing) return;
        int active = RunningAndWaitingDeletions + _deletionAdmissions;
        int attention = _deletionJobs.Count(job => job.State is EntryDeletionState.Failed or EntryDeletionState.RecycleRefused);
        DeletionQueueButton.Visibility = active > 0 || _deletionJobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeletionQueueButton.Content = active > 0 ? $"Deletions · {active:N0} active" : attention > 0
            ? $"Deletions · {attention:N0} to review" : "Deletions";
        DeletionQueueButton.ToolTip = "View queued deletions and results";
        _deletionWindow?.UpdateSummary(_pendingDeletions.Count, _activeDeletion is not null, _deletionAdmissions,
            _deletionJobs.Any(job => job.CanClear));
        UpdateUpdateButton();
    }

    private bool ConfirmDeletionShutdown() => !HasDeletionWork || MessageBox.Show(this,
        "Cancel waiting deletions and stop the current deletion? Changes already made will stay in place.",
        "Deletions in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task StopDeletionsAsync()
    {
        _deletionLifetime.Cancel();
        _activeDeletionCancellation?.Cancel();
        _deletionRefreshCancellation?.Cancel();
        foreach (var job in _pendingDeletions) job.Completion.TrySetResult(null);
        _pendingDeletions.Clear();
        if (_deletionWorker is { } worker) await worker;
        if (_deletionRefresh is { } refresh) await refresh;
    }

    private sealed record RemovalChange(string Path, bool IsDirectory, bool Permanent, WeakReference<ScanSnapshot>? Source,
        EntryRemoval? Removed, string? RecycledPath);

    private void TryRefreshAfterDeletions()
    {
        if (_closing || _work is not null || _browsing || _openingPath || _deletionWorker is not null
            || _deletionRefresh is not null || _removalChanges.Count == 0) return;
        _deletionRefresh = RefreshAfterDeletionsAsync();
    }

    private async Task RefreshAfterDeletionsAsync()
    {
        using var work = new CancellationTokenSource();
        _deletionRefreshCancellation = work;
        await Dispatcher.Yield(DispatcherPriority.Background);
        var changes = _removalChanges.ToArray();
        try
        {
            work.Token.ThrowIfCancellationRequested();
            if (IsLoaded && changes.Any(change => !change.Permanent)) _ = RefreshRecycleBinAsync();
            if (_snapshot is not { } scan) return;
            var files = Operations.Files;
            var (scopes, rootGone) = await Task.Run(() =>
            {
                var scopes = DeletionRefreshScopes(scan, changes, work.Token);
                bool rootAffected = changes.Any(change => change.IsDirectory && change.Removed.HasValue
                    && IsWithinDeletionPath(scan.RootPath, change.Path, StringComparison.OrdinalIgnoreCase));
                return (scopes, rootAffected && !files.DirectoryExists(scan.RootPath));
            }, work.Token);
            if (scopes.Length == 0) return;
            await WaitForExplorerBrowseAsync(work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_snapshot, scan)) return;

            foreach (var change in changes)
                if (change.Removed is { } removal)
                {
                    _staleScopes.RemoveWhere(scope => IsWithinDeletionPath(scope.Path, change.Path));
                    if (change.Source?.TryGetTarget(out var source) == true && ReferenceEquals(source, scan)
                        && scan.FindEntry(change.Path) is { } link && scan.EntryPath(link) == change.Path)
                        MarkExplorerEntryRemoved(link, removal);
                }

            var last = changes[^1];
            string name = Path.GetFileName(last.Path);
            string note = changes.Length > 1 ? "Deletion queue processed."
                : last.Removed == EntryRemoval.Deleted ? $"Permanently deleted {name}."
                : last.Removed == EntryRemoval.Recycled ? $"Moved {name} to the Recycle Bin. You can restore it there."
                : "Deletion stopped before its result could be verified.";
            if (rootGone || !scan.HasAllocationData)
            {
                MarkScanStale(note + (rootGone ? " Scan another folder to update results." : " Rescan to update results."), scopes);
                return;
            }

            using var log = new ScanLog(LogDirectory);
            try { await RefreshAffectedAsync(scan, scopes, note, work.Token, log, background: true); }
            catch (Exception error) when (error is not OperationCanceledException && CleanupBatch.IsExpected(error))
            {
                log.Record("entry-refresh-failed", new { Error = error.ToString() });
                MarkScanStale(note + " Refresh failed. Recheck to update results.", scopes);
                if (ScanRechecker.RequiresFullRescan(error)) OfferFullInventoryRescan(scan);
            }
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        finally
        {
            if (!work.IsCancellationRequested) _removalChanges.RemoveRange(0, changes.Length);
            _deletionRefreshCancellation = null;
            _deletionRefresh = null;
            TryRefreshAfterDeletions();
        }
    }

    private static ScanRecheckRequest[] DeletionRefreshScopes(ScanSnapshot scan, RemovalChange[] changes, CancellationToken token)
    {
        var scopes = new List<ScanRecheckRequest>();
        var removedFolders = changes.Where(change => change.IsDirectory && change.Removed.HasValue)
            .Select(change => change.Path).ToArray();
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            if (IsWithinDeletionPath(scan.RootPath, change.Path, StringComparison.OrdinalIgnoreCase))
                scopes.Add(new(scan.RootPath));
            else if (IsWithinDeletionPath(change.Path, scan.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                for (string? parent = Path.GetDirectoryName(change.Path); parent is not null; parent = Path.GetDirectoryName(parent))
                    if (scan.FindEntry(parent) is { } link && scan.EntryPath(link) is { } scanPath
                        && !removedFolders.Any(folder => IsWithinDeletionPath(scanPath, folder)))
                    { scopes.Add(new(scanPath, false)); break; }
                if (change.IsDirectory && !change.Removed.HasValue && scan.FindEntry(change.Path) is { } affected
                    && scan.EntryPath(affected) is { } affectedPath
                    && !removedFolders.Any(folder => IsWithinDeletionPath(affectedPath, folder)))
                    scopes.Add(new(affectedPath));
            }
            if (change.Permanent) continue;
            string volume = Path.GetPathRoot(change.Path)!;
            string bin = Path.Combine(volume, "$Recycle.Bin");
            if (scan.FindEntry(bin) is null && scan.FindEntry(volume) is null) continue;
            if (change.RecycledPath is { } destination && scan.FindEntry(bin) is not null)
            {
                scopes.Add(new(bin, false));
                scopes.Add(new(Path.GetDirectoryName(destination)!, false));
                if (change.IsDirectory) scopes.Add(new(destination));
            }
            else scopes.Add(new(bin));
        }
        return scopes.GroupBy(scope => scope.Path, StringComparer.Ordinal)
            .Select(group => new ScanRecheckRequest(group.Key, group.Any(scope => scope.Recursive))).ToArray();
    }

    private void MarkExplorerEntryRemoved(int link, EntryRemoval removal)
    {
        if (_explorer is not { } explorer) return;
        bool expanded = explorer.IsExpanded(link);
        var viewport = expanded ? CaptureTreeViewport() : null;
        bool selectionRemoved = _selectedLink == link || expanded && AncestorLinks(explorer.Scan, _selectedLink).Contains(link);
        _syncTree = true;
        try
        {
            explorer.MarkRemoved(link, removal);
            if (selectionRemoved)
            {
                _selectedLink = link;
                _selectionIsGroup = false;
                EntryDetail.DataContext = new ExplorerRow(explorer.Scan, explorer.Scan.Entry(link), 0, false) { Removal = removal };
                EntryDetail.Visibility = SelectedEntryPath.Visibility = Visibility.Visible;
            }
            SpaceTree.SelectedIndex = explorer.Find(_selectedLink);
            if (expanded) RestoreTreeViewport(viewport);
        }
        finally { _syncTree = false; }
    }

    internal void MarkScanStale(string message, IEnumerable<ScanRecheckRequest>? scopes = null)
    {
        if (scopes is not null) _staleScopes.UnionWith(scopes);
        else if (_staleScopes.Count == 0) _staleScopes.Add(new(_snapshot!.RootPath));
        _scanStale = true;
        _scanStatus = message;
        StatusText.Text = message;
        TreeNote.Text = "OUT OF DATE · Recheck or rescan";
        MapBasis.Text = _snapshot!.HasAllocationData ? "Previous scan · allocated size" : "Previous scan · logical size";
        ScannedSizeLabel.Text = "PREVIOUS SCAN";
        ScannedSizeCaption.Text = "Out of date";
        CandidateCaption.Text = "Out of date";
        CandidateText.Text = "—";
        OpportunitiesTab.Content = "Opportunities · Out of date";
        UpdateSelection();
    }
}
