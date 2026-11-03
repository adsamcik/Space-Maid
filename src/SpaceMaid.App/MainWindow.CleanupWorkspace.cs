using System.IO;
using System.Windows;
using System.Windows.Interop;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private CleanupWorkspace? _cleanupWorkspace;
    private bool _cleanupBrowserVisible;
    internal string CleanupDataDirectory => Path.Join(Path.GetDirectoryName(CleanupSettingsPath)!, "Cleanup");
    internal CleanupWorkspace? CleanupWorkspaceView => _cleanupWorkspace;

    private void InitializeCleanupWorkspace()
    {
        if (_cleanupWorkspace is not null || _extensions is null) return;
        var store = new CleanupRunStore(Operations.Files, CleanupDataDirectory);
        _cleanupWorkspace = new(Operations, store, DiscoverOpportunities, CleanupDataDirectory);
        _cleanupWorkspace.BrowseRequested += ShowCleanupBrowser;
        _cleanupWorkspace.RevealRequested += OpenFolderInExplorer;
        _cleanupWorkspace.AvailabilityChanged += UpdateExtensionWorkspace;
        _cleanupWorkspace.ExecuteRun = RunWorkspaceCleanupAsync;
        CleanupWorkspaceContent.Content = _cleanupWorkspace;
        _cleanupWorkspace.SetSource(_snapshot, _rows.Select(row => row.Item).ToArray(), CurrentBuildPolicy.KeepRecentDays);
        _ = ReconcileCleanupSchedulesAsync(store);
        ShowCleanupWorkspace();
    }

    private async Task ReconcileCleanupSchedulesAsync(CleanupRunStore store)
    {
        try
        {
            await SpaceMaid.Core.Analysis.CleanupMutationLease.RunAsync(() =>
            { store.RecoverInterruptedRuns(); return Task.CompletedTask; }, _extensionLifetime.Token);
            await new CleanupScheduleService(Operations, store).ReconcileAsync(_extensionLifetime.Token);
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is System.Text.Json.JsonException)
        { if (!_closing) StatusText.Text = "Cleanup schedules need attention. Open Saved cleanups. " + error.Message; }
    }

    private void ShowCleanupWorkspace()
    {
        _cleanupBrowserVisible = false;
        if (CleanupWorkspaceContent is null) return;
        CleanupWorkspaceContent.Visibility = _cleanupWorkspace is null ? Visibility.Collapsed : Visibility.Visible;
        OpportunitiesContent.Visibility = _cleanupWorkspace is null ? Visibility.Visible : Visibility.Collapsed;
        CleanupBrowserBack.Visibility = Visibility.Collapsed;
        UpdateExtensionWorkspace();
    }

    private void ShowCleanupBrowser()
    {
        _cleanupBrowserVisible = true; OpportunitiesTab.IsChecked = true;
        CleanupWorkspaceContent.Visibility = Visibility.Collapsed; OpportunitiesContent.Visibility = Visibility.Visible;
        CleanupBrowserBack.Visibility = Visibility.Visible;
        UpdateExtensionWorkspace();
    }
    private void CleanupBrowserBack_Click(object sender, RoutedEventArgs e) => ShowCleanupWorkspace();

    private async Task<CleanupBatchResult> RunWorkspaceCleanupAsync(CleanupPreparedRun prepared,
        IProgress<CleanupProgress> progress, CancellationToken token)
    {
        if (_closing || _work is not null || !CanRestartForUpdate())
            throw new InvalidOperationException("Wait for the current operation to finish before starting cleanup.");
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token, _extensionLifetime.Token);
        using var log = StartActionLog("cleanup-run", prepared.Definition.DiscoveryRootPath);
        _work = work; SetBusy(true);
        try
        {
            var coordinator = new CleanupRunCoordinator(Operations, _cleanupWorkspace!.Store, DiscoverOpportunities);
            var displayed = _snapshot;
            Task<CleanupBatchResult> task;
            if (displayed is not null && string.Equals(displayed.RootPath, prepared.Plan.Snapshot.RootPath, StringComparison.OrdinalIgnoreCase))
                task = ExecuteCleanupWithRefreshAsync(prepared.Plan, displayed, progress, work.Token, work, log,
                    current => coordinator.RunAsync(prepared, new WindowInteropHelper(this).Handle, progress, current));
            else task = coordinator.RunAsync(prepared, new WindowInteropHelper(this).Handle, progress, work.Token);
            _cleanupTask = task;
            var result = await task;
            foreach (var item in result.Locations) log.Record("cleanup-location-result", item);
            if (!_closing)
            {
                StatusText.Text = string.Join(" · ", CleanupInspectionData.Build(prepared.Plan, results: result.Locations)
                    .GroupBy(item => item.Decision).Select(group => $"{group.Count():N0} {CleanupInspectionData.Label(group.Key).ToLowerInvariant()}"));
                RefreshPathDriveSpace();
            }
            return result;
        }
        finally { _cleanupTask = null; _work = null; if (!_closing) SetBusy(false); }
    }
}
