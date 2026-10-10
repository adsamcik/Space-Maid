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
    private CancellationTokenSource? _work;
    private bool _closing;
    private bool _startupOffered;

    public MainWindow()
    {
        InitializeComponent();
        OpportunityGrid.ItemsSource = _rows;
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
        ElevateButton.Visibility = Elevation.IsAdministrator ? Visibility.Collapsed : Visibility.Visible;
        if (drives.Count == 0) StatusText.Text = "No accessible drives were found.";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupOffered || Elevation.IsAdministrator) return;
        _startupOffered = true;
        var startup = new StartupWindow { Owner = this };
        if (startup.ShowDialog() == true) RequestElevation();
    }

    private void Drive_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FreeSpaceText is null) return;
        if (DrivePicker.SelectedItem is DriveChoice drive)
        {
            FreeSpaceText.Text = SizeText.Format(drive.FreeBytes);
            CapacityText.Text = $"of {SizeText.Format(drive.TotalBytes)} total";
        }
        // Results always belong to the selected target, never the previous drive.
        ClearResults();
    }

    private void ClearResults()
    {
        ClearExplorer();
        _detectionElapsed = _presentationElapsed = TimeSpan.Zero;
        _snapshot = null;
        foreach (var row in _rows) row.PropertyChanged -= Row_Changed;
        _rows.Clear();
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailHint.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Visible;
        EmptyCaption.Text = "Scan a drive to review build output, caches,\nenvironments, and files left by coding agents.";
        ScannedSizeText.Text = CandidateText.Text = "—";
        OpportunityHeading.Text = "Opportunities";
        OpportunitiesTab.Content = "Opportunities";
        CandidateCaption.Text = "An estimate, subject to review";
        DetailsButton.Visibility = Visibility.Collapsed;
        UpdateSelection();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || DrivePicker.SelectedItem is not DriveChoice drive) return;
        ClearResults();
        using var work = new CancellationTokenSource();
        _work = work;
        SetBusy(true);
        EmptyCaption.Text = "Reading file metadata. You can cancel at any time.";
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (!_closing) StatusText.Text = $"{p.Phase} · {p.Files:N0} files · {p.Elapsed.TotalSeconds:0.0}s";
            });
            var scan = await new VolumeScanner().ScanAsync(drive.Path, progress, work.Token);
            StatusText.Text = "Finding development clutter…";
            var result = await Task.Run(() =>
            {
                var timer = Stopwatch.StartNew();
                var opportunities = OpportunityDetector.Find(scan, work.Token);
                var total = CleanupPlanner.Create(scan, opportunities, work.Token);
                return (Opportunities: opportunities, Total: total, Elapsed: timer.Elapsed);
            }, work.Token);
            if (_closing) return;
            work.Token.ThrowIfCancellationRequested();
            _snapshot = scan;
            _detectionElapsed = result.Elapsed;
            var presentation = Stopwatch.StartNew();
            foreach (var opportunity in result.Opportunities)
            {
                var row = new OpportunityRow(opportunity, scan.HasAllocationData);
                row.PropertyChanged += Row_Changed;
                _rows.Add(row);
            }
            OpportunityHeading.Text = $"Opportunities  ·  {_rows.Count:N0}";
            ScannedSizeLabel.Text = scan.HasAllocationData ? "OBSERVED ALLOCATION" : "OBSERVED LOGICAL SIZE";
            ScannedSizeText.Text = SizeText.Format(scan.HasAllocationData ? scan.AllocatedBytes : scan.LogicalBytes);
            CandidateText.Text = SizeText.Format(result.Total.EstimatedBytes);
            CandidateCaption.Text = $"{_rows.Count:N0} {(_rows.Count == 1 ? "location" : "locations")} · overlapping paths counted once";
            EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyCaption.Text = "No matching development clutter was found in the accessible files.";
            if (_rows.Count > 0) OpportunityGrid.SelectedIndex = 0;
            OpportunitiesTab.Content = $"Opportunities · {_rows.Count:N0}";
            await InitializeExplorerAsync(scan, result.Opportunities, work.Token);
            _presentationElapsed = presentation.Elapsed;
            _scanStatus = $"{scan.Backend} · {scan.RegularFileCount:N0} files · {scan.DirectoryCount:N0} folders · {(scan.Metrics.Acquisition + scan.Metrics.Aggregation + _detectionElapsed + _presentationElapsed).TotalSeconds:0.00}s"
                + (scan.WarningCount > 0 ? $" · {scan.WarningCount:N0} coverage warnings" : "");
            StatusText.Text = _scanStatus;
            DetailsButton.Visibility = Visibility.Visible;
            try
            {
                var refreshed = new DriveInfo(drive.Path);
                FreeSpaceText.Text = SizeText.Format(refreshed.AvailableFreeSpace);
            }
            catch (IOException) { }
        }
        catch (OperationCanceledException)
        {
            if (!_closing) { ClearResults(); StatusText.Text = "Scan cancelled."; EmptyCaption.Text = "Start another scan when you're ready."; TreeHint.Text = "Scan cancelled. Start another scan when you're ready."; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or OverflowException)
        {
            if (!_closing) { ClearResults(); StatusText.Text = $"Scan could not finish: {ex.Message}"; EmptyCaption.Text = "Check that the drive is connected and accessible, then try again."; }
        }
        finally
        {
            _work = null;
            if (!_closing) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        DrivePicker.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy && DrivePicker.SelectedItem is not null;
        ElevateButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OpportunityGrid.IsEnabled = !busy;
        ExplorePanel.IsEnabled = !busy && !_browsing;
        UpdateSelection();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();
    private void Row_Changed(object? sender, PropertyChangedEventArgs e) => UpdateSelection();
    private void UpdateSelection()
    {
        int selected = _rows.Count(row => row.Selected);
        SelectionText.Text = selected > 0 ? $"{selected:N0} {(selected == 1 ? "location" : "locations")} selected" : "Select locations to preview cleanup";
        ReviewButton.IsEnabled = selected > 0 && _work is null;
    }

    private void Opportunity_Changed(object sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = OpportunityGrid.SelectedItem is OpportunityRow;
        DetailPanel.DataContext = (OpportunityGrid.SelectedItem as OpportunityRow)?.Item;
        DetailPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        DetailHint.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || _work is not null) return;
        var scan = _snapshot;
        var selected = _rows.Where(r => r.Selected).Select(r => r.Item).ToArray();
        using var work = new CancellationTokenSource();
        _work = work;
        SetBusy(true);
        try
        {
            var plan = await Task.Run(() => CleanupPlanner.Create(scan, selected, work.Token), work.Token);
            if (!_closing) new ReviewWindow(plan) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException) { }
        finally { _work = null; if (!_closing) SetBusy(false); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (OpportunityGrid.SelectedItem is not OpportunityRow row) return;
        try
        {
            if (!Directory.Exists(row.Path)) { StatusText.Text = "This location is no longer available. Rescan to refresh the results."; return; }
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(row.Path);
            using var process = Process.Start(start);
        }
        catch (Win32Exception ex) { StatusText.Text = $"Could not open the folder: {ex.Message}"; }
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not { } scan) return;
        new ScanDetailsWindow(scan, _detectionElapsed, _presentationElapsed) { Owner = this }.ShowDialog();
    }

    private void Elevate_Click(object sender, RoutedEventArgs e) => RequestElevation();
    private void RequestElevation()
    {
        if (Elevation.TryRelaunch(out var error)) Close();
        else StatusText.Text = error is null ? "Continuing without administrator access. Standard scanning is available."
            : $"Could not enable fast scanning: {error}. Standard scanning is available.";
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { _closing = true; _work?.Cancel(); _browseWork?.Cancel(); }
}
