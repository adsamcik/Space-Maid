using System.IO;
using System.Windows;
using System.Windows.Threading;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    internal string OptimizationSettingsPath { get; set; } = AppData.File("optimization-settings.json");
    private OptimizationSettings _optimizationSettings = new(new());
    private string? _optimizationSettingsError;
    private DispatcherTimer? _optimizationTimer;
    private OptimizationWindow? _optimizationWindow;
    private Task<OptimizationResult>? _optimizationTask;

    internal void InitializeOptimization()
    {
        try { _optimizationSettings = OptimizationSettings.Load(OptimizationSettingsPath); _optimizationSettingsError = null; }
        catch (Exception error) when (SettingsError(error))
        { _optimizationSettingsError = "Optimization settings unavailable. Automatic cleanup is off; save settings to restore it."; }
        // Disabled mode has no recurring work, including timer wakeups.
        UpdateOptimizationTimer();
    }

    private void UpdateOptimizationTimer()
    {
        _optimizationTimer?.Stop();
        if (!_optimizationSettings.Policy.Enabled || _optimizationSettingsError is not null) return;
        if (_optimizationTimer is null)
        {
            _optimizationTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _optimizationTimer.Tick += async (_, _) => await TryAutomaticOptimizationAsync();
        }
        _optimizationTimer.Start();
    }

    private void SaveOptimizationPolicy(OptimizationPolicy policy)
    {
        var settings = _optimizationSettings with { Policy = policy.Validate() };
        settings.Save(OptimizationSettingsPath);
        _optimizationSettings = settings; _optimizationSettingsError = null; UpdateOptimizationTimer();
    }

    private void Optimization_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _work is not null || _optimizationWindow is not null) return;
        if (_snapshot is not { } scan) { StatusText.Text = "Scan a location before setting up automatic optimization."; return; }
        var findings = _rows.Select(row => row.Item).ToArray();
        _optimizationWindow = new(scan, findings, Operations, _optimizationSettings, SaveOptimizationPolicy,
            (preview, policy, token) => StartOptimizationAsync(scan, findings, preview, policy, token), _optimizationSettingsError) { Owner = this };
        try { _optimizationWindow.ShowDialog(); }
        finally { _optimizationWindow = null; }
    }

    internal async Task TryAutomaticOptimizationAsync()
    {
        var policy = _optimizationSettings.Policy;
        if (!policy.Enabled || _optimizationSettingsError is not null || _optimizationWindow is not null || !CanRestartForUpdate()
            || _scanStale || _snapshot is not { } scan || !string.Equals(policy.ScopePath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(scan.RootPath)), StringComparison.OrdinalIgnoreCase)
            || _optimizationSettings.LastRunUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(1)) return;
        try { await StartOptimizationAsync(scan, _rows.Select(row => row.Item).ToArray(), null, policy, _extensionLifetime.Token); }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        { if (!_closing) StatusText.Text = "Automatic optimization stopped. " + error.Message; }
    }

    private async Task<OptimizationResult> StartOptimizationAsync(ScanSnapshot scan, IReadOnlyList<SpaceMaid.Core.Analysis.Opportunity> findings,
        OptimizationPreview? preview, OptimizationPolicy policy, CancellationToken token)
    {
        if (_optimizationTask is not null || !CanRestartForUpdate() || !ReferenceEquals(_snapshot, scan))
            throw new InvalidOperationException("Wait for current work, then scan and preview again.");
        var task = RunOptimizationAsync(scan, findings, preview, policy, token);
        _optimizationTask = task;
        try { return await task; }
        finally { if (ReferenceEquals(_optimizationTask, task)) _optimizationTask = null; }
    }

    private async Task<OptimizationResult> RunOptimizationAsync(ScanSnapshot scan, IReadOnlyList<SpaceMaid.Core.Analysis.Opportunity> findings,
        OptimizationPreview? preview, OptimizationPolicy policy, CancellationToken token)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token, _extensionLifetime.Token);
        using var log = new ScanLog(Path.Join(LogDirectory, "Optimization"));
        _work = work; SetBusy(true);
        OptimizationResult? completed = null;
        // Persist the attempt before mutation: failures cannot create a tight
        // retry loop, and settings must be writable before unattended deletion.
        try
        {
            if (log.FilePath is null || log.Error is not null) throw new IOException("Optimization needs a writable activity log.");
            _optimizationSettings = _optimizationSettings with { LastRunUtc = DateTime.UtcNow, LastLog = log.FilePath };
            _optimizationSettings.Save(OptimizationSettingsPath);
            preview ??= await Operations.PreviewOptimizationAsync(scan, findings, policy, work.Token);
            log.Record("optimization-start", new { policy.ScopePath, policy.KeepRecentDays, policy.MaxBytesPerRun, Files = preview.Files.Select(f => f.Path).ToArray() });
            if (log.Error is not null) throw new IOException("Optimization could not write its activity log.");
            var progress = new Progress<OptimizationFileResult>(result =>
            { if (!_closing) StatusText.Text = "Optimizing · " + result.Path; });
            var result = await Operations.OptimizeAsync(scan, findings, preview, policy, DiscoverOpportunities, work.Token, log.Record, progress);
            completed = result;
            foreach (var file in result.Files) log.Record("optimization-file", file);
            string summary = $"Optimization · {result.DeletedCount:N0} files deleted · {SizeText.Format(result.RemovedLogicalBytes)} in file contents removed"
                + (result.Cancelled ? " · Cancelled" : "")
                + $" · {result.Files.Count(f => f.Outcome != OptimizationOutcome.Deleted):N0} kept / failed";
            _optimizationSettings = _optimizationSettings with { LastSummary = summary };
            _optimizationSettings.Save(OptimizationSettingsPath);
            log.Record("optimization-complete", new { Summary = summary });
            if (!_closing)
            {
                if (result.DeletedCount > 0 && scan.HasAllocationData)
                {
                    var scopes = preview.Files.Select(f => new ScanRecheckRequest(f.ContainerPath)).DistinctBy(r => r.Path).ToArray();
                    await RefreshAffectedAsync(scan, scopes, summary + ".", work.Token, log);
                }
                else if (result.DeletedCount > 0) MarkScanStale(summary + ". Scan again to refresh sizes and resume automatic optimization.",
                    preview.Files.Select(f => new ScanRecheckRequest(f.ContainerPath)).DistinctBy(r => r.Path));
                else StatusText.Text = summary;
                RefreshPathDriveSpace();
            }
            return result;
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        {
            log.Record("optimization-stopped", new { Error = error.ToString() });
            if (!_closing) MarkScanStale("Optimization stopped. Scan again to check affected locations.", [new(scan.RootPath)]);
            if (completed is not null) return completed with { Cancelled = completed.Cancelled || work.IsCancellationRequested,
                Note = "Files listed as deleted remain deleted. Results could not finish updating: " + error.Message };
            throw;
        }
        finally { _work = null; if (!_closing) SetBusy(false); }
    }
}
