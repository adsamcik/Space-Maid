using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private AppUpdates? _updates;
    private UpdatesWindow? _updatesWindow;
    private bool _restartForUpdate;
    private DispatcherTimer? _updateTimer;
    internal string UpdateSettingsPath { get; set; } = AppData.File("update-settings.json");

    private void InitializeUpdates()
    {
        if (_updates is not null) return;
        _updates = AppUpdates.Create(UpdateSettingsPath, Path.Combine(LogDirectory, "Updates"));
        _updates.Changed += UpdateUpdateButton;
        UpdateUpdateButton();
        _updateTimer = new(TimeSpan.FromMinutes(5), DispatcherPriority.Background,
            (_, _) => _ = _updates.RunAutomaticAsync(), Dispatcher);
        _ = _updates.RunAutomaticAsync();
    }

    private bool CanRestartForUpdate() => !_closing && _work is null && !_browsing && !_openingPath
        && _extensions?.HasOptimizationWork != true
        && _cleanupTask is null && _worktreeCheckTask is null
        && _extensions?.RecentJobs.Any(job => job.State is ExtensionJobState.Queued or ExtensionJobState.Running) != true;

    private void UpdateUpdateButton()
    {
        if (_closing) return;
        bool attention = _updates is { Supported: true } && (_updates.AvailableVersion is not null
            || _updates.CheckRetryAvailable || _updates.Error is not null);
        UpdateButton.Visibility = attention ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = _updates?.CheckRetryAvailable == true ? "Retry update check" : "Update available";
        UpdateButton.ToolTip = _updates is null ? null : _updates.Status + "\n" + _updates.CheckHistory;
        _updatesWindow?.Refresh();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    { AddAgentAccessMenu(); MoreButton.ContextMenu.PlacementTarget = MoreButton; MoreButton.ContextMenu.IsOpen = true; }

    private void Updates_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_updates is null) InitializeUpdates();
        if (_updatesWindow is not null) { _updatesWindow.Activate(); return; }
        bool CanRestart() => CanRestartForUpdate() && !HasDeletionWork;
        _updatesWindow = new(_updates!, CanRestart, () => { if (CanRestart()) { _restartForUpdate = true; Close(); } }) { Owner = this };
        _updatesWindow.Closed += (_, _) => _updatesWindow = null;
        _updatesWindow.Show();
    }
}
