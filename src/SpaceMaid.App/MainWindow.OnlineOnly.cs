using System.ComponentModel;
using System.IO;
using System.Windows;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    internal async Task MakeOnlineOnlyAsync(ScanSnapshot scan, int link)
    {
        if (!CanUseFolderMenu(scan) || _scanStale) return;
        using var work = new CancellationTokenSource();
        using var log = StartActionLog("make-online-only", scan.EntryPath(link)!);
        _work = work; SetBusy(true);
        bool started = false;
        try
        {
            StatusText.Text = "Checking current cloud sync status…";
            var plan = await Operations.PrepareOnlineOnlyAsync(scan, link, work.Token);
            if (_closing || work.IsCancellationRequested) return;
            log.Record("online-only-prepared", new { plan.Path, plan.Provider, plan.EligibleCount, plan.SkippedCount, plan.LocalBytes, plan.Warnings });
            if (plan.EligibleCount == 0) { StatusText.Text = "No eligible synced local files were found. Unsynced, local-only and already online-only files were skipped."; return; }
            if (new OnlineOnlyWindow(plan) { Owner = this }.ShowDialog() != true || _closing || work.IsCancellationRequested)
            {
                log.Record("online-only-confirmation-cancelled", new { plan.Path });
                if (!_closing) StatusText.Text = "Online-only request cancelled. No requests were sent.";
                return;
            }
            started = true;
            var progress = new Progress<OnlineOnlyProgress>(update =>
            {
                if (!_closing && ReferenceEquals(_work, work)) StatusText.Text = $"Requesting online-only: {update.Processed:N0} / {update.Total:N0} · {update.Skipped:N0} skipped";
            });
            var result = await Operations.MakeOnlineOnlyAsync(plan, progress, work.Token);
            log.Record("online-only-result", result);
            if (_closing) return;
            string note = $"{result.Requested:N0} online-only requests accepted · {result.ConfirmedOnlineOnly:N0} confirmed online-only · {result.Skipped:N0} skipped"
                + (result.Cancelled ? " · Cancelled; earlier requests remain active." : ". The provider may still be processing requests.");
            string folder = scan.Entry(link).IsDirectory ? plan.Path : Path.GetDirectoryName(plan.Path)!;
            if (result.Cancelled)
            { MarkScanStale(note + " Recheck the folder to update sizes.", [new(folder, scan.Entry(link).IsDirectory)]); return; }
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
                if (started) MarkScanStale(message + " Recheck the folder; accepted requests may still be processing."); else StatusText.Text = message;
            }
        }
        finally { _work = null; if (!_closing) { ShowActionLogError(log); SetBusy(false); } }
    }
}
