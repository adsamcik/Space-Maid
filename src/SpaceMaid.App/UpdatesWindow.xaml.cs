using System.Windows;

namespace SpaceMaid.App;

public partial class UpdatesWindow : Window
{
    private readonly AppUpdates _updates;
    private readonly Func<bool> _canRestart;
    private readonly Action _restart;
    internal UpdatesWindow(AppUpdates updates, Func<bool> canRestart, Action restart)
    {
        InitializeComponent(); _updates = updates; _canRestart = canRestart; _restart = restart;
        _updates.Changed += Refresh;
        Closed += (_, _) => _updates.Changed -= Refresh;
        Activated += (_, _) => Refresh();
        Refresh();
    }

    internal void Refresh()
    {
        VersionText.Text = _updates.CurrentVersion;
        StatusText.Text = _updates.Status;
        AutomaticBox.IsChecked = _updates.AutomaticChecks;
        AutomaticBox.IsEnabled = _updates.Supported;
        CheckButton.IsEnabled = _updates.Supported && !_updates.Busy;
        ActionButton.Visibility = _updates.AvailableVersion is null ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = _updates.Ready ? "Restart and update" : "Download update";
        ActionButton.IsEnabled = !_updates.Busy && (!_updates.Ready || _canRestart());
        BusyHint.Visibility = _updates.Ready && !_canRestart() ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = _updates.Busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = _updates.Status == "Checking…" ? "Cancel" : "Cancel download";
        ProgressBar.Visibility = _updates.Busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.IsIndeterminate = _updates.Status == "Checking…";
        ProgressBar.Value = _updates.Progress;
        ErrorDetails.Visibility = _updates.Error is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = _updates.Error ?? "";
    }
    private async void Check_Click(object sender, RoutedEventArgs e) => await _updates.CheckAsync();
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_updates.Ready) { if (_canRestart()) { Close(); _restart(); } }
        else await _updates.DownloadAsync();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _updates.Cancel();
    private void Automatic_Click(object sender, RoutedEventArgs e) => _updates.SetAutomaticChecks(AutomaticBox.IsChecked == true);
}
