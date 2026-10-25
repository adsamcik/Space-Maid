using System.IO;
using System.Windows;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private CacheMaintenanceWindow? _toolCachesWindow;
    private Task<ToolCacheRunResult>? _toolCacheTask;

    private void ToolCaches_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _work is not null || _browsing || _toolCachesWindow is not null) return;
        try
        {
            var service = new ToolCacheService(AppData.File(Path.Join("NativeTools", "Inspection")));
            _toolCachesWindow = new(service, _snapshot?.RootPath ?? _currentPath,
                (plan, token) => ExecuteToolCacheAsync(service, plan, token), _extensionLifetime.Token) { Owner = this };
            try { _toolCachesWindow.ShowDialog(); }
            finally { _toolCachesWindow = null; }
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = "Tool caches unavailable. " + error.Message; }
    }

    private async Task<ToolCacheRunResult> ExecuteToolCacheAsync(ToolCacheService service, ToolCachePlan plan, CancellationToken token)
    {
        if (_closing || _work is not null || _toolCacheTask is not null || !CanRestartForUpdate())
            throw new InvalidOperationException("Wait for current work before maintaining a tool cache.");
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token, _extensionLifetime.Token);
        using var log = new ScanLog(Path.Join(LogDirectory, "ToolCaches"));
        if (log.FilePath is null || log.Error is not null) throw new IOException("Native cache maintenance requires a writable activity log.");
        _work = work; SetBusy(true);
        try
        {
            log.Record("tool-cache-confirmed", new { plan.Action, plan.Scopes, plan.Observation.Version, plan.CommandPreview });
            if (log.Error is not null) throw new IOException("The activity log could not record this maintenance action.");
            var task = service.ExecuteAsync(plan, work.Token, log.Record); _toolCacheTask = task;
            var result = await task;
            if (!_closing)
            {
                if (_snapshot is { } scan && plan.AffectedPaths.Select(path => OptimizationPolicy.Covers(path, scan.RootPath) ? scan.RootPath
                        : OptimizationPolicy.Covers(scan.RootPath, path) ? path : null).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() is { Length: > 0 } affected)
                    MarkScanStale(result.Summary + " Scan again to refresh cache sizes.", affected.Select(path => new ScanRecheckRequest(path)));
                else StatusText.Text = result.Summary;
                RefreshPathDriveSpace();
            }
            return result;
        }
        finally { _toolCacheTask = null; _work = null; if (!_closing) SetBusy(false); }
    }
}
