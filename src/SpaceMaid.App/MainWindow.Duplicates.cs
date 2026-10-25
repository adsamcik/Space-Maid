using System.IO;
using System.Windows;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private DuplicateWindow? _duplicateWindow;

    private void Duplicates_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _duplicateWindow is not null || !CanRestartForUpdate()) return;
        if (_snapshot is not { } scan) { StatusText.Text = "Scan a location before comparing duplicates."; return; }
        try { OptimizationPolicy.NormalizePath(scan.RootPath); }
        catch (ArgumentException) { StatusText.Text = "Duplicate content comparison supports local drives and folders."; return; }
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_extensionLifetime.Token);
        using var log = StartActionLog("duplicates-and-storage", scan.RootPath);
        _work = work; SetBusy(true);
        _duplicateWindow = new DuplicateWindow(scan, _optimizationSettings.Policy.ExcludedPaths,
            paths =>
            {
                if (_closing) return;
                MarkScanStale("Storage changed. Scan again to refresh allocations.", paths.Select(p => new ScanRecheckRequest(Path.GetDirectoryName(p)!, false)));
                RefreshPathDriveSpace();
            }, RevealExtensionLocation, work.Token, Operations, (name, data) =>
            {
                log.Record(name, data);
                if (log.FilePath is null || log.Error is not null) throw new IOException("Storage changes require a writable activity log.");
            }) { Owner = this };
        try { _duplicateWindow.ShowDialog(); }
        finally { _duplicateWindow = null; _work = null; if (!_closing) SetBusy(false); }
    }
}
