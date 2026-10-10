using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private bool _deleting, _scanStale;

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
        if (!CanUseFolderMenu(scan)) return null;
        if (_scanStale) { StatusText.Text = "Results are out of date. Rescan before deleting another folder."; return null; }
        _deleting = true; // Guard modal reentrancy as well as repeated key presses.
        using var work = new CancellationTokenSource();
        using var log = StartActionLog("recycle", scan.EntryPath(link) ?? scan.RootPath);
        _work = work;
        bool started = false;
        try
        {
            SetBusy(true);
            // Resolve and validate live metadata off the dispatcher. The
            // selected entry is captured before opening the confirmation.
            var target = await Task.Run(() => FolderDeletionTarget.FromScan(scan, link), work.Token);
            if (_closing || work.IsCancellationRequested) return null;
            var confirmation = new RecycleFolderWindow(target) { Owner = this };
            if (confirmation.ShowDialog() != true || _closing || work.IsCancellationRequested)
            { log.Record("recycle-confirmation-cancelled", new { target.Path }); return null; }
            StatusText.Text = $"Moving to Recycle Bin: {target.Path} · Windows shows progress and cancellation.";
            started = true;
            var result = await FolderRecycler.RecycleAsync(target, new WindowInteropHelper(this).Handle, work.Token);
            if (!_closing)
            {
                log.Record("recycle-result", result);
                var requests = new List<ScanRecheckRequest> { new(Path.GetDirectoryName(target.Path)!, false) };
                if (Directory.Exists(target.Path)) requests.Add(new(target.Path));
                string recycleRoot = Path.Combine(Path.GetPathRoot(target.Path)!, "$Recycle.Bin");
                if (scan.FindEntry(recycleRoot) is not null || scan.FindEntry(Path.GetPathRoot(target.Path)!) is not null)
                {
                    if (result.RecycledPath is { } destination)
                    {
                        requests.Add(new(recycleRoot, false));
                        requests.Add(new(Path.GetDirectoryName(destination)!, false));
                        requests.Add(new(destination));
                    }
                    else requests.Add(new(recycleRoot));
                }
                await RefreshAffectedAsync(scan, requests, result.Cancelled
                    ? "Recycling cancelled; some contents may have moved."
                    : "Folder moved to the Recycle Bin; space stays occupied until the bin is emptied.", work.Token, log);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!_closing && started) MarkScanStale("Recycling cancelled. Some contents may already have moved. Recheck or rescan to refresh the results.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or UnauthorizedAccessException
            or Win32Exception or COMException or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (!_closing)
            {
                string message = $"Could not recycle the folder: {ex.Message}";
                if (started) MarkScanStale(message + " Results may be out of date. Rescan to check current contents.");
                else StatusText.Text = message;
                log.Record("recycle-or-recheck-failed", new { Error = ex.ToString(), started });
            }
            return null;
        }
        finally
        {
            _work = null; _deleting = false;
            if (!_closing) { ShowActionLogError(log); SetBusy(false); }
        }
    }

    internal void MarkScanStale(string message, IEnumerable<ScanRecheckRequest>? scopes = null)
    {
        if (scopes is not null) _staleScopes.UnionWith(scopes);
        else if (_staleScopes.Count == 0) _staleScopes.Add(new(_snapshot!.RootPath));
        _scanStale = true;
        _scanStatus = message;
        StatusText.Text = message;
        TreeNote.Text = "OUT OF DATE · Recheck affected folders or rescan to refresh.";
        MapBasis.Text = _snapshot!.HasAllocationData ? "Previous scan · allocated size" : "Previous scan · logical size";
        ScannedSizeLabel.Text = "PREVIOUS SCAN";
        ScannedSizeCaption.Text = "Out of date · Recheck or rescan to refresh";
        CandidateCaption.Text = "Out of date · Recheck or rescan to recalculate";
        CandidateText.Text = "—";
        OpportunitiesTab.Content = "Opportunities · Out of date";
        UpdateSelection();
    }
}
