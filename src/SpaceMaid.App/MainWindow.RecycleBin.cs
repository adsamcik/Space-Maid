using SpaceMaid.Extensions.Contracts;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Core.IO;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private RecycleBinSnapshot? _recycleBin;
    private int _recycleBinVersion;
    private OpportunityRecycleBinState? _recycleBinPresentation;
    private void ShowRecycleBin(string root, string size, string count, bool canEmpty, string hint = "Size and count reported by Windows for your account. Cleanup also checks for leftovers.")
    {
        _recycleBinPresentation = new("Recycle Bin · " + root, size, count, hint, canEmpty);
        OpportunityBrowser?.UpdateRecycleBin(_recycleBinPresentation);
    }

    internal async Task RefreshRecycleBinAsync()
    {
        if (_closing) return;
        string root = Path.GetPathRoot(_currentPath) ?? "";
        int version = ++_recycleBinVersion;
        if (_recycleBin?.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase) != true)
        {
            _recycleBin = null;
            ShowRecycleBin(root, "—", "Checking…", false);
        }
        try
        {
            var bin = await Operations.QueryRecycleBinAsync(root);
            if (_closing || version != _recycleBinVersion) return;
            _recycleBin = bin;
            ShowRecycleBin(root, SizeText.Format(bin.Bytes), bin.Items == 0 ? "No listed items" : $"{bin.Items:N0} {(bin.Items == 1 ? "item" : "items")}",
                _work is null && !_browsing && !_openingPath);
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        {
            if (_closing || version != _recycleBinVersion) return;
            _recycleBin = null;
            ShowRecycleBin(root, "—", "Unavailable", false, error.Message);
        }
    }

    internal Task EmptySelectedRecycleBinAsync() => EmptyRecycleBinAsync(Path.GetPathRoot(_currentPath) ?? "");

    private async Task ReviewRecycleLeftoversAsync(string root)
    {
        if (_closing || _work is not null || _browsing || _openingPath || _recycleTask is not null) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_extensionLifetime.Token);
        _work = work;
        SetBusy(true);
        try
        {
            var dialog = new RecycleLeftoversWindow(Operations, root, work.Token) { Owner = this };
            dialog.ShowDialog();
            if (!_closing && dialog.Changed)
            {
                RefreshPathDriveSpace();
                if (_snapshot is not null) MarkScanStale("Recycle Bin storage changed. Recheck affected folders or rescan.");
            }
        }
        finally { _work = null; if (!_closing) SetBusy(false); }
    }

    private async Task EmptyRecycleBinAsync(string root)
    {
        if (_closing || _work is not null || _browsing || _openingPath) return;
        if (_recycleTask is not null)
        {
            StatusText.Text = "Wait for queued moves to finish before cleaning up the Recycle Bin.";
            return;
        }
        var task = EmptyRecycleBinCoreAsync(root); _cleanupTask = task;
        try { await task; }
        finally { if (ReferenceEquals(_cleanupTask, task)) _cleanupTask = null; UpdateUpdateButton(); }
    }

    private async Task EmptyRecycleBinCoreAsync(string root)
    {
        const string emptyMessage = "Windows lists no items in your Recycle Bin.";
        var scan = _snapshot; string? scannedVolume = _snapshotVolumeId, confirmedVolume = null;
        var operations = Operations;
        using var work = new CancellationTokenSource();
        using var log = StartActionLog("empty-recycle-bin", root);
        _work = work; ++_recycleBinVersion;
        SetBusy(true);
        bool started = false, leftoversChanged = false;
        string message = "Recycle Bin unchanged.";
        try
        {
            StatusText.Text = "Checking Recycle Bin…";
            var bin = await operations.QueryRecycleBinAsync(root); confirmedVolume = bin.VolumeId;
            if (_closing || work.IsCancellationRequested) return;
            DiskAccess.RecycleLeftoverReview? leftovers = null;
            string? leftoverNotice = null;
            StatusText.Text = "Checking for untracked Recycle Bin files…";
            try
            {
                if (await operations.HasRecycleLeftoversAsync(bin, work.Token))
                    leftovers = await Task.Run(() => operations.ReviewRecycleLeftovers(bin, work.Token), work.Token);
            }
            catch (Exception error) when (CleanupBatch.IsExpected(error) && error is not OperationCanceledException)
            {
                log.Record("recycle-bin-leftovers-check-failed", new { Error = error.ToString() });
                leftoverNotice = "Untracked files could not be checked and will be kept. You can still empty Windows-listed items.";
            }
            if (_closing || work.IsCancellationRequested) return;
            if (bin.Items == 0 && leftovers?.Entries.Count is not > 0 && leftoverNotice is null)
            { message = emptyMessage + " No files with missing or corrupted Recycle Bin records found."; return; }

            var binLink = string.Equals(scannedVolume, bin.VolumeId, StringComparison.OrdinalIgnoreCase)
                ? scan?.FindEntry(Path.Combine(root, "$Recycle.Bin")) ?? scan?.FindEntry(Path.Combine(root, "Recycler")) : null;
            long? scanBytes = scan is not null && binLink is { } link && scan.Entry(link) is { IsDirectory: true } entry
                ? scan.HasAllocationData ? entry.AllocatedBytes : entry.LogicalBytes : null;
            var confirmation = new EmptyRecycleBinWindow(bin, scanBytes, scan?.HasAllocationData == true, leftovers, leftoverNotice);
            if (!Confirm(confirmation)) return;
            var approved = confirmation.DeleteLeftovers ? leftovers!.Entries.Where(item => item.CanDelete).ToArray() : [];
            int kept = (leftovers?.Entries.Count ?? 0) - approved.Length;
            log.Record("recycle-bin-confirmed", new { Bin = bin, UntrackedEntries = approved.Select(item => item.Name).ToArray() });
            if (bin.Items > 0)
            {
                // The Shell call has no managed cancellation hook. Retain its owner until it returns.
                CancelButton.Visibility = Visibility.Collapsed;
                StatusText.Text = "Emptying Recycle Bin on " + bin.RootPath + "…";
                started = true;
                var result = await operations.EmptyRecycleBinAsync(bin, new WindowInteropHelper(this).Handle, work.Token, log.Record);
                message = result.FailureHResult is not null ? "Windows could not finish its cleanup. See Details."
                    : result.Remaining.Items == 0 ? "Windows-listed items cleared." : "Windows did not clear every listed item.";
                log.Record("recycle-bin-result", result);
                if (result.Cancelled)
                {
                    message = "Cleanup cancelled. " + message;
                    if (approved.Length > 0) message += " Untracked files were not deleted.";
                    return;
                }
                bin = result.Remaining;
            }
            else message = emptyMessage;

            if (_closing || work.IsCancellationRequested) return;
            CancelButton.Visibility = Visibility.Visible;
            if (approved.Length > 0)
            {
                StatusText.Text = "Deleting untracked Recycle Bin files…";
                int deleted = 0, partial = 0;
                var progress = new Progress<DiskAccess.RecycleLeftoverResult>(result =>
                {
                    if (result.Completed) deleted++;
                    else if (result.MutationAttempted) partial++;
                    log.Record("recycle-bin-untracked-result", result);
                });
                // A failed directory deletion can still have removed children.
                leftoversChanged = true;
                try { await operations.DeleteRecycleLeftoversAsync(bin, leftovers!, approved, progress, work.Token); }
                catch (Exception error) when (CleanupBatch.IsExpected(error))
                {
                    log.Record("recycle-bin-untracked-failed", new { Error = error.ToString() });
                    message += error is OperationCanceledException ? " Untracked cleanup stopped." : " Untracked cleanup could not finish. See Details.";
                }
                kept += approved.Length - deleted;
                message += $" {deleted:N0} untracked {(deleted == 1 ? "entry" : "entries")} deleted.";
                if (partial > 0) message += $" {partial:N0} could not be fully deleted; some contents may have been removed. See Details.";
            }
            if (kept > 0) message += $" {kept:N0} untracked {(kept == 1 ? "entry remains" : "entries remain")} in the bin. Review leftovers for details.";
            if (leftoverNotice is not null) message += " Untracked files were not checked.";
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
                    if ((started || leftoversChanged) && scan is not null && ReferenceEquals(scan, _snapshot))
                    {
                        string binPath = Path.Combine(root, "$Recycle.Bin");
                        IReadOnlyList<ScanRecheckRequest> requests = [];
                        try
                        {
                            using var identity = WindowsIdentity.GetCurrent();
                            string sid = identity.User?.Value ?? throw new IOException("Windows account could not be checked.");
                            string userBin = Path.Combine(binPath, sid);
                            requests = RecycleBinRefreshScopes(scan, binPath, userBin, operations.Files.DirectoryExists(userBin), operations.Files.DirectoryExists(binPath));
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
                    StatusText.Text = message + (_scanStale && (started || leftoversChanged) ? " Recheck to update results." : "");
                    ShowActionLogError(log);
                }
            }
            finally
            {
                _work = null;
                if (!_closing) SetBusy(false);
            }
        }

        bool Confirm(EmptyRecycleBinWindow dialog)
        {
            dialog.Owner = this;
            using var registration = work.Token.Register(() => Dispatcher.BeginInvoke(new Action(() =>
                { if (dialog.IsVisible) dialog.Close(); })));
            return dialog.ShowDialog() == true && !_closing && !work.IsCancellationRequested;
        }
    }

    internal static IReadOnlyList<ScanRecheckRequest> RecycleBinRefreshScopes(ScanSnapshot scan, string binPath, string userBin,
        bool userBinExists = true, bool binExists = true)
        => userBinExists && scan.FindEntry(userBin) is not null ? [new(userBin)]
            : scan.FindEntry(binPath) is not null ? [new(binExists ? binPath : Path.GetPathRoot(binPath)!, false)] : [];
}
