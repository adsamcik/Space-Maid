using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Cloud;

public partial class CloudView : UserControl, IExtensionContextView
{
    private readonly IExtensionHost _host;
    private readonly ICloudStorageServices? _storage;
    private readonly CancellationToken _lifetime;
    private readonly List<CloudRoot> _saved = [];
    private IReadOnlyList<CloudRoot> _discovered = [];
    private IReadOnlyList<CloudEntryRow> _entries = [];
    private readonly Dictionary<string, (string? SelectedPath, double Offset)> _positions = new(StringComparer.OrdinalIgnoreCase);
    private SortDescription[] _sorting = [];
    private CancellationTokenSource? _work;
    private Task _pending = Task.CompletedTask;
    private string? _currentPath;
    private bool _hostBusy, _settingsLoaded;
    private string? _settingsError;
    private string SettingsPath => Path.Join(_host.DataDirectory, "folders.json");
    private CloudRoot? Root => RootList.SelectedItem as CloudRoot;
    private CloudEntryRow? Selected => FileList.SelectedItem as CloudEntryRow;

    public CloudView(IExtensionHost host, CancellationToken lifetime)
    {
        _host = host; _lifetime = lifetime;
        _storage = (host as ICloudStorageHost)?.CloudStorage;
        InitializeComponent();
        if (_storage is null) StatusText.Text = "Cloud storage services are unavailable. Update Space Maid to use this extension.";
    }

    public void SetContext(ExtensionViewContext context) { _hostBusy = context.IsBusy; UpdateActions(); }

    internal async Task FindAsync()
    {
        await RunAsync(async token =>
        {
            var result = await Task.Run(() => CloudDiscovery.Find(token), token);
            await LoadSavedAsync(token);
            _discovered = result.Roots;
            UpdateRoots();
            StatusText.Text = RootList.Items.Count == 0 ? "No connected folders found. Add a folder from your provider’s desktop app." : $"{RootList.Items.Count:N0} cloud folders · Select one to browse.";
            if (result.Warnings.Count > 0 || _settingsError is not null)
            {
                StatusText.Text += " Some folder locations could not be read; see Details or use Add folder.";
                DetailsText.Text = string.Join("\n", result.Warnings.Append(_settingsError).Where(w => w is not null));
            }
        });
    }

    internal Task BrowseAsync(string path) => RunAsync(async token =>
    {
        if (_storage is null) throw new InvalidOperationException("Update Space Maid to browse cloud folders.");
        if (Root is not { } root || !Within(path, root.Path)) throw new IOException("Choose a folder within the selected cloud drive.");
        SavePosition();
        if (_currentPath is null) { _currentPath = path; FolderPath.Text = path; }
        var listing = await _storage.ReadDirectoryAsync(path, token);
        token.ThrowIfCancellationRequested();
        _currentPath = listing.Path;
        FolderPath.Text = listing.Path;
        _entries = listing.Entries.Select(entry => new CloudEntryRow(entry)).ToArray();
        var view = CollectionViewSource.GetDefaultView(_entries);
        foreach (var sort in _sorting) view.SortDescriptions.Add(sort);
        FileList.ItemsSource = view;
        ApplyFilter();
        if (_positions.TryGetValue(listing.Path, out var position))
        {
            FileList.SelectedItem = _entries.FirstOrDefault(row => row.Entry.Path.Equals(position.SelectedPath, StringComparison.OrdinalIgnoreCase));
            FileList.UpdateLayout();
            FindScrollViewer(FileList)?.ScrollToVerticalOffset(position.Offset);
        }
        StatusText.Text = $"{listing.Entries.Count:N0} items" + (listing.Complete ? "" : " · Partial results; see Details");
        DetailsText.Text = "Local data is reported by Windows for each file; folder totals and provider caches are excluded. File size is not cloud quota usage.\n"
            + string.Join("\n", listing.Warnings);
        UpdateActions();
    });

    internal async Task AddRootAsync(string provider, string path)
    {
        bool added = false;
        await RunAsync(async token =>
        {
            await LoadSavedAsync(token);
            if (_settingsError is not null) throw new IOException(_settingsError);
            if (!CloudDiscovery.Providers.Contains(provider)) throw new ArgumentException("Choose a supported provider.");
            var root = new CloudRoot(provider, CloudDiscovery.Normalize(path), "Added folder");
            var saved = CloudDiscovery.Merge(_saved.Append(root));
            if (!saved.Any(value => value.Path.Equals(root.Path, StringComparison.OrdinalIgnoreCase))) throw new IOException("At most 64 added folders are supported.");
            await SaveAsync(saved, token);
            _saved.Clear(); _saved.AddRange(saved);
            UpdateRoots(root.Path);
            added = true;
        });
        if (added && Root is { } root) await BrowseAsync(root.Path);
    }

    private async Task LoadSavedAsync(CancellationToken token)
    {
        if (_settingsLoaded) return;
        try
        {
            var roots = await Task.Run(() => File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<CloudRoot[]>(CloudDiscovery.ReadSmallFile(SettingsPath)) ?? throw new JsonException("Invalid saved folders.") : [], token);
            if (roots.Length > 64 || roots.Any(root => root is null || root.Path is null || !CloudDiscovery.Providers.Contains(root.Provider)))
                throw new JsonException("Invalid saved folders.");
            foreach (var root in roots) _ = CloudDiscovery.Normalize(root.Path);
            _saved.AddRange(CloudDiscovery.Merge(roots));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { _settingsError = "Saved folders could not be read. Restore folders.json in the extension’s data folder before changing saved folders."; }
        _settingsLoaded = true;
    }

    private Task SaveAsync(IReadOnlyList<CloudRoot> roots, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_host.DataDirectory);
        string temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(roots));
        File.Move(temporary, SettingsPath, true);
    }, token);

    private void UpdateRoots(string? select = null)
    {
        SavePosition();
        string? previous = select ?? Root?.Path;
        RootList.ItemsSource = CloudDiscovery.Merge(_discovered.Concat(_saved));
        RootList.SelectedItem = RootList.Items.Cast<CloudRoot>().FirstOrDefault(root => root.Path.Equals(previous, StringComparison.OrdinalIgnoreCase));
        // No content enumeration until the user selects a folder or refreshes it.
        if (Root is null || _currentPath is { } current && !Within(current, Root.Path))
        { _currentPath = null; FolderPath.Text = ""; _entries = []; FileList.ItemsSource = null; ApplyFilter(); }
    }

    private void SavePosition()
    {
        if (FileList.ItemsSource is ICollectionView view) _sorting = view.SortDescriptions.ToArray();
        if (_currentPath is not { } current) return;
        if (_positions.Count >= 32 && !_positions.ContainsKey(current)) _positions.Remove(_positions.Keys.First());
        _positions[current] = (Selected?.Entry.Path, FindScrollViewer(FileList)?.VerticalOffset ?? 0);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, i)) is { } child) return child;
        return null;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_work is not null || _lifetime.IsCancellationRequested) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _work = work; UpdateActions();
        try { await action(work.Token); }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled. Accepted online-only requests may still be processing."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception)
        {
            StatusText.Text = "Could not finish. Refresh the folder or use the provider’s controls in Explorer.";
            DetailsText.Text = error.Message;
            _host.Log("cloud-operation-failed", new { Error = error.Message });
        }
        finally { _work = null; UpdateActions(); }
    }

    internal Task FreeSelectedAsync() => RunAsync(async token =>
    {
        if (_storage is null || Selected is not { Entry.CanRequestOnlineOnly: true } row) return;
        var result = await _storage.ReviewOnlineOnlyAsync(row.Entry.Path, token);
        StatusText.Text = result.Message;
        if (result.RequestsAccepted)
        {
            // Provider processing is asynchronous; old residency is no longer current.
            _entries = []; FileList.ItemsSource = null; ApplyFilter();
            EmptyText.Text = "Refresh to check the provider’s current local storage.";
        }
    });

    private void UpdateActions()
    {
        if (RootList is null) return;
        bool idle = _work is null;
        RootList.IsEnabled = FindButton.IsEnabled = AddButton.IsEnabled = idle;
        FileList.IsEnabled = idle;
        CancelButton.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = idle && _currentPath is not null && _storage is not null;
        UpButton.IsEnabled = idle && Root is { } root && _currentPath is { } current && !current.Equals(root.Path, StringComparison.OrdinalIgnoreCase);
        string? target = Selected?.Entry.Path ?? _currentPath;
        SelectedPath.Text = target ?? "";
        OpenFolderButton.IsEnabled = idle && Selected is { Entry.IsDirectory: true };
        ExplorerButton.IsEnabled = idle && target is not null;
        ExploreButton.IsEnabled = idle && !_hostBusy && target is not null && (_host as IExtensionNavigationHost)?.Navigation is not null;
        FreeButton.IsEnabled = idle && !_hostBusy && _storage is not null && Selected is { Entry.CanRequestOnlineOnly: true };
        ActionHint.Text = Selected is { Entry.CanRequestOnlineOnly: false }
            ? "Use the provider’s controls in Explorer for this item. Deleting synced files can also delete their cloud copies."
            : "Free local space keeps cloud copies. Downloading again needs internet.";
        RemoveButton.Visibility = Root is { } selectedRoot && _saved.Any(r => r.Path.Equals(selectedRoot.Path, StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = idle;
    }

    private void ApplyFilter()
    {
        if (SearchBox is null || FileList is null || EmptyText is null) return;
        string query = SearchBox.Text.Trim();
        if (FileList.ItemsSource is ICollectionView view) view.Filter = value => value is CloudEntryRow row && row.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        EmptyText.Visibility = FileList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _currentPath is null ? "OneDrive · Google Drive · Dropbox · Proton Drive" : _entries.Count == 0 ? "This folder is empty." : "No matching items.";
    }

    internal static bool Within(string path, string root)
    {
        path = CloudDiscovery.Normalize(path); root = CloudDiscovery.Normalize(root);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private async void Find_Click(object sender, RoutedEventArgs e) => await (_pending = FindAsync());
    private async void Root_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_work is not null) return;
        SavePosition();
        _currentPath = null; FolderPath.Text = ""; _entries = []; FileList.ItemsSource = null; ApplyFilter(); UpdateActions();
        if (Root is { } root) await (_pending = BrowseAsync(root.Path));
    }
    private void Add_Click(object sender, RoutedEventArgs e) { AddButton.ContextMenu.PlacementTarget = AddButton; AddButton.ContextMenu.IsOpen = true; }
    private async void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose your " + ((MenuItem)sender).Tag + " sync folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await (_pending = AddRootAsync((string)((MenuItem)sender).Tag, dialog.FolderName));
    }
    private async void Remove_Click(object sender, RoutedEventArgs e) => await (_pending = RunAsync(async token =>
    {
        if (Root is not { } root || _settingsError is not null) return;
        var kept = _saved.Where(r => !r.Path.Equals(root.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
        await SaveAsync(kept, token); _saved.Clear(); _saved.AddRange(kept); UpdateRoots();
        StatusText.Text = "Saved shortcut removed. Files and sync settings stay.";
    }));
    private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (_currentPath is { } path) await (_pending = BrowseAsync(path)); }
    private async void Up_Click(object sender, RoutedEventArgs e) { if (_currentPath is { } path && Path.GetDirectoryName(path) is { } parent) await (_pending = BrowseAsync(parent)); }
    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void File_Changed(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();
    private async Task OpenSelectedAsync() { if (Selected is { Entry.IsDirectory: true } row) await (_pending = BrowseAsync(row.Entry.Path)); }
    private async void File_DoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();
    private async void File_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedAsync(); } }
    private void Explorer_Click(object sender, RoutedEventArgs e) { if ((Selected?.Entry.Path ?? _currentPath) is { } path) _host.RevealLocation(path); }
    private async void Explore_Click(object sender, RoutedEventArgs e)
    {
        string? path = Selected is { Entry.IsDirectory: true } row ? row.Entry.Path : _currentPath;
        if (path is not null && (_host as IExtensionNavigationHost)?.Navigation is { } navigation && !await navigation.ExploreFolderAsync(path))
            StatusText.Text = "This folder could not be opened. Finish the current operation or use Explorer.";
    }
    private async void Free_Click(object sender, RoutedEventArgs e) => await (_pending = FreeSelectedAsync());
    internal async Task ShutdownAsync() { _work?.Cancel(); await _pending; }
}

internal sealed record CloudEntryRow(CloudDirectoryEntry Entry)
{
    public string Name => Entry.Name;
    public long LogicalBytes => Entry.LogicalBytes;
    public long? LocalBytes => Entry.LocalBytes;
    public string Availability => Entry.Availability;
    public string SizeText => Entry.IsDirectory ? "—" : Format(Entry.LogicalBytes);
    public string LocalText => Entry.LocalBytes is { } bytes ? Format(bytes) : "—";
    private static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
