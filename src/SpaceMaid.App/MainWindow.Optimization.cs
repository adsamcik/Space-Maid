using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

public partial class MainWindow
{
    internal string OptimizationSettingsPath { get; set; } = AppData.File("optimization-settings.json");
    private OptimizationSettings _optimizationSettings = new(new());
    private string? _optimizationSettingsError;
    private DispatcherTimer? _optimizationTimer;
    private OptimizationWindow? _optimizationWindow;
    private Task<OptimizationResult>? _optimizationTask;

    private void UpdateOptimizationAvailability(bool busy)
    {
        bool available = !_closing && !_scanning && !busy && _work is null && (_snapshot is not null || ExtensionsReady);
        string hint = _scanning ? "Available after scan" : _snapshot is null ? "Scan for generated files" : "Available when idle";
        string help = available ? "Review optimization actions from enabled extensions. Native tools are available without a scan."
            : _scanning ? "Wait for the scan to finish, then choose rules and preview files."
            : _snapshot is null ? "Complete a scan to choose rules and preview generated files."
            : "Wait for the current work to finish.";
        OptimizationButton.IsEnabled = available;
        OptimizationButton.ToolTip = help;
        AutomationProperties.SetHelpText(OptimizationButton, help);
        OptimizationHint.Text = available && _snapshot is not null ? "" : hint;
        OptimizationHint.Visibility = available && _snapshot is not null ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void InitializeOptimization()
    {
        try { _optimizationSettings = OptimizationSettings.Load(OptimizationSettingsPath); _optimizationSettingsError = null; }
        catch (Exception error) when (SettingsError(error))
        { _optimizationSettingsError = "Optimization settings unavailable. Automatic cleanup is off; save settings to restore it."; }
        // Disabled mode has no recurring work, including timer wakeups.
        UpdateOptimizationTimer();
        UpdateOptimizationAvailability(_work is not null);
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
        bool reset = _optimizationSettings.Policy.ScopePath != policy.ScopePath
            || !_optimizationSettings.Policy.EnabledRules.SequenceEqual(policy.EnabledRules);
        var settings = _optimizationSettings with { Policy = policy.Validate(), Cursor = reset ? 0 : _optimizationSettings.Cursor,
            Cooldowns = reset ? [] : _optimizationSettings.Cooldowns };
        settings.Save(OptimizationSettingsPath);
        _optimizationSettings = settings; _optimizationSettingsError = null; UpdateOptimizationTimer();
    }

    private async void Optimization_Click(object sender, RoutedEventArgs e)
    {
        if (!OptimizationButton.IsEnabled || _closing || _work is not null || _optimizationWindow is not null) return;
        if (!CanRestartForUpdate()) { StatusText.Text = "Finish the current extension workflow before optimizing."; return; }
        try { await OpenOptimizationWindowAsync(); }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        { StatusText.Text = "Optimization could not open. " + error.Message; }
    }

    private async Task OpenOptimizationWindowAsync()
    {
        var scan = _snapshot;
        var findings = _rows.Select(row => row.Item).ToArray();
        var integration = CreateOptimizationIntegration(scan?.RootPath ?? _currentPath);
        _optimizationWindow = new(scan, findings, Operations, _optimizationSettings, SaveOptimizationPolicy,
            (preview, policy, token) => scan is null ? throw new InvalidOperationException("Scan a location before deleting generated files.")
                : StartOptimizationAsync(scan, findings, preview, policy, token), _optimizationSettingsError, integration) { Owner = this };
        var window = _optimizationWindow;
        try { _optimizationWindow.ShowDialog(); }
        finally { _optimizationWindow = null; }
        if (_closing) return;
        if (window.RequestedRescan) await NavigatePathAsync(scan?.RootPath ?? _currentPath, forceScan: true);
        else if (window.RequestedWorkflow is { } requested)
        {
            try
            {
                var entry = _extensions?.Entries.SingleOrDefault(entry => entry.Id == requested.OwnerId);
                if (entry?.View is not IOptimizationWorkflowView view || view.IsOptimizationBusy || !OpenExtension(requested.OwnerId)
                    || !view.OpenOptimizationWorkflow(requested.Action.Workflow!))
                    StatusText.Text = "This extension workflow is unavailable or busy. Open Extensions to check it.";
            }
            catch (Exception error) { StatusText.Text = "Could not open the extension workflow. " + error.GetBaseException().Message; }
        }
    }

    private OptimizationIntegration CreateOptimizationIntegration(string scope)
    {
        var discovery = _extensions?.DiscoverOptimization() ?? new([], []);
        ToolCacheService? tools = null;
        if (discovery.Actions.Any(action => action.Action.NativeTool is not null))
        {
            try { tools = new ToolCacheService(AppData.File(Path.Join("NativeTools", "Inspection"))); }
            catch (Exception error) when (CleanupBatch.IsExpected(error))
            { discovery = discovery with { Errors = [.. discovery.Errors, "Native cache inspection needs a writable data folder: " + error.Message] }; }
        }
        return new(scope, discovery,
            _extensions?.Entries.ToDictionary(entry => entry.Id, entry => entry.Name, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>(),
            tools, tools is null ? null : (plan, token) => ExecuteToolCacheAsync(tools, plan, token));
    }

    internal async Task TryAutomaticOptimizationAsync()
    {
        var policy = _optimizationSettings.Policy;
        if (!policy.Enabled || _optimizationSettingsError is not null || _optimizationWindow is not null || _duplicateWindow is not null
            || _toolCachesWindow is not null || _worktreeLifecycleWindow is not null || !CanRestartForUpdate()
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
            preview ??= await Operations.PreviewOptimizationAsync(scan, findings, policy, work.Token,
                _optimizationSettings.Cursor, _optimizationSettings.Cooldowns);
            _optimizationSettings = _optimizationSettings with { Cursor = preview.NextCursor };
            _optimizationSettings.Save(OptimizationSettingsPath);
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
            _optimizationSettings = _optimizationSettings with { LastSummary = summary,
                Cooldowns = result.Files.Where(f => f.Cooldown is not null).Select(f => f.Cooldown!)
                    .Concat(_optimizationSettings.Cooldowns.Where(c => c.RetryAfterUtc > DateTime.UtcNow))
                    .Where(c => c.Path.Length <= 2048).DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Take(32).ToArray() };
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
