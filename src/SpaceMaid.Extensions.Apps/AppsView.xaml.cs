using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Apps;

public partial class AppsView : UserControl, IDriveExtensionView, IExtensionContextView
{
    private readonly IExtensionHost _host;
    private readonly CancellationToken _lifetime;
    private readonly ObservableCollection<AppRow> _rows = [];
    private readonly List<string> _addedFolders = [];
    private AppFolderSettings? _folderSettings;
    private string? _folderSettingsPath;
    private Task _settingsOperation = Task.CompletedTask;
    private bool _settingsBusy;
    private string? _settingsError;
    private bool _settingsWritable = true;
    private CancellationTokenSource? _work;
    private Task _pending = Task.CompletedTask;
    private string? _drive;
    private bool _discovered, _shutdown;
    private int _revision;
    internal Func<string, IReadOnlyList<string>, CancellationToken, AppDiscoveryResult> Discover { get; set; } = AppDiscovery.Discover;

    internal AppsView(IExtensionHost host, CancellationToken lifetime)
    {
        _host = host; _lifetime = lifetime;
        Discover = (drive, added, token) => AppDiscovery.Discover(host.Files, drive, added, token);
        InitializeComponent();
        ExploreButton.Visibility = (host as IExtensionNavigationHost)?.Navigation is null ? Visibility.Collapsed : Visibility.Visible;
        AppList.ItemsSource = _rows;
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.Filter = item => item is AppRow row && Matches(row);
        view.SortDescriptions.Add(new(nameof(AppRow.SizeBytes), ListSortDirection.Descending));
        SizeColumn.SortDirection = ListSortDirection.Descending;
        IsVisibleChanged += (_, _) => { if (IsVisible && !_discovered && _drive is not null) _ = RefreshAsync(false); };
    }
    public void SetDrive(string root)
    {
        root = Path.GetPathRoot(Path.GetFullPath(root)) ?? throw new ArgumentException("Choose a drive.", nameof(root));
        if (string.Equals(root, _drive, StringComparison.OrdinalIgnoreCase)) return;
        _drive = root; _revision++; _work?.Cancel(); _discovered = false;
        _rows.Clear(); DetailsPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Apps on {root}";
        if (IsVisible) _ = RefreshAsync(false);
    }
    public void SetContext(ExtensionViewContext context)
    {
        if (context.DriveRoot is { Length: > 0 } root) SetDrive(root);
    }

    private async void Explore_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not AppRow { App.Path: { } path }
            || (_host as IExtensionNavigationHost)?.Navigation is not { } navigation) return;
        if (!await navigation.ExploreFolderAsync(path)) StatusText.Text = "Could not open this folder. Finish the current operation and try again.";
    }
    internal Task RefreshAsync(bool refreshSizes = true)
    {
        if (_shutdown || _drive is null) return Task.CompletedTask;
        _work?.Cancel();
        var previous = _pending;
        var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _work = work;
        int revision = ++_revision;
        string drive = _drive;
        return _pending = RunAsync(previous, work, revision, drive, _addedFolders.ToArray(), refreshSizes);
    }
    private async Task RunAsync(Task previous, CancellationTokenSource work, int revision, string drive, string[] added, bool refreshSizes)
    {
        var timer = Stopwatch.StartNew();
        int measured = 0;
        string? selectedPath = (AppList.SelectedItem as AppRow)?.App.Path;
        try
        {
            await previous;
            if (!Current()) return;
            Busy(true); _discovered = true;
            UpdateEmptyState();
            await ReloadSavedFoldersAsync(work.Token);
            if (!Current()) return;
            added = _addedFolders.ToArray();
            StatusText.Text = $"Finding apps on {drive}…";
            var discovered = await Task.Run(() => Discover(drive, added, work.Token), work.Token);
            if (!Current()) return;
            _rows.Clear();
            foreach (var app in discovered.Apps) _rows.Add(new(app));
            UpdateList();
            AppList.SelectedItem = _rows.FirstOrDefault(row => row.App.Path == selectedPath);
            foreach (var row in _rows.ToArray())
            {
                work.Token.ThrowIfCancellationRequested();
                if (!Current()) return;
                StatusText.Text = $"{_rows.Count:N0} app folders on {drive} · Measuring {row.Name}";
                try
                {
                    InventoryFolderSize? size = null;
                    if (_host.Inventory is IInventoryFolderSizes sizes)
                    {
                        if (!refreshSizes) size = await sizes.ReadFolderSizeAsync(row.App.Path!, false, work.Token);
                        size ??= await sizes.ReadFolderSizeAsync(row.App.Path!, true, work.Token);
                    }
                    else size = await ReadLogicalSizeAsync(row.App.Path!, work.Token);
                    if (!Current()) return;
                    row.SetMeasurement(size); measured++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { if (Current()) row.SetMeasurement(null, "Could not measure this folder: " + error.Message); }
                UpdateBars();
            }
            if (!Current()) return;
            // Sorting once at completion keeps rows from jumping during discovery.
            UpdateList();
            StatusText.Text = $"{_rows.Count:N0} apps · {measured:N0} measured"
                + (discovered.Warnings.Count > 0 ? " · Some folders unavailable" : "");
            StatusText.ToolTip = string.Join("\n", discovered.Warnings);
            CoverageText.Text = "Install folders only. App data and caches elsewhere are excluded. Windows Store packages and apps outside known locations may need Add app folder."
                + (discovered.Warnings.Count > 0 ? "\n" + string.Join("\n", discovered.Warnings) : "");
            _host.Log("apps-discovery", new { Drive = drive, Apps = _rows.Count, Measured = measured, Milliseconds = timer.ElapsedMilliseconds, Warnings = discovered.Warnings });
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        { if (Current()) { StatusText.Text = $"Cancelled · {_rows.Count:N0} apps · {measured:N0} measured"; _discovered = _rows.Count > 0; UpdateList(); } }
        catch (Exception error)
        { if (Current()) { StatusText.Text = "Couldn't find apps. Select Refresh to try again."; StatusText.ToolTip = error.Message; _discovered = false; } }
        finally
        {
            if (ReferenceEquals(_work, work)) { _work = null; Busy(false); UpdateEmptyState(); }
            work.Dispose();
        }
        bool Current() => !_shutdown && revision == _revision;
    }
    private async Task<InventoryFolderSize> ReadLogicalSizeAsync(string path, CancellationToken token)
    {
        long logical = 0; var seen = new HashSet<int>(); InventoryCoverage? coverage = null;
        await foreach (var batch in _host.Inventory.ReadAsync(path, token))
        {
            coverage = batch.Coverage;
            foreach (var item in batch.Items)
                if (!item.IsDirectory && seen.Add(item.ObjectIndex)) logical = checked(logical + item.LogicalBytes);
        }
        return new(logical, null, coverage?.Complete == true, coverage?.ObservedAtUtc ?? DateTime.UtcNow,
            "Folder file sizes", coverage?.Warnings ?? ["Folder coverage is unavailable."]);
    }
    private bool Matches(AppRow row)
    {
        string search = SearchBox.Text.Trim();
        return search.Length == 0 || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase) || row.PathText.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.App.Names.Any(name => name.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
    private void UpdateList()
    {
        if (AppList.ItemsSource is null) return;
        CollectionViewSource.GetDefaultView(_rows).Refresh();
        UpdateBars();
        UpdateEmptyState();
    }
    private void UpdateEmptyState()
    {
        EmptyPanel.Visibility = AppList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (AppList.Items.Count == 0)
            EmptyText.Text = _rows.Count > 0 ? "No apps match your search."
                : _work is not null ? "Finding apps…"
                : _discovered ? "No apps found. Add an app folder." : "Select Refresh to look for apps.";
    }
    private void UpdateBars()
    { long max = _rows.Select(row => row.SizeBytes ?? 0).DefaultIfEmpty().Max(); foreach (var row in _rows) row.SetLargest(max); }
    private void Busy(bool busy)
    {
        RefreshButton.IsEnabled = !busy && !_settingsBusy;
        AddButton.IsEnabled = !busy && !_settingsBusy && _settingsWritable;
        RetryFoldersButton.IsEnabled = SavedFolderList.IsEnabled = !busy && !_settingsBusy;
        RemoveSavedFolderButton.IsEnabled = !busy && !_settingsBusy && SavedFolderList.SelectedItem is string && _settingsWritable;
        CancelButton.Visibility = busy && _work is not null ? Visibility.Visible : Visibility.Collapsed;
        WorkProgress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (AppList is not null) UpdateList(); }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    { DetailsPanel.DataContext = AppList.SelectedItem; DetailsPanel.Visibility = AppList.SelectedItem is AppRow ? Visibility.Visible : Visibility.Collapsed; }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose an app's install folder", Multiselect = false };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        string? path = AppDiscovery.NormalizeFolder(picker.FolderName);
        if (path is null) { StatusText.Text = "Choose a specific app folder, inside the drive or program folder."; return; }
        await AddFolderAsync(path);
    }
    internal Task AddFolderAsync(string path) => _settingsOperation = ChangeSavedFolderAsync(path, true);
    internal Task RemoveSavedFolderAsync(string path) => _settingsOperation = ChangeSavedFolderAsync(path, false);
    private AppFolderSettings FolderSettings
    {
        get
        {
            string path = Path.Join(_host.DataDirectory, "app-folders.json");
            if (_folderSettings is null || _folderSettingsPath != path)
            { _folderSettingsPath = path; _folderSettings = new(_host.Files, path); _addedFolders.Clear(); }
            return _folderSettings;
        }
    }
    private async Task<bool> ReloadSavedFoldersAsync(CancellationToken token)
    {
        var settings = FolderSettings;
        var state = await Task.Run(() =>
        {
            lock (settings) { bool loaded = settings.Reload(); return (Loaded: loaded, Folders: settings.Folders.ToArray(), settings.Error, settings.CanChange); }
        }, token);
        if (!_shutdown) ShowSavedFolders(state.Folders, state.Error, state.CanChange);
        return state.Loaded;
    }
    private void ShowSavedFolders(string[] folders, string? error, bool writable)
    {
        string? selected = SavedFolderList.SelectedItem as string;
        _settingsError = error;
        _settingsWritable = writable;
        _addedFolders.Clear(); _addedFolders.AddRange(folders);
        SavedFolderList.ItemsSource = _addedFolders.ToArray();
        SavedFolderList.SelectedItem = _addedFolders.FirstOrDefault(path => path == selected);
        SavedFoldersPanel.Visibility = _addedFolders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FoldersErrorText.Text = error ?? "";
        FoldersErrorSummaryText.Text = writable ? "Saved app folders were not changed. See details or try again."
            : "Saved app folders are unavailable. Existing shortcuts are retained; changes are paused. Restore app-folders.json or retry.";
        FoldersErrorPanel.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        Busy(_work is not null);
    }
    private async Task ChangeSavedFolderAsync(string path, bool add)
    {
        if (_shutdown || _settingsBusy || _work is not null) return;
        _settingsBusy = true; Busy(true);
        bool changed = false;
        try
        {
            var settings = FolderSettings;
            var state = await Task.Run(() =>
            {
                lock (settings) { bool saved = settings.Change(path, add); return (Saved: saved, Folders: settings.Folders.ToArray(), settings.Error, settings.CanChange); }
            }, _lifetime);
            changed = state.Saved;
            if (_shutdown) return;
            ShowSavedFolders(state.Folders, state.Error, state.CanChange);
            StatusText.Text = changed ? add ? "App folder saved. Files stay in place." : "Saved shortcut removed. The app and its files stay in place."
                : state.Error;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _settingsBusy = false; if (!_shutdown) Busy(_work is not null); }
        if (!changed || _shutdown) return;
        if (add) SetDrive(Path.GetPathRoot(path)!);
        await RefreshAsync(false);
    }
    private void SavedFolder_Changed(object sender, SelectionChangedEventArgs e)
    { if (RemoveSavedFolderButton is not null) RemoveSavedFolderButton.IsEnabled = _work is null && !_settingsBusy && SavedFolderList.SelectedItem is string && _settingsWritable; }
    private async void RemoveSavedFolder_Click(object sender, RoutedEventArgs e)
    { if (SavedFolderList.SelectedItem is string path) await RemoveSavedFolderAsync(path); }
    private async void RetryFolders_Click(object sender, RoutedEventArgs e) => await (_settingsOperation = RetrySavedFoldersAsync());
    private async Task RetrySavedFoldersAsync()
    {
        if (_shutdown || _work is not null || _settingsBusy) return;
        _settingsBusy = true; Busy(true);
        try
        {
            await ReloadSavedFoldersAsync(_lifetime);
            if (!_shutdown) StatusText.Text = _settingsError ?? "Saved app folders reloaded. Select Refresh to look for apps.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _settingsBusy = false; if (!_shutdown) Busy(_work is not null); }
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not AppRow { App.Path: { } path }) return;
        if (!_host.Files.DirectoryExists(path)) { StatusText.Text = "This folder is no longer available. Refresh apps."; return; }
        _host.RevealLocation(path);
    }
    internal async Task ShutdownAsync()
    { _shutdown = true; _revision++; _work?.Cancel(); await Task.WhenAll(_pending, _settingsOperation); _work = null; }
}
