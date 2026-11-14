using System.Windows;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class RecycleLocksWindow : Window
{
    private readonly FolderDeletionTarget _target;
    private readonly RecycleLockRecovery _recovery;
    private readonly CancellationTokenSource _work;
    private bool _busy;
    private bool _closed;

    internal RecycleLocksWindow(FolderDeletionTarget target, RecycleLockRecovery recovery, CancellationToken token)
    {
        _target = target;
        _recovery = recovery;
        _work = CancellationTokenSource.CreateLinkedTokenSource(token);
        InitializeComponent();
        FolderPath.Text = target.Path;
        Loaded += async (_, _) => await InspectAsync(closeApps: false);
        Closed += (_, _) => { _closed = true; _work.Cancel(); _work.Dispose(); };
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) => await InspectAsync(closeApps: false);
    private async void CloseApps_Click(object sender, RoutedEventArgs e) => await InspectAsync(closeApps: true);
    private void Retry_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Owners_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CloseAppsButton is not null) UpdateCloseButton();
    }
    private void UpdateCloseButton() =>
        CloseAppsButton.IsEnabled = !_busy && Owners.SelectedItems.OfType<RecycleLockOwner>().Any(item => item.CanClose);

    private async Task InspectAsync(bool closeApps)
    {
        if (_busy) return;
        var approved = closeApps ? Owners.SelectedItems.Cast<RecycleLockOwner>().Where(item => item.CanClose).ToArray() : [];
        if (closeApps && approved.Length == 0) return;
        _busy = true;
        RecheckButton.Visibility = RetryButton.Visibility = CloseAppsButton.Visibility = Visibility.Collapsed;
        ResultText.Text = closeApps ? "Waiting for apps to release their files…" : "Checking open files…";
        var token = _work.Token;
        try
        {
            if (closeApps && await Task.Run(() => _recovery.CloseAndCheckAsync(_target, approved, token), token))
            {
                if (!_closed) DialogResult = true;
                return;
            }
            var report = await Task.Run(() => _recovery.Inspect(_target, token), token);
            if (_closed) return;
            Owners.ItemsSource = report.Owners;
            Owners.Visibility = report.Owners.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Owners.UnselectAll();
            bool canClose = report.Owners.Any(owner => owner.CanClose);
            bool clear = report is { LockedFiles: 0, Incomplete: false };
            ResultText.Text = clear ? "No blocking files found in this check. You can retry recycling."
                : report.Owners.Length == 0 ? "Windows couldn't identify an app to close. Close apps using this folder, then check again."
                : closeApps ? "Some files are still open. Finish any save prompts, then check again."
                : canClose ? "Save your work, then select the apps you want to close."
                : "Close the listed apps manually, then check again.";
            if (report.Incomplete) ResultText.Text += "\nSome files or folders could not be checked.";
            RetryButton.Visibility = clear || report.Incomplete || report.Owners.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RecheckButton.Visibility = clear ? Visibility.Collapsed : Visibility.Visible;
            CloseAppsButton.Visibility = canClose ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        {
            if (_closed) return;
            Owners.ItemsSource = null;
            Owners.Visibility = Visibility.Collapsed;
            ResultText.Text = "Could not check open files. " + error.Message;
            RecheckButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            if (!_closed) UpdateCloseButton();
        }
    }
}
