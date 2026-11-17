using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private bool _removingEntry, _scanStale;
    private Task? _entryRemovalTask;

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

    private async Task<RecycleResult?> DeleteEntryAsync(ScanSnapshot scan, int link, bool permanent = false)
    {
        if (!CanUseEntryMenu(scan, link)) return null;
        var entry = scan.Entry(link);
        string path = scan.EntryPath(link) ?? scan.RootPath;
        string kind = entry.IsDirectory ? "folder" : "file";
        string mayHaveMoved = entry.IsDirectory ? "Some contents may already have moved." : "The file may already have moved.";
        using var work = new CancellationTokenSource();
        using var log = StartActionLog(permanent ? "delete-permanently" : "recycle", path);
        _work = work;
        bool mayHaveChanged = false;
        EntryRemoval? completed = null;
        RecycleResult? recycle = null;
        var requests = new List<ScanRecheckRequest>();
        try
        {
            SetBusy(true);
            StatusText.Text = $"Checking selected {kind}…";
            var target = await Operations.PrepareExploreDeletionAsync(scan, link, work.Token);
            if (_closing || work.IsCancellationRequested || !ReferenceEquals(_snapshot, scan)) return null;

            requests.Add(link == -1 ? new(scan.RootPath) : new(Path.GetDirectoryName(target.Path)!, false));
            string? recycleWarning = null;
            if (!permanent)
            {
                string volume = Path.GetPathRoot(target.Path)!;
                string recycleRoot = Path.Combine(volume, "$Recycle.Bin");
                bool binInScan = scan.FindEntry(recycleRoot) is not null || scan.FindEntry(volume) is not null;
                var binScope = new ScanRecheckRequest(recycleRoot);
                if (binInScan) requests.Add(binScope);
                BeginRemoval($"Moving {entry.Name} to the Recycle Bin…");
                mayHaveChanged = true;
                try
                {
                    var task = Operations.RecycleAsync(target, new WindowInteropHelper(this).Handle, work.Token, diagnostic: log.Record);
                    _entryRemovalTask = task;
                    recycle = await task;
                    if (!recycle.Cancelled) completed = EntryRemoval.Recycled;
                    log.Record("recycle-result", recycle);
                    if (binInScan && recycle.RecycledPath is { } destination)
                    {
                        requests.Remove(binScope);
                        requests.Add(new(recycleRoot, false));
                        requests.Add(new(Path.GetDirectoryName(destination)!, false));
                        if (entry.IsDirectory) requests.Add(new(destination));
                    }
                }
                catch (RecycleUnavailableException error)
                {
                    _entryRemovalTask = null;
                    mayHaveChanged = error.MayHaveMoved;
                    if (!mayHaveChanged) requests.Remove(binScope);
                    log.Record("recycle-unavailable", new { target.Path, error.MayHaveMoved });
                    recycleWarning = mayHaveChanged ? $"{error.Message} {mayHaveMoved}" : error.Message;
                    permanent = true;
                }
            }

            if (permanent)
            {
                if (!ConfirmPermanentDelete(target, recycleWarning))
                {
                    log.Record("permanent-delete-confirmation-cancelled", new { target.Path });
                    if (!_closing)
                    {
                        if (mayHaveChanged) MarkIncomplete($"Recycling stopped. {mayHaveMoved}");
                        else StatusText.Text = "Deletion cancelled.";
                    }
                    return null;
                }

                BeginRemoval($"Permanently deleting {entry.Name}…");
                _entryRemovalTask = Operations.DeletePermanentlyAsync(target, work.Token, () => mayHaveChanged = true);
                await _entryRemovalTask;
                completed = EntryRemoval.Deleted;
                log.Record("permanent-delete-completed", new { target.Path });
            }

            if (_closing) return recycle;
            if (completed is { } removal)
                await RefreshCompletedRemovalAsync(target, removal);
            else
            {
                string note = $"Recycling cancelled. {mayHaveMoved}";
                StatusText.Text = "Recycling cancelled. Updating scan results…";
                if (entry.IsDirectory && link != -1 && await Task.Run(() => Operations.Files.DirectoryExists(target.Path), work.Token))
                    requests.Add(new(target.Path));
                if (scan.HasAllocationData) await RefreshAffectedAsync(scan, requests, note, work.Token, log);
                else MarkIncomplete(note);
            }
            return recycle;
        }
        catch (OperationCanceledException)
        {
            if (!_closing)
            {
                if (completed is { } removal)
                    MarkScanStale(CompletionText(removal) + " Refresh cancelled. Recheck to update results.", requests);
                else if (mayHaveChanged)
                {
                    string message = permanent ? "Deletion stopped." : $"Recycling stopped. {mayHaveMoved}";
                    if (permanent && entry.IsDirectory) message += " Some contents may have been removed.";
                    MarkIncomplete(message);
                }
                else StatusText.Text = "Deletion cancelled.";
            }
            return recycle;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or UnauthorizedAccessException
            or Win32Exception or COMException or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            log.Record(completed.HasValue ? "entry-refresh-failed" : "entry-action-failed", new { Error = ex.ToString(), permanent, mayHaveChanged });
            if (!_closing)
            {
                string message = $"Could not {(permanent ? "finish deleting" : "recycle")} the {kind}. {ex.Message}";
                if (completed is { } removal)
                    MarkScanStale(CompletionText(removal) + " Refresh failed. Recheck to update results.", requests);
                else if (mayHaveChanged) MarkIncomplete(message);
                else StatusText.Text = message;
            }
            return recycle;
        }
        finally
        {
            _entryRemovalTask = null;
            _work = null; _removingEntry = false;
            if (!_closing) { ShowActionLogError(log); SetBusy(false); }
        }

        bool ConfirmPermanentDelete(DeletionTarget target, string? recycleWarning)
        {
            if (_closing || work.IsCancellationRequested) return false;
            var confirmation = new PermanentDeleteWindow(target, recycleWarning) { Owner = this };
            return confirmation.ShowDialog() == true && !_closing && !work.IsCancellationRequested;
        }

        void BeginRemoval(string message)
        {
            _removingEntry = true;
            ExplorePanel.IsEnabled = !_browsing;
            StatusText.Text = message;
        }

        string CompletionText(EntryRemoval removal) => removal == EntryRemoval.Recycled
            ? $"Moved {entry.Name} to the Recycle Bin."
            : $"Permanently deleted {entry.Name}.";

        async Task RefreshCompletedRemovalAsync(DeletionTarget target, EntryRemoval removal)
        {
            string note = CompletionText(removal);
            StatusText.Text = note + " Updating scan results…";
            // A completed removal survives cancellation of the refresh.
            await WaitForExplorerBrowseAsync(CancellationToken.None);
            if (_closing || !ReferenceEquals(_snapshot, scan)) return;
            _staleScopes.RemoveWhere(scope => scope.Path.Equals(target.Path, StringComparison.Ordinal)
                || scope.Path.StartsWith(target.Path + "\\", StringComparison.Ordinal));
            MarkExplorerEntryRemoved(link, removal);
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_closing || !ReferenceEquals(_snapshot, scan)) return;
            if (removal == EntryRemoval.Recycled) note += " You can restore it from the Recycle Bin.";
            if (link == -1 || !scan.HasAllocationData)
                MarkScanStale(note + (link == -1 ? " Scan another folder to update results." : " Rescan to update results."), requests);
            else
                await RefreshAffectedAsync(scan, requests, note, work.Token, log);
        }

        void MarkIncomplete(string message)
        {
            if (entry.IsDirectory && link != -1 && !requests.Contains(new(path))) requests.Add(new(path));
            MarkScanStale(message + " Recheck or rescan to update results.", requests);
        }
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
