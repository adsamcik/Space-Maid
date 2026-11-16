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
    private bool _recycling, _scanStale;
    private Task<RecycleResult>? _folderRecycleTask;

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

    private async Task<RecycleResult?> DeleteFolderAsync(ScanSnapshot scan, int link)
    {
        if (!CanUseFolderEntry(scan, link)) return null;
        using var work = new CancellationTokenSource();
        using var log = StartActionLog("recycle", scan.EntryPath(link) ?? scan.RootPath);
        _work = work;
        bool started = false;
        RecycleResult? outcome = null;
        var requests = new List<ScanRecheckRequest>();
        try
        {
            SetBusy(true);
            StatusText.Text = "Checking selected folder…";
            var target = await Operations.PrepareRecycleAsync(scan, link, work.Token);
            if (_closing || work.IsCancellationRequested || !ReferenceEquals(_snapshot, scan)) return null;
            var confirmation = new RecycleFolderWindow(target) { Owner = this };
            if (confirmation.ShowDialog() != true || _closing || work.IsCancellationRequested)
            {
                log.Record("recycle-confirmation-cancelled", new { target.Path });
                if (!_closing) StatusText.Text = "Folder unchanged.";
                return null;
            }
            string name = scan.Entry(link).Name;
            StatusText.Text = $"Moving {name} to the Recycle Bin…";
            _recycling = true;
            ExplorePanel.IsEnabled = true;
            started = true;
            string parent = Path.GetDirectoryName(target.Path)!;
            string volume = Path.GetPathRoot(target.Path)!;
            string recycleRoot = Path.Combine(volume, "$Recycle.Bin");
            requests.Add(new(link == -1 ? scan.RootPath : parent, link == -1));
            bool recycleBinInScan = scan.FindEntry(recycleRoot) is not null || scan.FindEntry(volume) is not null;
            var binScope = new ScanRecheckRequest(recycleRoot);
            if (recycleBinInScan) requests.Add(binScope);
            _folderRecycleTask = Operations.RecycleAsync(target, new WindowInteropHelper(this).Handle, work.Token, diagnostic: log.Record);
            var result = await _folderRecycleTask;
            outcome = result;
            if (!_closing)
            {
                log.Record("recycle-result", result);
                if (recycleBinInScan && result.RecycledPath is { } destination)
                {
                    requests.Remove(binScope);
                    requests.Add(new(recycleRoot, false));
                    requests.Add(new(Path.GetDirectoryName(destination)!, false));
                    requests.Add(new(destination));
                }
                string note = $"Moved {name} to the Recycle Bin. Empty the bin to free space.";
                if (!result.Cancelled)
                {
                    StatusText.Text = $"Moved {name} to the Recycle Bin. Updating scan results…";
                    // A confirmed move survives cancellation of the refresh.
                    await WaitForExplorerBrowseAsync(CancellationToken.None);
                    if (_closing || !ReferenceEquals(_snapshot, scan)) return result;
                    MarkExplorerFolderRecycled(link);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (_closing || !ReferenceEquals(_snapshot, scan)) return result;
                    if (link == -1 || !scan.HasAllocationData)
                    {
                        MarkScanStale(note + (link == -1 ? " Scan another folder to update results."
                            : " Rescan to update results."), requests);
                        return result;
                    }
                }
                else
                {
                    note = "Recycling cancelled. Some contents may already have moved.";
                    StatusText.Text = "Recycling cancelled. Updating scan results…";
                    if (link != -1 && await Task.Run(() => Operations.Files.DirectoryExists(target.Path), work.Token))
                        requests.Add(new(target.Path));
                }
                await RefreshAffectedAsync(scan, requests, note, work.Token, log);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!_closing)
            {
                if (started) MarkScanStale(outcome is { Cancelled: false }
                    ? "Folder moved. Refresh cancelled; recheck to update results."
                    : "Recycling cancelled. Some contents may already have moved. Recheck or rescan to refresh the results.", requests);
                else StatusText.Text = "Folder check cancelled. Nothing was moved.";
            }
            return outcome;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or UnauthorizedAccessException
            or Win32Exception or COMException or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (!_closing)
            {
                string message = outcome is { Cancelled: false } ? "Folder moved. Refresh failed; recheck to update results."
                    : $"Could not recycle the folder: {ex.Message}";
                if (started) MarkScanStale(message + " Results may be out of date. Rescan to check current contents.", requests);
                else StatusText.Text = message;
                log.Record(outcome is { Cancelled: false } ? "recycle-refresh-failed" : "recycle-or-recheck-failed", new { Error = ex.ToString(), started });
            }
            return outcome;
        }
        finally
        {
            _folderRecycleTask = null;
            _work = null; _recycling = false;
            if (!_closing) { ShowActionLogError(log); SetBusy(false); }
        }
    }

    private void MarkExplorerFolderRecycled(int link)
    {
        if (_explorer is not { } explorer) return;
        bool expanded = explorer.IsExpanded(link);
        var viewport = expanded ? CaptureTreeViewport() : null;
        bool selectionRemoved = _selectedLink == link || expanded && AncestorLinks(explorer.Scan, _selectedLink).Contains(link);
        _syncTree = true;
        try
        {
            explorer.MarkRecycled(link);
            if (selectionRemoved)
            {
                _selectedLink = link;
                _selectionIsGroup = false;
                EntryDetail.DataContext = new ExplorerRow(explorer.Scan, explorer.Scan.Entry(link), 0, false) { IsRecycled = true };
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
