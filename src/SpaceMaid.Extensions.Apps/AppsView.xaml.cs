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

public partial class AppsView : UserControl, IDriveExtensionView
{
    private readonly IExtensionHost _host;
    private readonly CancellationToken _lifetime;
    private readonly ObservableCollection<AppRow> _rows = [];
    private readonly List<string> _addedFolders = [];
    private CancellationTokenSource? _work;
    private Task _pending = Task.CompletedTask;
    private string? _drive;
    private bool _discovered, _shutdown;
    private int _revision;
    internal Func<string, IReadOnlyList<string>, CancellationToken, AppDiscoveryResult> Discover { get; set; } = AppDiscovery.Discover;

    internal AppsView(IExtensionHost host, CancellationToken lifetime)
    {
        _host = host; _lifetime = lifetime;
        InitializeComponent();
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
    internal Task RefreshAsync(bool refreshSizes = true)
    {
        if (_shutdown || _drive is null) return Task.CompletedTask;
        _work?.Cancel();
        var previous = _pending;
        var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _work = work;
        int revision = ++_revision;
        string drive = _drive;
        string[] added = _addedFolders.ToArray();
        return _pending = RunAsync(previous, work, revision, drive, added, refreshSizes);
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
            EmptyText.Text = "No apps found. Add an app folder.";
            _host.Log("apps-discovery", new { Drive = drive, Apps = _rows.Count, Measured = measured, Milliseconds = timer.ElapsedMilliseconds, Warnings = discovered.Warnings });
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        { if (Current()) { StatusText.Text = $"Cancelled · {_rows.Count:N0} apps · {measured:N0} measured"; UpdateList(); } }
        catch (Exception error)
        { if (Current()) { StatusText.Text = "Couldn't find apps. Try Find apps again."; StatusText.ToolTip = error.Message; _discovered = false; } }
        finally
        {
            if (ReferenceEquals(_work, work)) { Busy(false); _work = null; }
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
        EmptyPanel.Visibility = AppList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Count > 0 && AppList.Items.Count == 0) EmptyText.Text = "No apps match your search.";
    }
    private void UpdateBars()
    { long max = _rows.Select(row => row.SizeBytes ?? 0).DefaultIfEmpty().Max(); foreach (var row in _rows) row.SetLargest(max); }
    private void Busy(bool busy)
    {
        RefreshButton.IsEnabled = AddButton.IsEnabled = !busy;
        RefreshButton.Content = "Refresh";
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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
        if (!_addedFolders.Contains(path, StringComparer.Ordinal)) _addedFolders.Add(path);
        SetDrive(Path.GetPathRoot(path)!);
        await RefreshAsync();
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not AppRow { App.Path: { } path }) return;
        if (!Directory.Exists(path)) { StatusText.Text = "This folder is no longer available. Refresh apps."; return; }
        _host.RevealLocation(path);
    }
    internal async Task ShutdownAsync()
    { _shutdown = true; _revision++; _work?.Cancel(); await _pending; _work = null; }
}
