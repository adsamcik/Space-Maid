using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<OpportunityRow> _rows = [];
    private ScanSnapshot? _snapshot;
    private CancellationTokenSource? _work;
    private bool _closing;
    private bool _startupOffered;
    private string? _scanLogPath;
    private HelperLayoutSource? _scanHelper;
    private bool _fastEnabled;
    private readonly Task _classificationReady = Task.Run(FileTypeClassifier.Prepare);
    internal string LogDirectory { get; set; } = ScanLog.DefaultDirectory;

    public MainWindow()
    {
        InitializeComponent();
        Activated += Window_Activated;
        UpdateTreeSortHeaders();
        InitializeOpportunityBrowser();
        var drives = new List<DriveChoice>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                    drives.Add(new(drive.Name, drive.Name,
                        drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A drive may disconnect. */ }
        }
        DrivePicker.ItemsSource = drives;
        DrivePicker.SelectedIndex = drives.Count > 0 ? 0 : -1;
        ScanButton.IsEnabled = drives.Count > 0;
        UpdateFastScanButton(false);
        if (drives.Count == 0) StatusText.Text = "No accessible drives were found.";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupOffered) return;
        _startupOffered = true;
        if (!Elevation.IsAdministrator)
        {
            var startup = new StartupWindow { Owner = this };
            if (startup.ShowDialog() == true) await RequestElevationAsync();
        }
        if (!_closing) await InitializeExtensionsAsync();
    }

    private void Drive_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FreeSpaceText is null) return;
        if (DrivePicker.SelectedItem is DriveChoice drive)
        {
            ShowDriveSpace(drive.TotalBytes, drive.FreeBytes);
        }
        // Results always belong to the selected target, never the previous drive.
        ClearResults();
        _scanLogPath = null;
        OpenLogButton.Visibility = BackendStatusText.Visibility = Visibility.Collapsed;
    }

    private void ClearResults()
    {
        ClearExplorer();
        ClearPreliminary();
        _detectionElapsed = _presentationElapsed = TimeSpan.Zero;
        _snapshot = null;
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

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || DrivePicker.SelectedItem is not DriveChoice drive) return;
        ClearResults();
        using var work = new CancellationTokenSource();
        _work = work;
        using var log = new ScanLog(LogDirectory);
        _scanLogPath = log.FilePath;
        OpenLogButton.Visibility = _scanLogPath is null ? Visibility.Collapsed : Visibility.Visible;
        OpenLogButton.ToolTip = _scanLogPath;
        BackendStatusText.Visibility = Visibility.Visible;
        BackendStatusText.Text = "Selecting scanner…";
        log.Record("app-scan-start", new { Target = drive.Path, Administrator = Elevation.IsAdministrator,
            Executable = Environment.ProcessPath, Version = typeof(App).Assembly.GetName().Version?.ToString(),
            Build = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Runtime = Environment.Version.ToString(), OperatingSystem = Environment.OSVersion.ToString(), ProcessId = Environment.ProcessId });
        SetBusy(true);
        EmptyCaption.Text = "Scanning…";
        CandidateCaption.Text = "Scanning…";
        bool acquiring = true;
        var progress = new ScanUiProgress(p =>
        {
            if (!_closing && acquiring && ReferenceEquals(_work, work))
            {
                StatusText.Text = $"{p.Phase} · {p.Files:N0} files · {p.Elapsed.TotalSeconds:0.0}s";
                BackendStatusText.Text = p.Backend + (p.FallbackReason is null ? "" : $" · {p.FallbackReason}")
                    + (log.Error is null ? "" : $" · Logging unavailable: {log.Error}");
                if (p.Preliminary is { } preliminary) ShowPreliminary(preliminary);
            }
        });
        try
        {
            string? helperFailure = null;
            var selectedDrive = new DriveInfo(drive.Path);
            if (_fastEnabled && _scanHelper is null && selectedDrive.DriveType is DriveType.Fixed or DriveType.Removable
                && selectedDrive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                try { _scanHelper = await HelperLayoutSource.CreateAsync(true, work.Token); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception)
                {
                    _fastEnabled = false; helperFailure = $"Fast scan helper was unavailable: {ex.Message}. Standard scanning was used.";
                    log.Record("helper-unavailable", new { Error = ex.ToString() });
                }
            }
            var scan = await new VolumeScanner().ScanAsync(drive.Path, progress, work.Token,
                preferFast: _fastEnabled || Elevation.IsAdministrator, diagnostic: d => log.Record(d.Event, d), layoutSource: _scanHelper,
                fastUnavailableReason: helperFailure);
            acquiring = false;
            _scanHelper?.Dispose(); _scanHelper = null;
            if (_closing) return;
            if (progress.LatestPreliminary is { } preliminary) ShowPreliminary(preliminary);
            PreliminaryCaption.Text = "Preparing results…";
            BackendStatusText.Text = scan.Backend + (scan.FallbackReason is null ? "" : $" · {scan.FallbackReason}");
            StatusText.Text = "Finding development clutter…";
            log.Record("detection-start", new { scan.FileCount, scan.DirectoryCount });
            var result = await Task.Run(() =>
            {
                var timer = Stopwatch.StartNew();
                var opportunities = OpportunityDetector.Find(scan, work.Token);
                var total = CleanupPlanner.Create(scan, opportunities, work.Token);
                return (Opportunities: opportunities, Total: total, Groups: OpportunityGrouping.Build(scan, opportunities, work.Token), Elapsed: timer.Elapsed);
            }, work.Token);
            if (_closing) return;
            work.Token.ThrowIfCancellationRequested();
            _snapshot = scan;
            _detectionElapsed = result.Elapsed;
            log.Record("detection-complete", new { ElapsedMs = result.Elapsed.TotalMilliseconds, Opportunities = result.Opportunities.Count, result.Total.EstimatedBytes });
            var presentation = Stopwatch.StartNew();
            log.Record("explorer-start", new { Root = scan.RootPath });
            PublishOpportunities(scan, result.Opportunities, result.Groups);
            ScannedSizeLabel.Text = "Scanned";
            ScannedSizeText.Text = SizeText.Format(scan.HasAllocationData ? scan.AllocatedBytes : scan.LogicalBytes);
            CandidateText.Text = SizeText.Format(result.Total.EstimatedBytes);
            CandidateCaption.Text = $"Estimate · {_rows.Count:N0} {(_rows.Count == 1 ? "folder" : "folders")}";
            OpportunitiesTab.Content = $"Opportunities · {_rows.Count:N0}";
            await InitializeExplorerAsync(scan, result.Opportunities, work.Token);
            ScannedSizeCaption.Text = scan.HasAllocationData ? "On disk"
                : "Logical · Size on disk unavailable";
            _presentationElapsed = presentation.Elapsed;
            using var diagnosticProcess = Process.GetCurrentProcess();
            log.Record("app-scan-complete", new { scan.Backend, scan.FallbackReason, scan.FileCount, scan.NameCount,
                scan.WarningCount, scan.Warnings, scan.LogicalBytes, scan.AllocatedBytes, scan.Metrics,
                DetectionMs = _detectionElapsed.TotalMilliseconds, ExplorerMs = _presentationElapsed.TotalMilliseconds,
                ProcessPeakBytes = diagnosticProcess.PeakWorkingSet64 });
            _scanStatus = $"{scan.Backend} · {scan.RegularFileCount:N0} files · {scan.DirectoryCount:N0} folders · {(scan.Metrics.Acquisition + scan.Metrics.Aggregation + _detectionElapsed + _presentationElapsed).TotalSeconds:0.00}s"
                + (scan.WarningCount > 0 ? $" · {scan.WarningCount:N0} coverage warnings" : "");
            StatusText.Text = _scanStatus;
            DetailsButton.Visibility = Visibility.Visible;
            try
            {
                var refreshed = new DriveInfo(drive.Path);
                ShowDriveSpace(refreshed.TotalSize, refreshed.AvailableFreeSpace);
            }
            catch (IOException) { }
        }
        catch (OperationCanceledException)
        {
            acquiring = false;
            log.Record("scan-cancelled", new { Target = drive.Path });
            if (!_closing)
            {
                if (progress.LatestPreliminary is { } preliminary) ShowPreliminary(preliminary);
                RetainCancelledPreliminary();
                StatusText.Text = _preliminary is null ? "Scan cancelled." : "Scan cancelled · Preliminary results retained.";
                EmptyCaption.Text = "Start another scan to calculate cleanup estimates.";
                TreeHint.Text = "Scan cancelled. Start another scan when you're ready.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or OverflowException)
        {
            log.Record("scan-failed", new { Target = drive.Path, Error = ex.ToString(), Win32Error = (ex as Win32Exception)?.NativeErrorCode });
            if (!_closing) { ClearResults(); StatusText.Text = $"Scan could not finish: {ex.Message}"; EmptyCaption.Text = "Check that the drive is connected and accessible, then try again."; }
        }
        finally
        {
            acquiring = false;
            _scanHelper?.Dispose(); _scanHelper = null;
            if (!_closing && log.Error is not null) BackendStatusText.Text += $" · Logging unavailable: {log.Error}";
            _work = null;
            if (!_closing) SetBusy(false);
        }
    }

    internal void SetBusy(bool busy)
    {
        _extensions?.SetDiscoveryBusy(busy);
        DrivePicker.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy && DrivePicker.SelectedItem is not null;
        UpdateFastScanButton(busy);
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OpportunityGrid.IsEnabled = !busy;
        GroupBrowserControls.IsEnabled = !busy;
        OpportunityFilters.IsEnabled = !busy;
        OpportunityToolbar.IsEnabled = !busy;
        CleanupGroupCards.IsEnabled = !busy;
        WorktreeCleanupControls.IsEnabled = !busy;
        ExplorePanel.IsEnabled = !busy && !_browsing;
        UpdateSelection();
        if (!busy) DrainExplorerRecheck();
    }

    private void UpdateFastScanButton(bool busy)
    {
        bool canEnable = !_fastEnabled && !Elevation.IsAdministrator;
        ElevateButton.Visibility = canEnable ? Visibility.Visible : Visibility.Collapsed;
        ElevateButton.IsEnabled = canEnable && !busy;
    }

    private void ShowDriveSpace(long total, long free)
    {
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

    private async void Review_Click(object sender, RoutedEventArgs e) => await ReviewSelectionAsync();

    internal async Task ReviewSelectionAsync()
    {
        if (_snapshot is not { } scan || _work is not null) return;
        var selected = _rows.Where(r => r.Selected).Select(r => r.Item).ToArray();
        var checkedWorktrees = SelectedCheckedWorktrees(selected);
        if (selected.Length == 0) return;
        using var work = new CancellationTokenSource();
        using var log = StartActionLog("validate-review", scan.RootPath);
        _work = work;
        SetBusy(true);
        StatusText.Text = $"Checking {selected.Length:N0} selected locations…";
        var timer = Stopwatch.StartNew();
        try
        {
            log.Record("review-validation-start", new { Selected = selected.Length, OutOfDate = _scanStale, WorktreePolicies = checkedWorktrees });
            var validation = await OpportunityReviewValidator.ValidateAsync(scan, selected, work.Token, log.Record);
            var gitCheck = GitWorktreeGuard.CheckAsync(validation.Snapshot, validation.ValidatedLocations, checkedWorktrees, work.Token, log.Record,
                new Progress<string>(path => { if (!_closing && ReferenceEquals(_work, work)) StatusText.Text = "Checking worktree… " + path; }));
            _worktreeCheckTask = gitCheck;
            IReadOnlyDictionary<string, string> gitBlocks;
            try { gitBlocks = await gitCheck; }
            finally { if (ReferenceEquals(_worktreeCheckTask, gitCheck)) _worktreeCheckTask = null; }
            var plan = await Task.Run(() => CleanupPlanner.Prepare(validation.Snapshot, validation.ValidatedLocations, work.Token, gitBlocks), work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (_closing || !ReferenceEquals(_snapshot, scan)) return;
            if (validation.CanMergeInventory)
            {
                _staleScopes.UnionWith(validation.UncheckedScopes);
                await RefreshAffectedAsync(scan, validation.RecheckedScopes, "Selected locations checked.", work.Token, log,
                    validation.Snapshot, validation.Findings);
                if (_closing || !ReferenceEquals(_snapshot, validation.Snapshot)) return;
            }
            else
                MarkScanStale("Selected locations checked for review. The full inventory retains earlier logical-size data.", [new(scan.RootPath)]);
            // A replacement at the same path is a new target, never a retained
            // checkbox selection. Missing/no-longer-matching rows are excluded too.
            var excluded = validation.ExcludedPaths.ToHashSet(StringComparer.Ordinal);
            _changingOpportunitySelection = true;
            try { foreach (var row in _rows) if (excluded.Contains(row.Path)) row.Selected = false; }
            finally { _changingOpportunitySelection = false; }
            UpdateSelection();
            string note = $"Selected locations checked at {DateTime.Now:HH:mm:ss}. "
                + (excluded.Count == 1 ? "1 location was excluded because it is missing, changed or no longer matches. "
                    : excluded.Count > 1 ? $"{excluded.Count:N0} locations were excluded because they are missing, changed or no longer match. " : "")
                + "Other folders retain earlier scan data.";
            if (checkedWorktrees.Count > 0) note += " Worktree rules checked: "
                + string.Join("; ", checkedWorktrees.Values.Distinct().Select(DescribeWorktreePolicy))
                + ". Ignored contents are included; recycling preserves branches and leaves Git registration in place.";
            StatusText.Text = plan.Preview.Locations.Count + plan.BlockedLocations.Count == 0 ? "None of the selected locations still match. Selection updated; scan again to discover new opportunities." : note;
            log.Record("review-validation-complete", new { ElapsedMs = timer.Elapsed.TotalMilliseconds,
                Validated = validation.ValidatedLocations.Count, validation.ExcludedPaths, validation.CanMergeInventory, plan.Preview.EstimatedBytes, Blocked = plan.BlockedLocations.Count });
            if (work.IsCancellationRequested)
            {
                StatusText.Text = "Review cancelled. No preview was opened.";
                log.Record("review-cancelled", new { Phase = "before-preview", validation.CanMergeInventory });
                return;
            }
            if (plan.Preview.Locations.Count + plan.BlockedLocations.Count > 0)
            {
                var displayed = _snapshot!;
                new ReviewWindow(plan, note, (progress, token) =>
                {
                    var cleanup = ExecuteCleanupAsync(plan, displayed, progress, token, work, log, checkedWorktrees);
                    _cleanupTask = cleanup; return cleanup;
                }) { Owner = this }.ShowDialog();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or Win32Exception
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            log.Record("review-validation-failed", new { ElapsedMs = timer.Elapsed.TotalMilliseconds, Error = ex.ToString() });
            if (!_closing) MarkScanStale(ex is OperationCanceledException
                ? "Selection check cancelled. Review again to check the selected locations."
                : $"Selected locations could not be checked: {ex.Message}", selected.Select(location => new ScanRecheckRequest(location.Path)));
        }
        finally { _cleanupTask = null; _work = null; if (!_closing) { ShowActionLogError(log); SetBusy(false); } }
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

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not { } scan) return;
        new ScanDetailsWindow(scan, _detectionElapsed, _presentationElapsed) { Owner = this }.ShowDialog();
    }

    private async void Elevate_Click(object sender, RoutedEventArgs e) => await RequestElevationAsync();
    private async Task RequestElevationAsync()
    {
        if (_work is not null || _closing || _fastEnabled || Elevation.IsAdministrator) return;
        using var work = new CancellationTokenSource(); _work = work; SetBusy(true);
        try
        {
            _scanHelper?.Dispose(); _scanHelper = await HelperLayoutSource.CreateAsync(true, work.Token);
            _fastEnabled = true; StatusText.Text = "Fast scanning enabled";
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidDataException or OperationCanceledException)
        {
            _fastEnabled = false;
            StatusText.Text = ex is Win32Exception { NativeErrorCode: 1223 } or OperationCanceledException
                ? "Continuing with standard scanning."
                : $"Fast scanning could not start: {ex.Message}. Standard scanning is available.";
        }
        finally { _work = null; if (!_closing) SetBusy(false); }
    }
    private bool _shutdownComplete;
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) { e.Cancel = !_shutdownComplete; return; }
        _closing = true; _opportunitySearchTimer.Stop(); _work?.Cancel(); _browseWork?.Cancel(); _scanHelper?.Dispose(); _extensionLifetime.Cancel();
        e.Cancel = true;
        try
        {
            if (_cleanupTask is not null)
                try { await _cleanupTask; }
                catch (Exception error) { Debug.WriteLine($"Cleanup shutdown: {error}"); }
            if (_worktreeCheckTask is not null)
                try { await _worktreeCheckTask; }
                catch (Exception error) { Debug.WriteLine($"Worktree check shutdown: {error}"); }
            if (_extensions is not null) await _extensions.DisposeAsync();
        }
        catch (Exception error) { Debug.WriteLine($"Extension shutdown: {error}"); }
        finally
        {
            _shutdownComplete = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
