using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private DriveHistoryStore? _driveHistory;
    private Task? _driveInitialization, _driveDiscovery;
    private IReadOnlyList<DriveDescriptor> _availableDrives = [];
    private bool _driveRestoreAttempted;
    internal string DriveHistoryPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceMaid", "drive-history.json");
    internal Func<IReadOnlyList<DriveDescriptor>> DriveProvider { get; set; } = DriveDiscovery.Read;
    internal Func<string, (long Total, long Free)> DriveSpaceProvider { get; set; } = DriveDiscovery.Space;

    private Task InitializeDriveHistoryAsync() => _driveInitialization ??= LoadDriveHistoryAsync();
    private async Task LoadDriveHistoryAsync()
    {
        _driveHistory = new(DriveHistoryPath);
        await _driveHistory.LoadAsync();
    }

    internal async Task RefreshDriveChoicesAsync()
    {
        if (_driveDiscovery is { } existing) { await existing; return; }
        var load = LoadDriveChoicesAsync(); _driveDiscovery = load;
        try { await load; }
        finally { if (ReferenceEquals(_driveDiscovery, load)) _driveDiscovery = null; }
    }

    private async Task LoadDriveChoicesAsync()
    {
        await InitializeDriveHistoryAsync();
        if (_closing) return;
        DrivePickerNote.Text = _availableDrives.Count == 0 ? "Loading drives…" : "";
        try
        {
            _availableDrives = await Task.Run(DriveProvider);
            if (_closing) return;
            if (!_driveRestoreAttempted)
            {
                _driveRestoreAttempted = true;
                var last = _availableDrives.FirstOrDefault(drive => drive.Ready && drive.VolumeId is not null && drive.VolumeId == _driveHistory!.LastVolumeId);
                if (last is not null && _snapshot is null && _work is null && !_openingPath && !_editingPath
                    && _currentPath.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                    SetScanTarget(last.RootPath);
            }
            DisplayDriveChoices();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { DrivePickerNote.Text = "Drives unavailable"; DrivePickerNote.ToolTip = error.Message; }
    }

    private void DisplayDriveChoices()
    {
        string? root = Path.GetPathRoot(_currentPath);
        DriveChoices.ItemsSource = _availableDrives.Select(drive => new DriveChoice(drive,
                drive.VolumeId is null ? null : _driveHistory?.Find(drive.VolumeId, drive.RootPath),
                drive.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(choice => choice.Drive.VolumeId is not null && choice.Drive.VolumeId == _driveHistory?.LastVolumeId)
            .ThenByDescending(choice => choice.Drive.LowSpace).ThenByDescending(choice => choice.History?.LastUsedUtc)
            .ThenBy(choice => choice.RootPath, StringComparer.OrdinalIgnoreCase).ToArray();
        DrivePickerNote.Text = _driveHistory?.Error is not null ? "History unavailable" : _availableDrives.Count == 0 ? "No drives found" : "";
        DrivePickerNote.ToolTip = _driveHistory?.Error;
    }

    private async void Drives_Click(object sender, RoutedEventArgs e)
    {
        if (!PathBar.IsEnabled) return;
        if (DrivePopup.IsOpen) { DrivePopup.IsOpen = false; return; }
        EndPathEdit();
        DrivePopup.IsOpen = true;
        await RefreshDriveChoicesAsync();
        if (DrivePopup.IsOpen) DriveChoices.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private async void ChooseDrive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DriveChoice choice } button)
            await ChooseDriveAsync(choice, Equals(button.Tag, "recent") ? choice.RecentPath : null);
    }

    internal async Task ChooseDriveAsync(DriveChoice choice, string? recentPath = null)
    {
        if (_closing || _work is not null || _browsing || _openingPath || !choice.Ready) return;
        _driveRestoreAttempted = true;
        string target = recentPath ?? choice.RootPath;
        DrivePopup.IsOpen = false; EndPathEdit(); ExploreTab.IsChecked = true;
        var scan = _snapshot;
        // Choosing a drive/folder never starts a scan. Reuse the current
        // inventory where possible; otherwise select the next scan target.
        if (scan is not null && target.Equals(scan.RootPath, StringComparison.OrdinalIgnoreCase))
            await NavigateToEntryAsync(-1);
        else if (scan?.FindEntry(target) is { } link && scan.Entry(link) is { IsDirectory: true, IsPrimaryName: true } entry
            && !scan.IsUnmeasuredDirectory(entry.FileIndex)) await NavigateToEntryAsync(link);
        else { SetScanTarget(target); StatusText.Text = "Ready · Press Scan"; }
        if (choice.Drive.VolumeId is { } identity)
        {
            await InitializeDriveHistoryAsync();
            _driveHistory!.RememberSelection(identity, choice.RootPath, target);
            await _driveHistory.SaveAsync();
        }
        if (!_closing) { DisplayDriveChoices(); DrivesButton.Focus(); }
    }

    private async Task RememberDriveScanAsync(ScanSnapshot scan, string? originalVolume)
    {
        await InitializeDriveHistoryAsync();
        string root = Path.GetPathRoot(scan.RootPath)!;
        // Resolve again after scanning: a disconnected/remapped drive must
        // not receive another volume's successful-scan history.
        string? identity = await Task.Run(() => DriveDiscovery.VolumeId(root));
        if (identity is null || !identity.Equals(originalVolume, StringComparison.OrdinalIgnoreCase) || _closing) return;
        _driveHistory!.RememberSelection(identity, root, scan.RootPath);
        _driveHistory.RecordScan(identity, root, scan.RootPath, scan.ScannedAtUtc, scan.HasAllocationData, scan.WarningCount > 0,
            _cleanupGroups.Select(group => new DriveRecommendation(group.Key == "cleanup:worktrees" ? "Worktrees to review" : group.Name, group.ObservedBytes)));
        await _driveHistory.SaveAsync();
        if (!_closing) DisplayDriveChoices();
    }

    private void DrivePopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; DrivePopup.IsOpen = false; DrivesButton.Focus(); }
    }
}
