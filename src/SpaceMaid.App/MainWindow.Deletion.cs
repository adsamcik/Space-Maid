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
            if (confirmation.ShowDialog() != true || _closing || work.IsCancellationRequested) return null;
            StatusText.Text = $"Moving to Recycle Bin: {target.Path} · Windows shows progress and cancellation.";
            started = true;
            var result = await FolderRecycler.RecycleAsync(target, new WindowInteropHelper(this).Handle, work.Token);
            if (!_closing)
                MarkScanStale(result.Cancelled
                    ? "Recycling cancelled. Some contents may already have moved. Rescan to refresh the results."
                    : "Folder moved to the Recycle Bin. Rescan to refresh the results; space stays occupied until the bin is emptied.");
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!_closing && started) MarkScanStale("Recycling cancelled. Some contents may already have moved. Rescan to refresh the results.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or COMException or InvalidOperationException or ArgumentException)
        {
            if (!_closing)
            {
                string message = $"Could not recycle the folder: {ex.Message}";
                if (started) MarkScanStale(message + " Results may be out of date. Rescan to check current contents.");
                else StatusText.Text = message;
            }
            return null;
        }
        finally
        {
            _work = null; _deleting = false;
            if (!_closing) SetBusy(false);
        }
    }

    private void MarkScanStale(string message)
    {
        _scanStale = true;
        _scanStatus = message;
        StatusText.Text = message;
        TreeNote.Text = "OUT OF DATE · Sizes show the previous scan. Rescan to refresh.";
        MapBasis.Text = _snapshot!.HasAllocationData ? "Previous scan · allocated size" : "Previous scan · logical size";
        ScannedSizeLabel.Text = "PREVIOUS SCAN";
        ScannedSizeCaption.Text = "Out of date · Rescan to refresh";
        CandidateCaption.Text = "Out of date · Rescan to recalculate";
        CandidateText.Text = "—";
        OpportunityHeading.Text = "Opportunities · Previous scan";
        OpportunitiesTab.Content = "Opportunities · Out of date";
        UpdateSelection();
    }
}
