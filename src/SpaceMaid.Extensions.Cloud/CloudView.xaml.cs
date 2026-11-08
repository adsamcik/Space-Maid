using SpaceMaid.Core.IO;
using System.ComponentModel;
using System.Diagnostics;
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
    private readonly ICloudFolderUsageServices? _usage;
    private readonly CancellationToken _lifetime;
    private readonly List<CloudRoot> _saved = [];
    private IReadOnlyList<CloudRoot> _discovered = [];
    private IReadOnlyList<CloudEntryRow> _entries = [];
    private readonly Dictionary<string, (string? SelectedPath, double Offset)> _positions = new(StringComparer.OrdinalIgnoreCase);
    private SortDescription[] _sorting = [];
    private CancellationTokenSource? _work;
    private Task _pending = Task.CompletedTask;
    private string? _currentPath;
    private bool _hostBusy, _settingsLoaded, _reviewingCloudSpace;
    private CloudDirectoryListing? _listing;
    private string? _settingsError;
    private string SettingsPath => Path.Join(_host.DataDirectory, "folders.json");
    private CloudRoot? Root => RootList.SelectedItem as CloudRoot;
    private CloudEntryRow? Selected => FileList.SelectedItem as CloudEntryRow;

    public CloudView(IExtensionHost host, CancellationToken lifetime)
    {
        _host = host; _lifetime = lifetime;
        _storage = (host as ICloudStorageHost)?.CloudStorage;
        _usage = _storage as ICloudFolderUsageServices;
        InitializeComponent();
        MeasureButton.Visibility = CloudCleanupButton.Visibility = _usage is null ? Visibility.Collapsed : Visibility.Visible;
        if (_storage is null) StatusText.Text = "Cloud storage services are unavailable. Update Space Maid to use this extension.";
    }

    public void SetContext(ExtensionViewContext context) { _hostBusy = context.IsBusy; UpdateActions(); }

    internal async Task FindAsync()
    {
        await RunAsync(async token =>
        {
            var result = await Task.Run(() => CloudDiscovery.Find(token, _host.Files), token);
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
        _listing = listing;
        _reviewingCloudSpace = false;
        LocalColumn.Header = "Local data";
        SetRows(listing.Entries.Select(entry => new CloudEntryRow(entry)).ToArray(), listing.Path);
        StatusText.Text = $"{listing.Entries.Count:N0} items" + (listing.Complete ? "" : " · Partial results; see Details");
        DetailsText.Text = "Local data shows the Windows Cloud Files residency of each file. Measure folders to see recursive totals and reported disk allocation.\n"
            + string.Join("\n", listing.Warnings);
        UpdateActions();
    });

    internal Task MeasureAsync() => MeasureFolderAsync(reviewCloudSpace: false);

    private Task MeasureFolderAsync(bool reviewCloudSpace) => RunAsync(async token =>
    {
        if (_usage is null || _listing is not { } listing || Root is not { } root || !Within(listing.Path, root.Path)) return;
        string path = listing.Path;
        SavePosition();
        StatusText.Text = "Measuring folder sizes…";
        var usage = await _usage.MeasureFolderUsageAsync(path, token);
        token.ThrowIfCancellationRequested();
        _reviewingCloudSpace = reviewCloudSpace;
        LocalColumn.Header = "Allocated";
        SetRows(listing.Entries.Select(entry => new CloudEntryRow(entry, true, usage.Entries.GetValueOrDefault(entry.Path))).ToArray(), path);
        var total = usage.Total;
        bool itemsChanged = listing.Complete && total.Complete && (usage.Entries.Count != listing.Entries.Count
            || listing.Entries.Any(entry => !usage.Entries.ContainsKey(entry.Path)));
        StatusText.Text = (reviewCloudSpace ? "Cloud cleanup review · " : "") + $"{CloudEntryRow.Format(total.LogicalBytes, total.Complete)} total size · "
            + (total.AllocatedBytes is { } bytes ? $"{CloudEntryRow.Format(bytes, total.Complete)} allocated" : "Allocation unavailable")
            + (total.Complete ? "" : " · Partial measurement")
            + (!listing.Complete ? " · List partial" : itemsChanged ? " · Refresh to see current items" : "");
        DetailsText.Text = "Size includes online-only files visible to Windows. Allocation is reported by the filesystem and excludes provider caches. "
            + "Providers can fetch folder listings during measurement. Use the provider’s app for account-wide usage.\n"
            + (!listing.Complete ? "The total can include items beyond the displayed list.\n" : "")
            + string.Join("\n", usage.Warnings.Concat(listing.Warnings).Distinct());
    }, "Measurement cancelled.");

    private void SetRows(IReadOnlyList<CloudEntryRow> rows, string path)
    {
        _entries = rows;
        var view = CollectionViewSource.GetDefaultView(_entries);
        SortDescription[] sorting = _reviewingCloudSpace
            ? [new(nameof(CloudEntryRow.LogicalBytes), ListSortDirection.Descending)] : _sorting;
        FileList.ItemsSource = view;
        // DataGrid can clear the sort descriptions when its source changes.
        foreach (var sort in sorting) view.SortDescriptions.Add(sort);
        foreach (var column in FileList.Columns)
            column.SortDirection = sorting.Where(sort => sort.PropertyName == column.SortMemberPath).Select(sort => (ListSortDirection?)sort.Direction).FirstOrDefault();
        ApplyFilter();
        if (_positions.TryGetValue(path, out var position))
        {
            FileList.SelectedItem = _entries.FirstOrDefault(row => row.Entry.Path.Equals(position.SelectedPath, StringComparison.OrdinalIgnoreCase));
            FileList.UpdateLayout();
            FindScrollViewer(FileList)?.ScrollToVerticalOffset(_reviewingCloudSpace ? 0 : position.Offset);
        }
    }

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
            var roots = await Task.Run(() => _host.Files.FileExists(SettingsPath)
                ? JsonSerializer.Deserialize<CloudRoot[]>(CloudDiscovery.ReadSmallFile(SettingsPath, _host.Files)) ?? throw new JsonException("Invalid saved folders.") : [], token);
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
        _host.Files.CreateDirectory(_host.DataDirectory);
        string temporary = SettingsPath + ".tmp";
        _host.Files.WriteAllText(temporary, JsonSerializer.Serialize(roots));
        _host.Files.MoveFile(temporary, SettingsPath, true);
    }, token);

    private void UpdateRoots(string? select = null)
    {
        SavePosition();
        string? previous = select ?? Root?.Path;
        RootList.ItemsSource = CloudDiscovery.Merge(_discovered.Concat(_saved));
        RootList.SelectedItem = RootList.Items.Cast<CloudRoot>().FirstOrDefault(root => root.Path.Equals(previous, StringComparison.OrdinalIgnoreCase));
        // No content enumeration until the user selects a folder or refreshes it.
        if (Root is null || _currentPath is { } current && !Within(current, Root.Path))
        { _currentPath = null; FolderPath.Text = ""; _listing = null; _reviewingCloudSpace = false; _entries = []; FileList.ItemsSource = null; ApplyFilter(); }
    }

    private void SavePosition()
    {
        if (!_reviewingCloudSpace && FileList.ItemsSource is ICollectionView view) _sorting = view.SortDescriptions.ToArray();
        if (_currentPath is not { } current) return;
        if (_positions.Count >= 32 && !_positions.ContainsKey(current)) _positions.Remove(_positions.Keys.First());
        double offset = _reviewingCloudSpace ? _positions.GetValueOrDefault(current).Offset : FindScrollViewer(FileList)?.VerticalOffset ?? 0;
        _positions[current] = (Selected?.Entry.Path, offset);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, i)) is { } child) return child;
        return null;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, string cancelled = "Cancelled. Accepted online-only requests may still be processing.")
    {
        if (_work is not null || _lifetime.IsCancellationRequested) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _work = work; UpdateActions();
        try { await action(work.Token); }
        catch (OperationCanceledException) { StatusText.Text = cancelled; }
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
            _listing = null; _reviewingCloudSpace = false; _entries = []; FileList.ItemsSource = null; ApplyFilter();
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
        MeasureButton.IsEnabled = CloudCleanupButton.IsEnabled = idle && _listing is not null && _usage is not null;
        ProviderButton.Visibility = _reviewingCloudSpace ? Visibility.Visible : Visibility.Collapsed;
        ProviderButton.IsEnabled = idle && _reviewingCloudSpace && Root is not null;
        ProviderButton.Content = Root is { } providerRoot ? $"Open {providerRoot.Provider}" : "Open provider website";
        UpButton.IsEnabled = idle && Root is { } root && _currentPath is { } current && !current.Equals(root.Path, StringComparison.OrdinalIgnoreCase);
        string? target = Selected?.Entry.Path ?? _currentPath;
        SelectedPath.Text = target ?? "";
        OpenFolderButton.IsEnabled = idle && Selected is { Entry.IsDirectory: true };
        ExplorerButton.IsEnabled = idle && target is not null;
        ExploreButton.IsEnabled = idle && !_hostBusy && target is not null && (_host as IExtensionNavigationHost)?.Navigation is not null;
        FreeButton.IsEnabled = idle && !_hostBusy && _storage is not null && Selected is { Entry.CanRequestOnlineOnly: true };
        ActionHint.Text = _reviewingCloudSpace
            ? "Sizes cover the sync folder. Check your account and quota in the provider’s website. Deletions there can sync to other devices."
            : Selected is { Entry.CanRequestOnlineOnly: false }
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
        _currentPath = null; FolderPath.Text = ""; _listing = null; _reviewingCloudSpace = false; _entries = []; FileList.ItemsSource = null; ApplyFilter(); UpdateActions();
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
    private async void Measure_Click(object sender, RoutedEventArgs e) => await (_pending = MeasureAsync());
    private async void CloudCleanup_Click(object sender, RoutedEventArgs e) => await (_pending = MeasureFolderAsync(reviewCloudSpace: true));
    private void Provider_Click(object sender, RoutedEventArgs e)
    {
        if (!ProviderButton.IsEnabled) return;
        if (Root?.Provider == "OneDrive")
        {
            var menu = (ContextMenu)Resources["OneDriveWebsiteMenu"];
            menu.PlacementTarget = ProviderButton;
            menu.IsOpen = true;
        }
        else OpenProvider(workOrSchool: false);
    }
    private void OneDrivePersonal_Click(object sender, RoutedEventArgs e) => OpenProvider(workOrSchool: false);
    private void OneDriveWork_Click(object sender, RoutedEventArgs e) => OpenProvider(workOrSchool: true);
    private void OpenProvider(bool workOrSchool)
    {
        if (!ProviderButton.IsEnabled || Root is not { } root || CloudDiscovery.StorageUrl(root.Provider, workOrSchool) is not { } url) return;
        try { using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            StatusText.Text = $"Could not open {root.Provider}. Open its website in your browser.";
            _host.Log("cloud-website-open-failed", new { root.Provider, Error = error.Message });
        }
    }
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

internal sealed record CloudEntryRow(CloudDirectoryEntry Entry, bool Measured = false, CloudUsageTotals? Usage = null)
{
    public string Name => Entry.Name;
    public long? LogicalBytes => Measured ? Usage?.LogicalBytes : Entry.IsDirectory ? null : Entry.LogicalBytes;
    public long? LocalBytes => Measured ? Usage?.AllocatedBytes : Entry.LocalBytes;
    public string Availability => Entry.Availability;
    public string SizeText => LogicalBytes is { } bytes ? Format(bytes, Usage?.Complete ?? true) : "—";
    public string LocalText => LocalBytes is { } bytes ? Format(bytes, Usage?.Complete ?? true) : "—";
    internal static string Format(long bytes, bool complete)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        if (!complete && unit > 0) value = Math.Floor(value * 10) / 10;
        return (complete ? "" : "≥ ") + $"{value:0.#} {units[unit]}";
    }
}
