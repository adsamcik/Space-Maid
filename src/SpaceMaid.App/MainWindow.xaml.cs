using SpaceMaid.Core.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<OpportunityRow> _rows = [];
    private ScanSnapshot? _snapshot;
    private string? _snapshotVolumeId;
    private CancellationTokenSource? _work;
    private bool _closing;
    private bool _startupOffered;
    private string? _scanLogPath;
    private readonly Task _classificationReady = Task.Run(FileTypeClassifier.Prepare);
    internal string LogDirectory { get; set; } = AppData.File("Logs");
    internal FileOperations Operations { get; set; }

    public MainWindow()
    {
        Operations = new(() => _scanHelper?.IsAvailable == true ? _scanHelper : null, inventory: () => _snapshot);
        InitializeComponent();
        InitializeTreeColumns();
        Activated += Window_Activated;
        UpdateTreeSortHeaders();
        SetScanTarget(Path.GetPathRoot(Environment.SystemDirectory)!);
        UpdateFastScanButton(false);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupOffered) return;
        _startupOffered = true;
        LoadColumnLayout();
        LoadBuildCleanupSettings();
        _ = RefreshRecycleBinAsync();
        _ = RefreshDriveChoicesAsync();
        if (!Elevation.IsAdministrator)
        {
            var startup = new StartupWindow { Owner = this };
            if (startup.ShowDialog() == true) await RequestElevationAsync();
        }
        if (!_closing) await InitializeExtensionsAsync();
        if (!_closing) InitializeUpdates();
        if (!_closing) InitializeOptimization();
        if (!_closing) InitializeWorktreeLifecycle();
    }

    internal void SetScanTarget(string path)
    {
        // Results always belong to the selected target, never the previous drive.
        ClearResults();
        ShowPath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        RefreshPathDriveSpace();
        _scanLogPath = null;
        OpenLogButton.Visibility = BackendStatusText.Visibility = Visibility.Collapsed;
    }

    private void ClearResults()
    {
        ClearExplorer();
        ClearPreliminary();
        _detectionElapsed = _presentationElapsed = TimeSpan.Zero;
        _snapshot = null;
        UpdateOptimizationAvailability(_work is not null);
        _snapshotVolumeId = null;
        _scanStale = false;
        _staleScopes.Clear();
        _explorerRecheckPath = null;
        _explorerReturnPending = false;
        _rows.Clear();
        ResetOpportunityBrowser();
        ScannedSizeText.Text = CandidateText.Text = "—";
        ScannedSizeLabel.Text = "Scanned";
        ScannedSizeCaption.Text = "On disk";
        OpportunitiesTab.Content = "Opportunities";
        CandidateCaption.Text = "Estimate";
        DetailsButton.Visibility = Visibility.Collapsed;
        UpdateSelection();
    }

    internal void SetBusy(bool busy)
    {
        UpdateExtensionWorkspace();
        _extensions?.SetDiscoveryBusy(busy);
        UpdatePathAvailability(busy);
        UpdateFastScanButton(busy);
        UpdateOptimizationAvailability(busy);
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.IsIndeterminate = true;
        OpportunityBrowser?.SetBusy(busy);
        ExplorePanel.IsEnabled = !busy && !_browsing;
        UpdateSelection();
        UpdateUpdateButton();
        if (!busy) { DrainExplorerRecheck(); if (_startupOffered) _ = RefreshRecycleBinAsync(); }
    }

    private void UpdateFastScanButton(bool busy)
    {
        bool canEnable = !_fastEnabled && !Elevation.IsAdministrator;
        ElevateButton.Visibility = canEnable ? Visibility.Visible : Visibility.Collapsed;
        ElevateButton.IsEnabled = canEnable && !busy;
    }

    private void ShowDriveSpace(long total, long free)
    {
        _driveSpaceVersion++;
        FreeSpaceText.Text = SizeText.Format(free);
        CapacityText.Text = $"of {SizeText.Format(total)} total";
        DriveCapacityBar.Value = total > 0 ? Math.Clamp(100d * free / total, 0, 100) : 0;
        DriveCapacityBar.ToolTip = $"{SizeText.Format(free)} free of {SizeText.Format(total)} total";
        System.Windows.Automation.AutomationProperties.SetHelpText(DriveCapacityBar, (string)DriveCapacityBar.ToolTip);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();
    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (_scanLogPath is null) return;
        try
        {
            var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
            start.ArgumentList.Add(_scanLogPath);
            using var process = Process.Start(start);
        }
        catch (Win32Exception ex) { StatusText.Text = $"Could not open the log: {ex.Message}"; }
    }
    private void UpdateSelection()
    {
        PublishExtensionContext();
        var choices = _rows.Where(row => row.Selected).Select(row => row.Item).ToArray();
        _ = SelectedCheckedWorktrees(choices);
        foreach (string path in _worktreesRequiringCheck.Keys.Where(path => !choices.Any(choice => GitWorktreeGuard.Covers(choice.Path, path))).ToArray())
            _worktreesRequiringCheck.Remove(path);
        OpportunityBrowser?.UpdateContext(_currentPath, _scanStale);
    }

    private void OpenFolderInExplorer(string path)
    {
        try
        {
            if (!DiskAccess.Default.DirectoryExists(path)) { StatusText.Text = "This location is no longer available. Rescan to refresh the results."; return; }
            _explorerRecheckPath = Operations.OpenInExplorer(path);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        { StatusText.Text = $"Could not open the folder: {ex.Message}"; }
    }

    private bool _shutdownComplete;
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) { e.Cancel = !_shutdownComplete; return; }
        var scanHelper = _scanHelper;
        SaveColumnLayout();
        _closing = true; _optimizationTimer?.Stop(); _worktreeLifecycleTimer?.Stop(); _updateTimer?.Stop(); _work?.Cancel(); _browseWork?.Cancel(); _scanHelper?.Dispose(); _extensionLifetime.Cancel();
        e.Cancel = true;
        bool workersStopped = true;
        try
        {
            if (_updates is not null) await _updates.StopAsync();
            if (_duplicateWindow is not null)
                try { await _duplicateWindow.StopAsync(); }
                catch (Exception error) { workersStopped = false; Debug.WriteLine($"Duplicate search shutdown: {error}"); }
            if (_toolCacheTask is not null)
                try { await _toolCacheTask; }
                catch (Exception error) { workersStopped = false; Debug.WriteLine($"Tool cache shutdown: {error}"); }
            if (_worktreeLifecycleTask is not null)
                try { await _worktreeLifecycleTask; }
                catch (Exception error) { workersStopped = false; Debug.WriteLine($"Worktree lifecycle shutdown: {error}"); }
            if (_optimizationTask is not null)
                try { await _optimizationTask; }
                catch (Exception error) { workersStopped = false; Debug.WriteLine($"Optimization shutdown: {error}"); }
            if (_cleanupTask is not null)
                try { await _cleanupTask; }
                catch (Exception error) { workersStopped = false; Debug.WriteLine($"Cleanup shutdown: {error}"); }
            if (_worktreeCheckTask is not null)
                try { await _worktreeCheckTask; }
                catch (Exception error) { workersStopped = false; Debug.WriteLine($"Worktree check shutdown: {error}"); }
            if (_extensions is not null) await _extensions.DisposeAsync();
            if (scanHelper is not null)
                try { await scanHelper.Exit.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) { workersStopped = false; Debug.WriteLine($"Scanner shutdown: {error}"); }
            if (_restartForUpdate && _updates is not null)
            {
                if (!workersStopped) throw new InvalidOperationException("Some workers could not finish safely.");
                await _updates.ScheduleRestartAsync();
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine($"App shutdown: {error}");
            if (_restartForUpdate) MessageBox.Show("The update could not start. Reopen Space Maid and try again.", "Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updates?.Dispose();
            _shutdownComplete = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
