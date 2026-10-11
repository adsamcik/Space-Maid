using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Photos;

internal sealed class PhotoDisplayRow(PhotoRow row) : INotifyPropertyChanged
{
    public PhotoRow Row { get; } = row;
    public BitmapSource? Preview { get; private set; }
    public string PreviewStatus { get; private set; } = row.ContentAvailable ? row.AnalysisCurrent ? "Waiting for preview" : "Analyze to enable previews" : "Offline/reparse preview unavailable";
    public string PreviewHint => Preview is not null ? "" : !Row.ContentAvailable ? "Offline" : !Row.AnalysisCurrent ? "Analyze" : PreviewStatus == "Waiting for preview" ? "Loading…" : "Unavailable";
    public bool Requested { get; set; }
    public void SetPreview(PhotoThumbnail result)
    {
        Preview = result.Image; PreviewStatus = result.Status;
        PropertyChanged?.Invoke(this, new(nameof(Preview))); PropertyChanged?.Invoke(this, new(nameof(PreviewStatus))); PropertyChanged?.Invoke(this, new(nameof(PreviewHint)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class PhotosView : UserControl
{
    private readonly IExtensionHost _host;
    private readonly PhotoCatalog _catalog;
    private readonly PhotosService _service;
    private readonly ThumbnailCache _thumbnails;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _operation, _pageRequests;
    private PhotoMode _mode;
    private PhotoSort _sort = PhotoSort.Name;
    private bool _descending, _busy, _started, _suppressCollection;
    private int _page;
    private long _total, _pageGeneration;
    private readonly HashSet<Task> _tasks = [];
    private readonly object _taskGate = new();
    private bool _shuttingDown;
    private bool _discovering, _liveRefresh;
    private long _operationGeneration;
    private PhotoCollection? Collection => CollectionList.SelectedItem as PhotoCollection;
    internal PhotosView(IExtensionHost host, PhotoCatalog catalog, CancellationToken lifetime)
    {
        _host = host; _catalog = catalog; _lifetime = lifetime;
        _service = new(host, catalog); _thumbnails = new(host);
        InitializeComponent();
        FileList.Columns.First(column => column.SortMemberPath == "Name").SortDirection = ListSortDirection.Ascending;
        Loaded += View_Loaded;
        Unloaded += (_, _) => { if (!_shuttingDown) _pageRequests?.Cancel(); };
    }
    private async void View_Loaded(object sender, RoutedEventArgs e)
    { await Track(InitializeViewAsync()); }
    private async Task InitializeViewAsync()
    {
        if (_started) { await LoadPageAsync(); return; }
        _started = true;
        try
        {
            await LoadCollectionsAsync();
            var version = await _host.Tools.ExifToolVersionAsync(_lifetime);
            ToolText.Text = version is null ? "ExifTool unavailable · Analyze can compare default streams; rich metadata remains unknown." : $"ExifTool {version} · Catalog and previews stay on this PC";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error); }
    }
    private Task LoadCollectionsAsync(long? select = null) => Track(LoadCollectionsCoreAsync(select));
    private async Task LoadCollectionsCoreAsync(long? select)
    {
        var wanted = select ?? Collection?.Id;
        var collections = await Task.Run(_catalog.Collections, _lifetime);
        _suppressCollection = true;
        try
        {
            CollectionList.ItemsSource = collections;
            CollectionList.SelectedItem = collections.FirstOrDefault(x => x.Id == wanted) ?? collections.FirstOrDefault();
        }
        finally { _suppressCollection = false; }
        UpdateControls();
        await LoadPageAsync();
    }
    private Task LoadPageAsync() => Track(LoadPageCoreAsync());
    private async Task LoadPageCoreAsync()
    {
        if (_shuttingDown || !IsInitialized || Collection is not { } collection) return;
        var generation = ++_pageGeneration;
        var selectedPath = (FileList.SelectedItem as PhotoDisplayRow)?.Row.Path;
        _pageRequests?.Cancel(); _pageRequests?.Dispose(); _pageRequests = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var token = _pageRequests.Token; var watch = Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(() => _catalog.Page(collection.Id, _mode, _sort, _descending, _page), token);
            var lastJob = await Task.Run(() => _catalog.LastJob(collection.Id), token);
            if (generation != _pageGeneration || token.IsCancellationRequested) return;
            _page = result.Page; _total = result.Total;
            GroupColumn.Visibility = _mode == PhotoMode.Browse ? Visibility.Collapsed : Visibility.Visible;
            SelectionText.Text = ""; SelectedPreview.Source = null; PreviewStatusText.Text = "Select a file to inspect its evidence.";
            FileList.ItemsSource = result.Rows.Select(x => new PhotoDisplayRow(x)).ToArray();
            EmptyPanel.Visibility = result.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = _mode switch
            {
                PhotoMode.Duplicates => "No verified exact duplicates to review. Analyze reads full default streams; hard-link aliases count as one physical file.",
                PhotoMode.Families => "No possible RAW/image/sidecar families found in this collection.",
                _ => "No supported photo filenames found. Refresh observes current metadata; Analyze enables content inspection and previews."
            };
            CoverageText.Text = $"{collection.Status} · Observed {DisplayDate(collection.Observed)}\n" + (_mode switch
            {
                PhotoMode.Duplicates => "Default-stream equality only · Companion files and alternate streams are not equivalent by implication.",
                PhotoMode.Families => "Possible basename relationships within the same folder · No equivalence or edit-fidelity claim.",
                _ => lastJob ?? "Filenames provide format hints. Analyze verifies supported metadata and content."
            }) + (collection.Coverage is null ? "" : "\n" + collection.Coverage);
            if (_discovering) CoverageText.Text = "Preliminary metadata · Unvisited locations retain earlier observations.\n" + CoverageText.Text;
            PageText.Text = $"Page {_page + 1:N0} of {Math.Max(1, (_total + 99) / 100):N0} · {_total:N0} locations";
            FileList.SelectedItem = ((PhotoDisplayRow[])FileList.ItemsSource).FirstOrDefault(x => x.Row.Path == selectedPath);
            UpdateControls();
            _host.Log("photos-page", new { collection.Id, Mode = _mode.ToString(), Page = _page, Rows = result.Rows.Count, Total = result.Total, ElapsedMs = watch.ElapsedMilliseconds });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error); }
    }
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Add photo collection", Multiselect = false };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        await Track(AddFolderAsync(picker.FolderName));
    }
    private async Task AddFolderAsync(string folder)
    {
        try
        {
            var id = await Task.Run(() => _catalog.AddCollection(folder), _lifetime);
            await LoadCollectionsAsync(id); await DiscoverCurrentAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await DiscoverCurrentAsync();
    private Task DiscoverCurrentAsync()
    {
        if (Collection is not { } collection || _busy) return Task.CompletedTask;
        return RunAsync($"Discovering {collection.Name}", (progress, token) => _service.DiscoverAsync(collection, progress, token), true);
    }
    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (Collection is not { } collection || _busy) return;
        await RunAsync($"Analyzing {collection.Name}", (progress, token) => _service.AnalyzeAsync(collection, progress, token));
    }
    private Task RunAsync(string phase, Func<IProgress<ExtensionJobProgress>, CancellationToken, Task> work, bool discovering = false) => Track(RunCoreAsync(phase, work, discovering));
    private async Task RunCoreAsync(string phase, Func<IProgress<ExtensionJobProgress>, CancellationToken, Task> work, bool discovering)
    {
        if (_busy) return;
        _busy = true; _discovering = discovering; _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _pageRequests?.Cancel(); UpdateControls();
        StatusText.Text = phase; JobProgress.Visibility = Visibility.Visible;
        var refreshClock = Stopwatch.StartNew();
        var operationGeneration = ++_operationGeneration;
        var operationCollection = Collection?.Id;
        var progress = new Progress<ExtensionJobProgress>(value =>
        {
            if (_shuttingDown || !_busy || operationGeneration != _operationGeneration) return;
            StatusText.Text = $"{phase} · {value.Phase} · {value.Completed:N0}" + (value.BytesRead > 0 ? $" · {PhotoFormatting.Bytes(value.BytesRead)} read" : "") + (value.Message is null ? "" : $" · {value.Message}");
            if (_discovering && !_liveRefresh && refreshClock.ElapsedMilliseconds >= 1000)
            { refreshClock.Restart(); _liveRefresh = true; _ = Track(RefreshCommittedPageAsync()); }
        });
        try
        {
            await work(progress, _operation.Token);
            var completedJob = operationCollection is long id ? await Task.Run(() => _catalog.LastJob(id), _lifetime) : null;
            StatusText.Text = (discovering ? "Discovery finished" : completedJob?.Contains("Completed", StringComparison.Ordinal) == true ? "Analysis complete" : "Analysis finished") + (completedJob is null ? "" : " · " + completedJob);
        }
        catch (OperationCanceledException) { StatusText.Text = "Interrupted · Committed evidence retained. Refresh or Analyze validates inputs before continuing."; }
        catch (Exception error) { ShowFailure(error); }
        finally
        {
            _busy = false; _discovering = false; _operation.Dispose(); _operation = null; JobProgress.Visibility = Visibility.Collapsed; UpdateControls();
            if (!_lifetime.IsCancellationRequested) await LoadCollectionsAsync();
        }
    }
    private async Task RefreshCommittedPageAsync()
    {
        try { await LoadCollectionsAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error); }
        finally { _liveRefresh = false; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); StatusText.Text = "Cancelling · Finishing the current checkpoint"; }
    private async void Collection_Changed(object sender, SelectionChangedEventArgs e)
    { if (_suppressCollection || !IsInitialized || _discovering) return; _page = 0; await LoadPageAsync(); UpdateControls(); }
    private async void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || FileList is null) return;
        _mode = sender == DuplicatesTab ? PhotoMode.Duplicates : sender == FamiliesTab ? PhotoMode.Families : PhotoMode.Browse;
        _page = 0; await LoadPageAsync();
    }
    private async void Files_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (!Enum.TryParse<PhotoSort>(e.Column.SortMemberPath, out var sort)) return;
        _descending = _sort == sort ? !_descending : sort == PhotoSort.Size; _sort = sort; _page = 0;
        foreach (var column in FileList.Columns) column.SortDirection = null;
        e.Column.SortDirection = _descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        await LoadPageAsync();
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) { _page = Math.Max(0, _page - 1); await LoadPageAsync(); }
    private async void Next_Click(object sender, RoutedEventArgs e) { if ((_page + 1L) * 100 < _total) _page++; await LoadPageAsync(); }
    private async void File_LoadingRow(object? sender, DataGridRowEventArgs e)
    { if (e.Row.Item is PhotoDisplayRow row) await PreviewAsync(row); }
    private Task PreviewAsync(PhotoDisplayRow display) => Track(PreviewCoreAsync(display));
    private async Task PreviewCoreAsync(PhotoDisplayRow display)
    {
        if (display.Requested || _pageRequests is null || _discovering) return;
        display.Requested = true; var token = _pageRequests.Token;
        try
        {
            var result = await _thumbnails.GetAsync(display.Row, token);
            if (token.IsCancellationRequested) return;
            display.SetPreview(result);
            if (result.Image is null && display.Row.AnalysisCurrent && display.Row.ContentAvailable) display.Requested = false;
            if (ReferenceEquals(FileList.SelectedItem, display)) { SelectedPreview.Source = result.Image; PreviewStatusText.Text = result.Status; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { display.SetPreview(new(null, "Preview unavailable")); _host.Log("photos-preview-failure", new { Error = error.GetType().Name }); }
    }
    private async void File_Changed(object sender, SelectionChangedEventArgs e)
    { await Track(SelectionAsync()); }
    private async Task SelectionAsync()
    {
        try
        {
        if (_shuttingDown) return;
        if (FileList.SelectedItem is not PhotoDisplayRow row) { RevealButton.IsEnabled = false; RetainButton.Visibility = Visibility.Collapsed; return; }
        RetainButton.Visibility = _mode == PhotoMode.Duplicates ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = row.Row.Detail + (row.Row.Hash is null ? "" : $"\nSHA-256 (default stream): {row.Row.Hash}") + (row.Row.GroupCount > 1 ? $"\n{row.Row.GroupCount:N0} distinct physical files in this group." : "");
        SelectedPreview.Source = row.Preview; PreviewStatusText.Text = row.PreviewStatus; RevealButton.IsEnabled = true;
        await PreviewAsync(row);
        if (Collection is { } collection)
        {
            var mode = _mode;
            var related = await Task.Run(() => _catalog.ReviewLocations(collection.Id, row.Row, mode), _lifetime);
            if (ReferenceEquals(FileList.SelectedItem, row)) SelectionText.Text += related;
        }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error); }
    }
    private void Retain_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not PhotoDisplayRow row || _mode != PhotoMode.Duplicates) return;
        SelectionText.Text = "Review preference: retain this physical copy and all its hard-link aliases.\n" + row.Row.Path + "\n\nOther locations remain untouched. Dependencies, alternate streams and links outside this collection still require review. Recovery is unknown.\n\n" + SelectionText.Text;
        StatusText.Text = "Cleanup preview only · No files changed";
    }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    { if (FileList.SelectedItem is PhotoDisplayRow row) { try { _host.RevealLocation(row.Row.Path); } catch (Exception error) { ShowFailure(error); } } }
    private void UpdateControls()
    {
        if (!IsInitialized) return;
        AddButton.IsEnabled = !_busy; RefreshButton.IsEnabled = AnalyzeButton.IsEnabled = !_busy && Collection is not null;
        CollectionList.IsEnabled = !_discovering; CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.IsEnabled = _page > 0; NextButton.IsEnabled = (_page + 1L) * 100 < _total;
        BrowseTab.IsEnabled = DuplicatesTab.IsEnabled = FamiliesTab.IsEnabled = true;
        FileList.IsEnabled = true;
    }
    private void ShowFailure(Exception error)
    {
        if (_shuttingDown) return;
        StatusText.Text = "Photos could not finish: " + error.Message;
        _host.Log("photos-workflow-failure", new { Error = error.GetType().Name });
    }
    private Task Track(Task task)
    {
        lock (_taskGate) _tasks.Add(task);
        _ = task.ContinueWith(_ => { lock (_taskGate) _tasks.Remove(task); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
    internal async Task ShutdownAsync()
    {
        _shuttingDown = true; _operation?.Cancel(); _pageRequests?.Cancel();
        Task[] tasks; lock (_taskGate) tasks = _tasks.ToArray();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { } catch (Exception) { /* Each view operation reports its own failure. */ }
        _pageRequests?.Dispose(); _pageRequests = null;
    }
    private static string DisplayDate(string? date) => DateTime.TryParse(date, out var value) ? value.ToLocalTime().ToString("g") : "never";
}
