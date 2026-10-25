using System.IO;
using System.Windows;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    internal string WorktreeLifecycleSettingsPath { get; set; } = AppData.File("managed-worktrees.json");
    private DispatcherTimer? _worktreeLifecycleTimer;
    private WorktreeLifecycleWindow? _worktreeLifecycleWindow;
    private Task? _worktreeLifecycleTask;
    private readonly Dictionary<string, DateTime> _worktreeRetirementAttempts = new(StringComparer.OrdinalIgnoreCase);

    internal void InitializeWorktreeLifecycle()
    {
        _worktreeLifecycleTimer?.Stop();
        try
        {
            if (!new WorktreeRetirement(WorktreeLifecycleSettingsPath).Load().Policies.Any(p => p.Automatic)) return;
            if (_worktreeLifecycleTimer is null)
            {
                _worktreeLifecycleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
                _worktreeLifecycleTimer.Tick += async (_, _) => await TryManagedWorktreeRetirementAsync();
            }
            _worktreeLifecycleTimer.Start();
        }
        catch (Exception error) when (SettingsError(error))
        { if (!_closing) StatusText.Text = "Managed worktree settings unavailable. Automatic retirement is off. " + error.Message; }
    }

    private void WorktreeLifecycle_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _work is not null || _worktreeLifecycleWindow is not null) return;
        try
        {
            var paths = _rows.Where(row => row.Item.IsWorktree).Select(row => row.Path).ToArray();
            _worktreeLifecycleWindow = new(paths, new WorktreeRetirement(WorktreeLifecycleSettingsPath), RunWorktreeLifecycleActionAsync,
                moved => { InitializeWorktreeLifecycle(); if (moved && _snapshot is { } scan) MarkScanStale("Worktree lifecycle changed a checkout. Scan again to update locations.", [new(scan.RootPath)]); }) { Owner = this };
            _worktreeLifecycleWindow.ShowDialog();
        }
        catch (Exception error) when (SettingsError(error)) { StatusText.Text = "Managed worktrees unavailable. " + error.Message; }
        finally { _worktreeLifecycleWindow = null; }
    }

    private async Task RunWorktreeLifecycleActionAsync(Func<CancellationToken, Task> operation, CancellationToken token)
    {
        if (_worktreeLifecycleTask is not null || !CanRestartForUpdate()) throw new InvalidOperationException("Wait for the current scan or cleanup to finish.");
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token, _extensionLifetime.Token);
        _work = work; SetBusy(true);
        using var log = new ScanLog(Path.Join(LogDirectory, "WorktreeRetirement"));
        try
        {
            if (log.FilePath is null || log.Error is not null) throw new IOException("Worktree retirement requires a writable activity log.");
            log.Record("managed-worktree-action-start", new { Settings = WorktreeLifecycleSettingsPath });
            var task = operation(work.Token); _worktreeLifecycleTask = task;
            await task; log.Record("managed-worktree-action-complete", new { Cancelled = work.IsCancellationRequested });
        }
        catch (Exception error)
        { log.Record("managed-worktree-action-stopped", new { Error = error.ToString() }); throw; }
        finally { _worktreeLifecycleTask = null; _work = null; if (!_closing) SetBusy(false); }
    }

    internal async Task TryManagedWorktreeRetirementAsync()
    {
        if (_closing || _worktreeLifecycleWindow is not null || _duplicateWindow is not null || _toolCachesWindow is not null
            || _optimizationWindow is not null || _worktreeLifecycleTask is not null || !CanRestartForUpdate()) return;
        try
        {
            var service = new WorktreeRetirement(WorktreeLifecycleSettingsPath); var state = service.Load();
            var policies = state.Policies.Where(p => p.Automatic && (!_worktreeRetirementAttempts.TryGetValue(p.Path, out var attempt)
                || DateTime.UtcNow - attempt >= TimeSpan.FromHours(1))).ToArray();
            if (policies.Length == 0) return;
            await RunWorktreeLifecycleActionAsync(async token =>
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromMinutes(2));
                foreach (var policy in policies)
                {
                    budget.Token.ThrowIfCancellationRequested(); _worktreeRetirementAttempts[policy.Path] = DateTime.UtcNow;
                    var pending = service.Load().Records.LastOrDefault(r => r.Policy.Path == policy.Path
                        && r.State is not (WorktreeRetirementState.Completed or WorktreeRetirementState.Restored));
                    try
                    {
                        WorktreeRetirementRecord? result = pending is { State: WorktreeRetirementState.Quarantined } && DateTime.UtcNow >= pending.FinalizeAfterUtc
                            ? await service.FinalizeAsync(pending.Id, budget.Token)
                            : pending is null ? await service.QuarantineAsync(policy, budget.Token) : null;
                        if (result is not null && !_closing)
                        {
                            StatusText.Text = result.Message;
                            if (_snapshot is { } scan) MarkScanStale(result.Message + " Scan again to update sizes.", [new(scan.RootPath)]);
                        }
                    }
                    catch (Exception error) when (CleanupBatch.IsExpected(error))
                    { if (!_closing) StatusText.Text = "Managed checkout kept. " + error.Message; }
                }
            }, _extensionLifetime.Token);
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is System.Text.Json.JsonException)
        { if (!_closing) StatusText.Text = "Automatic worktree retirement stopped. " + error.Message; }
    }
}
