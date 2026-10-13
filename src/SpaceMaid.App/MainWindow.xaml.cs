using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

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

    public MainWindow()
    {
        InitializeComponent();
        InitializeTreeColumns();
        Activated += Window_Activated;
        UpdateTreeSortHeaders();
        InitializeOpportunityBrowser();
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
        _snapshotVolumeId = null;
        _scanStale = false;
        _staleScopes.Clear();
        _explorerRecheckPath = null;
        _explorerReturnPending = false;
        foreach (var row in _rows) row.PropertyChanged -= Row_Changed;
        _rows.Clear();
        ResetOpportunityBrowser();
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailHint.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Visible;
        EmptyCaption.Text = "Scan a drive to start.";
        ScannedSizeText.Text = CandidateText.Text = "—";
        ScannedSizeLabel.Text = "Scanned";
        ScannedSizeCaption.Text = "On disk";
        OpportunityHeading.Text = "Opportunities";
        OpportunitiesTab.Content = "Opportunities";
        CandidateCaption.Text = "Estimate";
        DetailsButton.Visibility = Visibility.Collapsed;
        UpdateSelection();
    }

    internal void SetBusy(bool busy)
    {
        _extensions?.SetDiscoveryBusy(busy);
        UpdatePathAvailability(busy);
        UpdateFastScanButton(busy);
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.IsIndeterminate = true;
        BuildKeepRecentDaysBox.IsEnabled = !busy;
        RecycleBinRow.IsEnabled = !busy;
        EmptyRecycleBinButton.IsEnabled = !busy && _recycleBin?.Items > 0;
        OpportunityGrid.IsEnabled = !busy;
        GroupBrowserControls.IsEnabled = !busy;
        OpportunityFilters.IsEnabled = !busy;
        OpportunityToolbar.IsEnabled = !busy;
        CleanupGroupCards.IsEnabled = !busy;
        WorktreeCleanupControls.IsEnabled = !busy;
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
    private void Row_Changed(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(OpportunityRow.Selected) && !_changingOpportunitySelection) UpdateSelection(); }
    private void UpdateSelection()
    {
        UpdateOpportunitySelection();
    }

    private void Opportunity_Changed(object sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = OpportunityGrid.SelectedItem is OpportunityRow;
        DetailPanel.DataContext = (OpportunityGrid.SelectedItem as OpportunityRow)?.Item;
        DetailPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        DetailHint.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        var assessment = OpportunityGrid.SelectedItem is OpportunityRow row ? _worktreeAssessments.GetValueOrDefault(row.Path) : null;
        WorktreeDetailText.Visibility = assessment is null ? Visibility.Collapsed : Visibility.Visible;
        WorktreeDetailText.Text = assessment is null ? "" : assessment.Status + ": " + assessment.Reason
            + (assessment.Branch is null ? "" : "\nBranch: " + assessment.Branch)
            + (assessment.IntegrationTarget is null ? "" : "\nCompared with cached ref: " + assessment.IntegrationTarget)
            + (assessment.LastActivityUtc is { } activity ? "\nLast observed activity: " + activity.ToLocalTime().ToString("g") : "\nLast observed activity: unavailable")
            + (assessment.VerifiedClean ? "\nLocal tracked/untracked changes: none" : "")
            + (assessment.Integrated ? "\nCommits already in upstream: yes" : "");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (OpportunityGrid.SelectedItem is not OpportunityRow row) return;
        OpenFolderInExplorer(row.Path);
    }
    private void OpenFolderInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path)) { StatusText.Text = "This location is no longer available. Rescan to refresh the results."; return; }
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            _explorerRecheckPath = path;
        }
        catch (Win32Exception ex) { StatusText.Text = $"Could not open the folder: {ex.Message}"; }
    }

    private bool _shutdownComplete;
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) { e.Cancel = !_shutdownComplete; return; }
        var scanHelper = _scanHelper;
        SaveColumnLayout();
        _closing = true; _opportunitySearchTimer.Stop(); _work?.Cancel(); _browseWork?.Cancel(); _scanHelper?.Dispose(); _extensionLifetime.Cancel();
        e.Cancel = true;
        bool workersStopped = true;
        try
        {
            if (_updates is not null) await _updates.StopAsync();
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
