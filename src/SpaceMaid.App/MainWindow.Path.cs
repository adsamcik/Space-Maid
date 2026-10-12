using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private string _currentPath = "";
    private bool _editingPath, _openingPath;
    private int _driveSpaceVersion;
    private string? _capacityRoot;
    internal string CurrentPath => _currentPath;

    private void ShowPath(string path)
    {
        if (_currentPath.Equals(path, StringComparison.Ordinal) && DriveRootText.Text.Length > 0) return;
        string? previousRoot = Path.GetPathRoot(_currentPath);
        _currentPath = path;
        UpdateDriveLocation();
        OpportunityScopeText.Visibility = OpportunityScopeText.Text == path ? Visibility.Collapsed : Visibility.Visible;
        if (!_editingPath) PathEditor.Text = path;
        foreach (var button in Breadcrumbs.Children.OfType<Button>())
            if (button.ContextMenu is { } previousMenu) previousMenu.IsOpen = false;
        Breadcrumbs.Children.Clear();
        var crumbs = PathNavigation.Crumbs(path);
        for (int index = 1; index < crumbs.Count; index++)
        {
            if (index > 0) Breadcrumbs.Children.Add(new TextBlock { Text = "›", Opacity = .6, VerticalAlignment = VerticalAlignment.Center });
            if (index == 1 && crumbs.Count > 3)
            {
                var menu = new ContextMenu();
                foreach (var hidden in crumbs.Skip(1).Take(crumbs.Count - 3))
                {
                    var item = new MenuItem { Header = hidden.Name, ToolTip = hidden.Path };
                    item.Click += async (_, _) => await NavigatePathAsync(hidden.Path);
                    menu.Items.Add(item);
                }
                var overflow = new Button { Content = "…", Style = (Style)FindResource("PathCrumb"), ToolTip = "Parent folders", ContextMenu = menu };
                AutomationProperties.SetName(overflow, "Parent folders");
                overflow.Click += (_, _) => { menu.PlacementTarget = overflow; menu.IsOpen = true; };
                Breadcrumbs.Children.Add(overflow);
                index = crumbs.Count - 3;
                continue;
            }
            var crumb = crumbs[index];
            var button = new Button
            {
                Content = new TextBlock { Text = crumb.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 },
                Style = (Style)FindResource("PathCrumb"), ToolTip = crumb.Path, Tag = crumb.Path,
                FontWeight = index == crumbs.Count - 1 ? FontWeights.SemiBold : FontWeights.Normal
            };
            AutomationProperties.SetName(button, $"Open {crumb.Path}");
            button.Click += async (_, _) => await NavigatePathAsync(crumb.Path);
            Breadcrumbs.Children.Add(button);
        }
        PathBar.ToolTip = path;
        if (!string.Equals(previousRoot, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)) RefreshPathDriveSpace();
    }

    private void UpdateDriveLocation()
    {
        string root = Path.GetPathRoot(_currentPath)!;
        DriveRootText.Text = root;
        string title = _availableDrives.FirstOrDefault(drive => drive.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase))?.Label ?? "";
        DriveRootButton.ToolTip = string.IsNullOrEmpty(title) ? $"Open {root}" : $"Open {root} · {title}";
        AutomationProperties.SetName(DriveRootButton, $"Open drive root {root}");
        UpdateAppsDrive();
    }

    private async void DriveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (!PathBar.IsEnabled) return;
        string root = Path.GetPathRoot(_currentPath)!;
        var drive = _availableDrives.FirstOrDefault(item => item.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase))
            ?? new DriveDescriptor(root, "", null, true);
        await ChooseDriveAsync(new(drive, null, true));
    }

    private async void RefreshPathDriveSpace()
    {
        string root = Path.GetPathRoot(_currentPath)!;
        int version = ++_driveSpaceVersion;
        if (!root.Equals(_capacityRoot, StringComparison.OrdinalIgnoreCase))
        {
            _capacityRoot = root;
            FreeSpaceText.Text = "—"; CapacityText.Text = ""; DriveCapacityBar.Value = 0;
            DriveCapacityBar.ToolTip = null;
        }
        try
        {
            var space = await Task.Run(() => DriveSpaceProvider(root));
            if (!_closing && version == _driveSpaceVersion) ShowDriveSpace(space.Total, space.Free);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            if (_closing || version != _driveSpaceVersion) return;
            FreeSpaceText.Text = "—"; CapacityText.Text = "Capacity unavailable";
            DriveCapacityBar.Value = 0; DriveCapacityBar.ToolTip = null;
            AutomationProperties.SetHelpText(DriveCapacityBar, "Capacity unavailable");
        }
    }

    private void UpdatePathAvailability(bool busy)
    {
        if (busy || _browsing || _openingPath) DrivePopup.IsOpen = false;
        PathBar.IsEnabled = !busy && !_browsing && !_openingPath;
        ScanButton.IsEnabled = !busy && !_browsing && !_openingPath && _currentPath.Length > 0;
    }

    private void BeginPathEdit()
    {
        if (!PathBar.IsEnabled) return;
        _driveRestoreAttempted = true;
        if (!_editingPath) PathEditor.Text = _currentPath;
        _editingPath = true;
        PathScroll.Visibility = EditPathButton.Visibility = DriveRootButton.Visibility = Visibility.Collapsed;
        DriveSwitchLabel.Visibility = Visibility.Visible;
        PathEditor.Visibility = Visibility.Visible;
        PathEditor.Focus(); PathEditor.SelectAll();
    }

    private void EndPathEdit()
    {
        _editingPath = false; PathEditor.Text = _currentPath;
        PathEditor.Visibility = Visibility.Collapsed;
        PathScroll.Visibility = EditPathButton.Visibility = DriveRootButton.Visibility = Visibility.Visible;
        DriveSwitchLabel.Visibility = Visibility.Collapsed;
    }

    private void EditPath_Click(object sender, RoutedEventArgs e) => BeginPathEdit();
    private void PathBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editingPath) return;
        for (DependencyObject? source = e.OriginalSource as DependencyObject; source is not null && source != PathBar; source = VisualTreeHelper.GetParent(source))
            if (source is Button) return;
        e.Handled = true; BeginPathEdit();
    }
    private async void PathEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await NavigatePathAsync(PathEditor.Text); }
        else if (e.Key == Key.Escape) { e.Handled = true; EndPathEdit(); EditPathButton.Focus(); }
    }
    private void PathEditor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_editingPath && !_openingPath && e.NewFocus != ScanButton) EndPathEdit();
    }
    private async void Window_PathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.SystemKey == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt && PathBar.IsEnabled)
        { e.Handled = true; Drives_Click(DrivesButton, new RoutedEventArgs()); }
        else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control
            || e.SystemKey == Key.D && Keyboard.Modifiers == ModifierKeys.Alt)
        { e.Handled = true; BeginPathEdit(); }
        else if (e.SystemKey == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt && !_editingPath
            && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_currentPath)) is { } parent)
        { e.Handled = true; await NavigatePathAsync(parent); }
    }

    internal async Task<bool> NavigatePathAsync(string input, bool forceScan = false)
    {
        if (_closing || _work is not null || _browsing || _openingPath) return false;
        _driveRestoreAttempted = true;
        _openingPath = true;
        try
        {
            string path; int? link; var scan = _snapshot;
            using (var check = new CancellationTokenSource())
            {
                var token = check.Token;
                _work = check; SetBusy(true); StatusText.Text = "Opening folder…";
                try
                {
                    path = PathNavigation.Normalize(input, _currentPath);
                    link = await Task.Run(() =>
                    {
                        if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
                            throw new IOException("Enter a folder path.");
                        token.ThrowIfCancellationRequested();
                        return scan?.FindEntry(path);
                    }, token).WaitAsync(token);
                }
                catch (OperationCanceledException) { StatusText.Text = "Opening cancelled"; return false; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { StatusText.Text = "Could not open folder: " + error.Message; return false; }
                finally { _work = null; if (!_closing) SetBusy(false); }
            }
            if (_closing) return false;
            EndPathEdit(); ExploreTab.IsChecked = true;
            if (!forceScan && ReferenceEquals(_snapshot, scan) && scan is not null && link is { } destination
                && scan.Entry(destination) is { IsDirectory: true, IsPrimaryName: true } entry && !scan.IsUnmeasuredDirectory(entry.FileIndex))
                await NavigateToEntryAsync(destination);
            else
            { SetScanTarget(path); await ScanCurrentPathAsync(); }
            // Focusing the list can restore its previously focused row and
            // navigate away again. Focus the chosen row instead.
            if (SpaceTree.SelectedItem is { } selected
                && SpaceTree.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem container) container.Focus();
            return true;
        }
        finally
        {
            _openingPath = false;
            if (!_closing)
            {
                UpdatePathAvailability(_work is not null);
                if (_editingPath) PathEditor.Focus();
                DrainExplorerRecheck();
            }
        }
    }
}
