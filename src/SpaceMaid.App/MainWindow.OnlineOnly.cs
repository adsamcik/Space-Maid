using System.ComponentModel;
using System.IO;
using System.Windows;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

public partial class MainWindow
{
    internal async Task MakeOnlineOnlyAsync(ScanSnapshot scan, int link)
    {
        if (!CanUseFolderMenu(scan)) return;
        await ReviewOnlineOnlyCoreAsync(scan.EntryPath(link)!, scan, link, default);
    }

    private async Task<CloudStorageReviewResult> ReviewOnlineOnlyCoreAsync(string path, ScanSnapshot? scan, int link, CancellationToken token)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token, _extensionLifetime.Token);
        using var log = StartActionLog("make-online-only", path);
        _work = work; SetBusy(true);
        bool started = false;
        var outcome = new CloudStorageReviewResult("No requests were sent.", false);
        try
        {
            StatusText.Text = "Checking current cloud sync status…";
            var plan = scan is null ? await Operations.PrepareOnlineOnlyAsync(path, work.Token)
                : await Operations.PrepareOnlineOnlyAsync(scan, link, work.Token);
            if (_closing || work.IsCancellationRequested) return outcome;
            log.Record("online-only-prepared", new { plan.Path, plan.Provider, plan.EligibleCount, plan.SkippedCount, plan.LocalBytes, plan.Warnings });
            if (plan.EligibleCount == 0)
            {
                StatusText.Text = "No eligible synced local files were found. Unsynced, local-only and already online-only files were skipped.";
                return new(StatusText.Text, false);
            }
            if (new OnlineOnlyWindow(plan) { Owner = this }.ShowDialog() != true || _closing || work.IsCancellationRequested)
            {
                log.Record("online-only-confirmation-cancelled", new { plan.Path });
                if (!_closing) StatusText.Text = "Online-only request cancelled. No requests were sent.";
                return new("Online-only request cancelled. No requests were sent.", false);
            }
            started = true;
            var progress = new Progress<OnlineOnlyProgress>(update =>
            {
                if (!_closing && ReferenceEquals(_work, work)) StatusText.Text = $"Requesting online-only: {update.Processed:N0} / {update.Total:N0} · {update.Skipped:N0} skipped";
            });
            var result = await Operations.MakeOnlineOnlyAsync(plan, progress, work.Token);
            log.Record("online-only-result", result);
            if (_closing) return new("Requests may still be processing. Recheck the folder.", result.Requested > 0);
            string note = $"{result.Requested:N0} online-only requests accepted · {result.ConfirmedOnlineOnly:N0} confirmed online-only · {result.Skipped:N0} skipped"
                + (result.Cancelled ? " · Cancelled; earlier requests remain active." : ". The provider may still be processing requests.");
            outcome = new(note, result.Requested > 0);
            if (scan is null)
            {
                if (_snapshot is not null && result.Requested > 0) MarkScanStale(note + " Recheck to update sizes.");
                else StatusText.Text = note;
                return outcome;
            }
            string folder = scan.Entry(link).IsDirectory ? plan.Path : Path.GetDirectoryName(plan.Path)!;
            if (result.Cancelled)
            { MarkScanStale(note + " Recheck the folder to update sizes.", [new(folder, scan.Entry(link).IsDirectory)]); return outcome; }
            await RefreshAffectedAsync(scan, [new(folder, scan.Entry(link).IsDirectory)], note, work.Token, log);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or Win32Exception
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            log.Record("online-only-failed", new { Error = ex.ToString(), started });
            if (!_closing)
            {
                string message = ex is OperationCanceledException ? "Online-only operation cancelled." : $"Online-only operation could not finish: {ex.Message}";
                outcome = new(message, started);
                if (started) MarkScanStale(message + " Recheck the folder; accepted requests may still be processing."); else StatusText.Text = message;
            }
        }
        finally { _work = null; if (!_closing) { ShowActionLogError(log); SetBusy(false); } }
        return outcome;
    }

    private sealed class CloudStorageServices(MainWindow window) : ICloudStorageServices
    {
        public Task<CloudDirectoryListing> ReadDirectoryAsync(string path, CancellationToken token)
            => window.Operations.ReadCloudDirectoryAsync(path, token);

        public async Task<CloudStorageReviewResult> ReviewOnlineOnlyAsync(string path, CancellationToken token)
        {
            if (window.Dispatcher.HasShutdownStarted) return new("Space Maid is closing.", false);
            return await window.Dispatcher.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                return window._closing || window._work is not null
                    ? Task.FromResult(new CloudStorageReviewResult("Wait for the current operation to finish.", false))
                    : window.ReviewOnlineOnlyCoreAsync(path, null, -1, token);
            }).Task.Unwrap();
        }
    }
}
