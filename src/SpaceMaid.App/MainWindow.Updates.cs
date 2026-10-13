using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private AppUpdates? _updates;
    private UpdatesWindow? _updatesWindow;
    private bool _restartForUpdate;
    internal string UpdateSettingsPath { get; set; } = AppData.File("update-settings.json");

    private void InitializeUpdates()
    {
        if (_updates is not null) return;
        _updates = AppUpdates.Create(UpdateSettingsPath, Path.Combine(LogDirectory, "Updates"));
        _updates.Changed += UpdateUpdateButton;
        UpdateUpdateButton();
        if (_updates.AutomaticCheckDue) _ = _updates.CheckAsync();
    }

    private bool CanRestartForUpdate() => !_closing && _work is null && !_browsing && !_openingPath
        && _cleanupTask is null && _worktreeCheckTask is null
        && _extensions?.RecentJobs.Any(job => job.State is ExtensionJobState.Queued or ExtensionJobState.Running) != true;

    private void UpdateUpdateButton()
    {
        if (_closing) return;
        UpdateButton.Visibility = _updates?.AvailableVersion is null ? Visibility.Collapsed : Visibility.Visible;
        _updatesWindow?.Refresh();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    { MoreButton.ContextMenu.PlacementTarget = MoreButton; MoreButton.ContextMenu.IsOpen = true; }

    private void Updates_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_updates is null) InitializeUpdates();
        if (_updatesWindow is not null) { _updatesWindow.Activate(); return; }
        _updatesWindow = new(_updates!, CanRestartForUpdate, () => { if (CanRestartForUpdate()) { _restartForUpdate = true; Close(); } }) { Owner = this };
        _updatesWindow.Closed += (_, _) => _updatesWindow = null;
        _updatesWindow.Show();
    }
}
