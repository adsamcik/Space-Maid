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
    private bool _removingEntry, _scanStale;
    private Task? _entryRemovalTask;

    private async void Explore_KeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key != Key.Delete || modifiers is not (ModifierKeys.None or ModifierKeys.Shift) || e.IsRepeat
            || ExploreTab.IsChecked != true) return;
        // Delete in a path field retains normal text-selection behavior.
        for (DependencyObject? source = e.OriginalSource as DependencyObject; source is not null;
             source = VisualTreeHelper.GetParent(source))
            if (source is TextBoxBase) return;
        e.Handled = true;
        await DeleteSelectedEntryAsync(permanent: modifiers == ModifierKeys.Shift);
    }

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
        bool isDirectory = scan.Entry(link).IsDirectory;
        string kind = isDirectory ? "folder" : "file";
        string kindLabel = isDirectory ? "Folder" : "File";
        string mayHaveMoved = isDirectory ? "Some contents may already have moved." : "The file may already have moved.";
        using var work = new CancellationTokenSource();
        using var log = StartActionLog(permanent ? "delete-permanently" : "recycle", scan.EntryPath(link) ?? scan.RootPath);
        _work = work;
        bool started = false;
        bool deleted = false;
        bool recycleMayHaveMoved = false;
        RecycleResult? outcome = null;
        var requests = new List<ScanRecheckRequest>();
        try
        {
            SetBusy(true);
            StatusText.Text = $"Checking selected {kind}…";
            var target = await Operations.PrepareExploreDeletionAsync(scan, link, work.Token);
            if (_closing || work.IsCancellationRequested || !ReferenceEquals(_snapshot, scan)) return null;
            if (!Confirm(target)) return null;
            string name = scan.Entry(link).Name;
            _removingEntry = true;
            ExplorePanel.IsEnabled = true;
            string parent = Path.GetDirectoryName(target.Path)!;
            requests.Add(new(link == -1 ? scan.RootPath : parent, link == -1));
            string volume = Path.GetPathRoot(target.Path)!;
            string recycleRoot = Path.Combine(volume, "$Recycle.Bin");
            bool recycleBinInScan = scan.FindEntry(recycleRoot) is not null || scan.FindEntry(volume) is not null;
            var binScope = new ScanRecheckRequest(recycleRoot);
            if (!permanent)
            {
                StatusText.Text = $"Moving {name} to the Recycle Bin…";
                if (recycleBinInScan) requests.Add(binScope);
                started = true;
                try
                {
                    var recycleTask = Operations.RecycleAsync(target, new WindowInteropHelper(this).Handle, work.Token, diagnostic: log.Record);
                    _entryRemovalTask = recycleTask;
                    outcome = await recycleTask;
                    log.Record("recycle-result", outcome);
                }
                catch (RecycleUnavailableException error)
                {
                    started = false;
                    _entryRemovalTask = null;
                    recycleMayHaveMoved = error.MayHaveMoved;
                    if (!recycleMayHaveMoved) requests.Remove(binScope);
                    log.Record("recycle-unavailable", new { target.Path, error.MayHaveMoved });
                    permanent = true;
                    string warning = recycleMayHaveMoved ? $"{error.Message} {mayHaveMoved}" : error.Message;
                    if (_closing || work.IsCancellationRequested || !Confirm(target, warning))
                    {
                        if (!_closing && recycleMayHaveMoved)
                            MarkPartial($"Recycling stopped. {mayHaveMoved} Recheck the folder.");
                        return null;
                    }
                }
            }
            if (permanent)
            {
                StatusText.Text = $"Permanently deleting {name}…";
                _entryRemovalTask = Operations.DeletePermanentlyAsync(target, work.Token, () => started = true);
                await _entryRemovalTask;
                deleted = true;
                log.Record("permanent-delete-completed", new { target.Path });
            }
            if (!_closing)
            {
                if (recycleBinInScan && outcome?.RecycledPath is { } destination)
                {
                    requests.Remove(binScope);
                    requests.Add(new(recycleRoot, false));
                    requests.Add(new(Path.GetDirectoryName(destination)!, false));
                    if (isDirectory) requests.Add(new(destination));
                }
                string completed = deleted ? $"Permanently deleted {name}." : $"Moved {name} to the Recycle Bin.";
                string note = deleted ? completed : completed + " Empty the bin to free space.";
                if (outcome is not { Cancelled: true })
                {
                    StatusText.Text = completed + " Updating scan results…";
                    // A completed removal survives cancellation of the refresh.
                    await WaitForExplorerBrowseAsync(CancellationToken.None);
                    if (_closing || !ReferenceEquals(_snapshot, scan)) return outcome;
                    _staleScopes.RemoveWhere(scope => scope.Path.Equals(target.Path, StringComparison.Ordinal)
                        || scope.Path.StartsWith(target.Path + "\\", StringComparison.Ordinal));
                    MarkExplorerEntryRemoved(link, deleted ? EntryRemoval.Deleted : EntryRemoval.Recycled);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (_closing || !ReferenceEquals(_snapshot, scan)) return outcome;
                    if (link == -1 || !scan.HasAllocationData)
                    {
                        MarkScanStale(note + (link == -1 ? " Scan another folder to update results."
                            : " Rescan to update results."), requests);
                        return outcome;
                    }
                }
                else
                {
                    note = $"Recycling cancelled. {mayHaveMoved}";
                    StatusText.Text = "Recycling cancelled. Updating scan results…";
                    if (isDirectory && link != -1 && await Task.Run(() => Operations.Files.DirectoryExists(target.Path), work.Token))
                        requests.Add(new(target.Path));
                }
                await RefreshAffectedAsync(scan, requests, note, work.Token, log);
            }
            return outcome;
        }
        catch (OperationCanceledException)
        {
            if (!_closing)
            {
                if (deleted || outcome is { Cancelled: false })
                    MarkScanStale((deleted ? $"{kindLabel} permanently deleted." : $"{kindLabel} moved to the Recycle Bin.")
                        + " Refresh cancelled. Recheck to update results.", requests);
                else if (started || recycleMayHaveMoved)
                    MarkPartial(permanent && started
                        ? isDirectory ? "Deletion stopped. Some items may already be permanently deleted. Recheck or rescan to update results."
                            : "Deletion stopped. Recheck the folder to see whether the file is still present."
                        : $"Recycling stopped. {mayHaveMoved} Recheck or rescan to update results.");
                else StatusText.Text = $"Cancelled. {kindLabel} unchanged.";
            }
            return outcome;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or UnauthorizedAccessException
            or Win32Exception or COMException or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (!_closing)
            {
                bool completed = deleted || outcome is { Cancelled: false };
                string message = completed ? (deleted ? $"{kindLabel} permanently deleted." : $"{kindLabel} moved to the Recycle Bin.")
                    + " Refresh failed. Recheck to update results." : permanent
                    ? $"Could not finish deleting the {kind}. {ex.Message}" : $"Could not recycle the {kind}: {ex.Message}";
                if (permanent && started && !completed && isDirectory)
                    message = "Deletion was incomplete. Some items may already be permanently deleted. " + ex.Message;
                log.Record(completed ? "entry-refresh-failed" : "entry-action-failed", new { Error = ex.ToString(), permanent, started });
                if (completed) MarkScanStale(message, requests);
                else if (started || recycleMayHaveMoved) MarkPartial(message + " Recheck or rescan to update results.");
                else StatusText.Text = message;
            }
            return outcome;
        }
        finally
        {
            _entryRemovalTask = null;
            _work = null; _removingEntry = false;
            if (!_closing) { ShowActionLogError(log); SetBusy(false); }
        }

        bool Confirm(DeletionTarget target, string? recycleWarning = null)
        {
            var confirmation = new RecycleEntryWindow(target, permanent, recycleWarning) { Owner = this };
            if (confirmation.ShowDialog() == true && !_closing && !work.IsCancellationRequested) return true;
            log.Record("entry-confirmation-cancelled", new { target.Path, permanent });
            if (!_closing) StatusText.Text = $"{kindLabel} unchanged.";
            return false;
        }

        void MarkPartial(string message)
        {
            if (isDirectory && permanent && link != -1) requests.Add(new(scan.EntryPath(link)!));
            MarkScanStale(message, requests);
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
