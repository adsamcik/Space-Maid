using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private HelperLayoutSource? _scanHelper;
    private bool _fastEnabled;
    private INtfsLayoutSource? FastMetadataSource() => _scanHelper?.IsAvailable == true ? _scanHelper
        : Elevation.IsAdministrator ? new NativeNtfsLayoutSource() : null;
    private string _scanStatus = "Ready · Scan a drive to explore space";
    private TimeSpan _detectionElapsed, _presentationElapsed;

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_editingPath) await NavigatePathAsync(PathEditor.Text, forceScan: true);
        else await ScanCurrentPathAsync();
    }

    internal async Task<bool> ScanCurrentPathAsync()
    {
        if (_work is not null || _browsing || _closing || _currentPath.Length == 0) return false;
        _driveRestoreAttempted = true;
        string target = _currentPath;
        ClearResults();
        using var work = new CancellationTokenSource();
        _work = work;
        using var log = new ScanLog(LogDirectory);
        _scanLogPath = log.FilePath;
        OpenLogButton.Visibility = _scanLogPath is null ? Visibility.Collapsed : Visibility.Visible;
        OpenLogButton.ToolTip = _scanLogPath;
        BackendStatusText.Visibility = Visibility.Visible;
        BackendStatusText.Text = "Selecting scanner…";
        log.Record("app-scan-start", new { Target = target, Administrator = Elevation.IsAdministrator,
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
            bool helperStarted = false;
            string root = Path.GetPathRoot(target)!;
            string? historyVolume = await Task.Run(() => DriveDiscovery.VolumeId(root)).WaitAsync(work.Token);
            var selectedDrive = root.Length == 3 ? new DriveInfo(root) : null;
            if (_fastEnabled && _scanHelper?.IsAvailable != true
                && selectedDrive is not null && selectedDrive.DriveType is DriveType.Fixed or DriveType.Removable
                && selectedDrive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _scanHelper?.Dispose();
                    _scanHelper = await HelperLayoutSource.CreateAsync(true, work.Token);
                    helperStarted = true;
                    log.Record("helper-started", new { _scanHelper.ProcessId });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception)
                {
                    _fastEnabled = false; helperFailure = $"Fast scan helper was unavailable: {ex.Message}. Standard scanning was used.";
                    log.Record("helper-unavailable", new { Error = ex.ToString() });
                }
            }
            if (_scanHelper?.IsAvailable == true) log.Record("helper-session", new { _scanHelper.ProcessId, Reused = !helperStarted });
            var scan = await new VolumeScanner().ScanAsync(target, progress, work.Token,
                preferFast: _fastEnabled || Elevation.IsAdministrator, diagnostic: d => log.Record(d.Event, d), layoutSource: FastMetadataSource(),
                fastUnavailableReason: helperFailure);
            acquiring = false;
            if (_closing) return false;
            if (progress.LatestPreliminary is { } preliminary) ShowPreliminary(preliminary);
            PreliminaryCaption.Text = "Preparing results…";
            BackendStatusText.Text = scan.Backend + (scan.FallbackReason is null ? "" : $" · {scan.FallbackReason}");
            StatusText.Text = "Finding cleanup opportunities…";
            await InitializeExtensionsAsync().WaitAsync(work.Token);
            log.Record("detection-start", new { scan.FileCount, scan.DirectoryCount });
            var result = await Task.Run(() =>
            {
                var timer = Stopwatch.StartNew();
                var analysis = OpportunityExtensions.Analyze(scan, work.Token);
                var opportunities = analysis.Findings;
                var total = CleanupPlanner.Create(scan, opportunities, work.Token);
                return (Opportunities: opportunities, Total: total, Groups: analysis.Groups, Elapsed: timer.Elapsed);
            }, work.Token);
            if (_closing) return false;
            work.Token.ThrowIfCancellationRequested();
            _snapshot = scan;
            _detectionElapsed = result.Elapsed;
            log.Record("opportunity-extensions", new { Providers = _extensions?.Entries.Select(entry => new { entry.Id, entry.Status }),
                Status = OpportunityExtensions.Status(scan), ExtensionLog = _extensions?.LogPath });
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
            _scanStatus = $"{scan.RegularFileCount:N0} files · {scan.DirectoryCount:N0} folders · {(scan.Metrics.Acquisition + scan.Metrics.Aggregation + _detectionElapsed + _presentationElapsed).TotalSeconds:0.00}s"
                + (scan.WarningCount > 0 ? $" · {scan.WarningCount:N0} coverage warnings" : "");
            string extensionStatus = OpportunityExtensions.Status(scan);
            if (extensionStatus.Length > 0) _scanStatus += " · " + extensionStatus;
            StatusText.Text = _scanStatus;
            DetailsButton.Visibility = Visibility.Visible;
            RefreshPathDriveSpace();
            string? verifiedVolume = await RememberDriveScanAsync(scan, historyVolume);
            if (_closing) return false;
            work.Token.ThrowIfCancellationRequested();
            _snapshotVolumeId = verifiedVolume;
            return true;
        }
        catch (OperationCanceledException)
        {
            acquiring = false;
            log.Record("scan-cancelled", new { Target = target });
            if (!_closing)
            {
                if (progress.LatestPreliminary is { } preliminary) ShowPreliminary(preliminary);
                RetainCancelledPreliminary();
                StatusText.Text = _preliminary is null ? "Scan cancelled." : "Scan cancelled · Preliminary results retained.";
                EmptyCaption.Text = "Start another scan to calculate cleanup estimates.";
                TreeHint.Text = "Scan cancelled. Start another scan when you're ready.";
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or OverflowException)
        {
            log.Record("scan-failed", new { Target = target, Error = ex.ToString(), Win32Error = (ex as Win32Exception)?.NativeErrorCode });
            if (!_closing) { ClearResults(); StatusText.Text = $"Scan could not finish: {ex.Message}"; EmptyCaption.Text = "Check that the drive is connected and accessible, then try again."; }
            return false;
        }
        finally
        {
            acquiring = false;
            if (_closing) _scanHelper?.Dispose();
            if (_scanHelper is { IsAvailable: false })
            {
                log.Record("helper-disconnected", new { _scanHelper.ProcessId });
                _scanHelper.Dispose(); _scanHelper = null; _fastEnabled = false;
            }
            if (!_closing && log.Error is not null) BackendStatusText.Text += $" · Logging unavailable: {log.Error}";
            _work = null;
            if (!_closing) SetBusy(false);
        }
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
        finally { _work = null; if (_closing) _scanHelper?.Dispose(); else SetBusy(false); }
    }
}
