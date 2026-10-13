using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private RecycleBinSnapshot? _recycleBin;
    private int _recycleBinVersion;

    internal async Task RefreshRecycleBinAsync()
    {
        if (_closing) return;
        string root = Path.GetPathRoot(_currentPath) ?? "";
        int version = ++_recycleBinVersion;
        if (_recycleBin?.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase) != true)
        {
            _recycleBin = null;
            RecycleBinSize.Text = "—"; RecycleBinCount.Text = "Checking…";
            EmptyRecycleBinButton.IsEnabled = false;
        }
        RecycleBinTitle.Text = "Recycle Bin · " + root;
        try
        {
            var bin = await Operations.QueryRecycleBinAsync(root);
            if (_closing || version != _recycleBinVersion) return;
            _recycleBin = bin;
            RecycleBinSize.Text = SizeText.Format(bin.Bytes);
            RecycleBinCount.Text = bin.Items == 0 ? "Empty" : $"{bin.Items:N0} {(bin.Items == 1 ? "item" : "items")}";
            RecycleBinRow.ToolTip = "Your Recycle Bin on this drive. Emptying permanently deletes its current contents.";
            EmptyRecycleBinButton.IsEnabled = bin.Items > 0 && _work is null && !_browsing && !_openingPath;
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        {
            if (_closing || version != _recycleBinVersion) return;
            _recycleBin = null;
            RecycleBinSize.Text = "—"; RecycleBinCount.Text = "Unavailable";
            RecycleBinRow.ToolTip = error.Message; EmptyRecycleBinButton.IsEnabled = false;
        }
    }

    private async void EmptyRecycleBin_Click(object sender, RoutedEventArgs e) => await EmptySelectedRecycleBinAsync();
    private async void RefreshRecycleBin_Click(object sender, RoutedEventArgs e) => await RefreshRecycleBinAsync();

    internal async Task EmptySelectedRecycleBinAsync()
    {
        if (_closing || _work is not null || _browsing || _openingPath) return;
        var task = EmptyRecycleBinCoreAsync(); _cleanupTask = task;
        try { await task; }
        finally { if (ReferenceEquals(_cleanupTask, task)) _cleanupTask = null; UpdateUpdateButton(); }
    }

    private async Task EmptyRecycleBinCoreAsync()
    {
        string root = Path.GetPathRoot(_currentPath) ?? "";
        var scan = _snapshot; string? scannedVolume = _snapshotVolumeId, confirmedVolume = null;
        var operations = Operations;
        using var work = new CancellationTokenSource();
        using var log = StartActionLog("empty-recycle-bin", root);
        _work = work; ++_recycleBinVersion;
        SetBusy(true); RecycleBinRow.IsEnabled = false;
        bool started = false;
        string message = "Recycle Bin unchanged.";
        try
        {
            StatusText.Text = "Checking Recycle Bin…";
            var bin = await operations.QueryRecycleBinAsync(root); confirmedVolume = bin.VolumeId;
            if (_closing || work.IsCancellationRequested) return;
            if (bin.Items == 0) { message = "Recycle Bin is empty."; return; }
            var confirmation = new EmptyRecycleBinWindow(bin) { Owner = this };
            bool accepted;
            using (work.Token.Register(() => Dispatcher.BeginInvoke(new Action(() =>
                { if (confirmation.IsVisible) confirmation.Close(); }))))
                accepted = confirmation.ShowDialog() == true;
            if (!accepted || _closing || work.IsCancellationRequested) return;
            log.Record("recycle-bin-confirmed", bin);
            // Windows owns its progress UI. Its Shell call has no managed
            // cancellation hook, so keep the owner alive until the call returns.
            CancelButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "Emptying Recycle Bin on " + bin.RootPath + "…";
            started = true;
            var result = await operations.EmptyRecycleBinAsync(bin, new WindowInteropHelper(this).Handle, work.Token, log.Record);
            message = result.Remaining.Items == 0 ? "Recycle Bin is empty."
                : $"{result.Remaining.Items:N0} items remain in the Recycle Bin.";
            if (result.Cancelled) message = "Cleanup cancelled. " + message;
            log.Record("recycle-bin-result", result);
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        {
            log.Record("recycle-bin-failed", new { Started = started, Error = error.ToString() });
            message = error is OperationCanceledException ? "Recycle Bin cleanup cancelled."
                : "Could not empty Recycle Bin. " + error.Message;
        }
        finally
        {
            try
            {
                if (!_closing)
                {
                    RefreshPathDriveSpace();
                    if (started && scan is not null && ReferenceEquals(scan, _snapshot))
                    {
                        string binPath = Path.Combine(root, "$Recycle.Bin");
                        IReadOnlyList<ScanRecheckRequest> requests = [];
                        try
                        {
                            using var identity = WindowsIdentity.GetCurrent();
                            string sid = identity.User?.Value ?? throw new IOException("Windows account could not be checked.");
                            string userBin = Path.Combine(binPath, sid);
                            requests = RecycleBinRefreshScopes(scan, binPath, userBin, Directory.Exists(userBin), Directory.Exists(binPath));
                            if (requests.Count > 0)
                            {
                                var current = await operations.QueryRecycleBinAsync(root);
                                if (scannedVolume is null || !scannedVolume.Equals(confirmedVolume, StringComparison.OrdinalIgnoreCase)
                                    || !current.VolumeId.Equals(confirmedVolume, StringComparison.OrdinalIgnoreCase))
                                    throw new IOException("Drive identity changed. Rescan to update results.");
                                await RefreshAffectedAsync(scan, requests, message, work.Token, log);
                            }
                        }
                        catch (Exception error) when (CleanupBatch.IsExpected(error))
                        {
                            log.Record("recycle-bin-recheck-failed", new { Error = error.ToString() });
                            if (requests.Count > 0 || scan.FindEntry(binPath) is not null)
                                MarkScanStale(message + " Recheck to update results.", requests.Count > 0 ? requests : [new(binPath)]);
                        }
                    }
                    StatusText.Text = message + (_scanStale && started ? " Recheck to update results." : "");
                    ShowActionLogError(log);
                }
            }
            finally
            {
                _work = null;
                if (!_closing) SetBusy(false);
            }
        }
    }

    internal static IReadOnlyList<ScanRecheckRequest> RecycleBinRefreshScopes(ScanSnapshot scan, string binPath, string userBin,
        bool userBinExists = true, bool binExists = true)
        => userBinExists && scan.FindEntry(userBin) is not null ? [new(userBin)]
            : scan.FindEntry(binPath) is not null ? [new(binExists ? binPath : Path.GetPathRoot(binPath)!, false)] : [];
}
