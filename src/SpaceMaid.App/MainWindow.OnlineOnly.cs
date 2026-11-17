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
        if (!CanOpenEntryMenu(scan)) return;
        await ReviewOnlineOnlyCoreAsync(scan.EntryPath(link)!, scan, link, default);
    }

    private async Task<CloudStorageReviewResult> ReviewOnlineOnlyCoreAsync(string path, ScanSnapshot? scan, int link, CancellationToken token)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token, _extensionLifetime.Token);
        using var log = StartActionLog("make-online-only", path);
        _work = work; SetBusy(true);
        bool started = false;
        ScanRecheckRequest[] affected = [];
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
                outcome = OnlineOnlyPresentation.NoEligible(plan);
                StatusText.Text = OnlineOnlyPresentation.Status(outcome);
                return outcome;
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
            // Capture the provider receipt before any follow-up work can fail.
            outcome = OnlineOnlyPresentation.CompletedWithPreparation(result, plan);
            log.Record("online-only-result", result);
            if (_closing) return outcome;
            string note = OnlineOnlyPresentation.Status(outcome);
            if (scan is null)
            {
                if (_snapshot is not null && result.Requested > 0) MarkScanStale(note + " Recheck to update sizes.");
                else StatusText.Text = note;
                return outcome;
            }
            string folder = scan.Entry(link).IsDirectory ? plan.Path : Path.GetDirectoryName(plan.Path)!;
            affected = [new(folder, scan.Entry(link).IsDirectory)];
            // No submitted request means no provider mutation to refresh.
            if (result.Requested == 0) { StatusText.Text = note; return outcome; }
            if (result.Cancelled)
            { MarkScanStale(note + " Recheck the folder to update sizes.", affected); return outcome; }
            await RefreshAffectedAsync(scan, affected, note, work.Token, log);
            if (_scanStale && !StatusText.Text.StartsWith(note, StringComparison.Ordinal))
                MarkScanStale(note + " " + StatusText.Text);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or Win32Exception
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            log.Record("online-only-failed", new { Error = ex.ToString(), started });
            if (!_closing)
            {
                if (outcome.Receipt is not null)
                {
                    bool rescan = ScanRechecker.RequiresFullRescan(ex);
                    outcome = OnlineOnlyPresentation.RefreshFailed(outcome, ex, rescan);
                    if (_snapshot is not null)
                    {
                        MarkScanStale(OnlineOnlyPresentation.Status(outcome), affected.Length > 0 ? affected : null);
                        if (rescan) OfferFullInventoryRescan(_snapshot);
                    }
                    else StatusText.Text = OnlineOnlyPresentation.Status(outcome);
                    return outcome;
                }
                string message = ex is OperationCanceledException ? "Online-only operation cancelled." : $"Online-only operation could not finish: {ex.Message}";
                outcome = new(message, started);
                if (started && _snapshot is not null) MarkScanStale(message + " Recheck the folder; accepted requests may still be processing."); else StatusText.Text = message;
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
