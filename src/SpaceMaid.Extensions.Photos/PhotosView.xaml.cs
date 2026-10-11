using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Photos;

internal sealed class PhotoDisplayRow(PhotoRow row, string root) : INotifyPropertyChanged
{
    public PhotoRow Row { get; } = row;
    public string Folder { get; } = Path.GetRelativePath(root, Path.GetDirectoryName(row.Path)!) is { } parent && parent != "." ? parent : "";
    public string LocationHint => Row.Path + "\n" + Row.Status;
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
    private bool _noticeVisible;
    private enum EmptyAction { Add, Browse, Analyze, Refresh, Retry }
    private EmptyAction _emptyAction;
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
        if (_shuttingDown || !IsInitialized) return;
        if (Collection is not { } collection)
        {
            _pageGeneration++; _pageRequests?.Cancel(); FileList.ItemsSource = null; _total = 0;
            UpdateEmptyState(null, null); UpdateControls(); return;
        }
        var generation = ++_pageGeneration;
        var selectedPath = (FileList.SelectedItem as PhotoDisplayRow)?.Row.Path;
        _pageRequests?.Cancel(); _pageRequests?.Dispose(); _pageRequests = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var token = _pageRequests.Token; var watch = Stopwatch.StartNew();
        if (FileList.Items.Count == 0)
        {
            FileList.Visibility = Visibility.Collapsed; EmptyPanel.Visibility = Visibility.Visible;
            SetEmpty("Loading photos…", "Loading this collection's saved results.", EmptyAction.Retry, "Try again");
            EmptyActionButton.Visibility = Visibility.Collapsed;
            UpdateCoverageNotice(collection, null);
        }
        try
        {
            var result = await Task.Run(() => _catalog.Page(collection.Id, _mode, _sort, _descending, _page), token);
            var lastJob = await Task.Run(() => _catalog.LastJob(collection.Id), token);
            if (generation != _pageGeneration || token.IsCancellationRequested) return;
            _page = result.Page; _total = result.Total;
            GroupColumn.Visibility = _mode == PhotoMode.Browse ? Visibility.Collapsed : Visibility.Visible;
            FileList.ItemsSource = result.Rows.Select(x => new PhotoDisplayRow(x, collection.Root)).ToArray();
            // Replacing ItemsSource clears WPF's sort arrows even when our
            // catalog supplied the ordering. Restore them with the new page.
            foreach (var column in FileList.Columns)
                column.SortDirection = column.SortMemberPath == _sort.ToString() ? _descending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
            UpdateEmptyState(collection, lastJob);
            UpdateCoverageNotice(collection, lastJob);
            CoverageText.Text = $"{collection.Status} · Observed {DisplayDate(collection.Observed)}\n" + (_mode switch
            {
                PhotoMode.Duplicates => "Default-stream equality only · Companion files and alternate streams are not equivalent by implication.",
                PhotoMode.Families => "Possible basename relationships within the same folder · No equivalence or edit-fidelity claim.",
                _ => lastJob ?? "Filenames provide format hints. Analyze verifies supported metadata and content."
            }) + (collection.Coverage is null ? "" : "\n" + collection.Coverage);
            if (_discovering) CoverageText.Text = "Preliminary metadata · Unvisited locations retain earlier observations.\n" + CoverageText.Text;
            PageText.Text = $"{_page * 100 + 1:N0}–{_page * 100 + result.Rows.Count:N0} of {_total:N0} files";
            PageText.ToolTip = $"Sorted by {_sort} ({(_descending ? "descending" : "ascending")}) · 100 rows per page";
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
    private async void EmptyAction_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        switch (_emptyAction)
        {
            case EmptyAction.Add: Add_Click(sender, e); break;
            case EmptyAction.Browse: BrowseTab.IsChecked = true; break;
            case EmptyAction.Analyze: Analyze_Click(sender, e); break;
            case EmptyAction.Refresh: await DiscoverCurrentAsync(); break;
            case EmptyAction.Retry: SetNotice("", false); await LoadPageAsync(); break;
        }
    }

    private void UpdateEmptyState(PhotoCollection? collection, string? lastJob)
    {
        var empty = FileList.Items.Count == 0;
        FileList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        if (collection is null)
            SetEmpty("Add your first photo folder", "Browse photos, find duplicates and review related files. Your originals stay untouched.", EmptyAction.Add, "Add folder");
        else if (_discovering)
            SetEmpty("Finding photos…", "Files will appear as their metadata is discovered.", EmptyAction.Browse, "Browse photos");
        else if (_mode == PhotoMode.Families)
            SetEmpty("No related files found", "RAW, image and sidecar files with matching names appear together here.", EmptyAction.Browse, "Browse photos");
        else if (_mode == PhotoMode.Duplicates && collection.Count > 0)
        {
            var analyzed = lastJob?.StartsWith("Analysis", StringComparison.Ordinal) == true && lastJob.Contains("Completed", StringComparison.Ordinal);
            SetEmpty("No verified duplicates", analyzed ? "No exact duplicates were verified in this collection." : "Analyze this collection to compare file contents. Your originals stay untouched.",
                analyzed ? EmptyAction.Browse : EmptyAction.Analyze, analyzed ? "Browse photos" : "Analyze photos");
        }
        else SetEmpty("No photos found", "This folder has no supported photo filenames. Refresh to check for new files, or add another folder.", EmptyAction.Refresh, "Refresh folder");
        EmptyActionButton.Visibility = _discovering ? Visibility.Collapsed : Visibility.Visible;
    }
    private void SetEmpty(string title, string message, EmptyAction action, string label)
    {
        EmptyTitle.Text = title; EmptyText.Text = message; _emptyAction = action; EmptyActionButton.Content = label;
        EmptyActionButton.Visibility = Visibility.Visible;
    }
    private void UpdateCoverageNotice(PhotoCollection collection, string? lastJob)
    {
        FreshnessText.Text = _discovering ? "Preliminary" : collection.Status.StartsWith("Incomplete", StringComparison.Ordinal) ? "Incomplete" :
            collection.Status.Contains("Interrupted", StringComparison.OrdinalIgnoreCase) || lastJob?.Contains("Interrupted", StringComparison.Ordinal) == true ? "Interrupted" :
            collection.Status.Contains("failed", StringComparison.OrdinalIgnoreCase) || lastJob?.Contains("Failed", StringComparison.Ordinal) == true || lastJob?.Contains("NeedsAttention", StringComparison.Ordinal) == true ? "Needs attention" :
            collection.Status == "Not discovered" ? "Not discovered" : "";
        FreshnessText.Visibility = FreshnessText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        FreshnessText.ToolTip = collection.Status + (lastJob is null ? "" : "\n" + lastJob) + (collection.Coverage is null ? "" : "\n" + collection.Coverage);
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
        SetNotice(phase); JobProgress.Visibility = Visibility.Visible;
        var refreshClock = Stopwatch.StartNew();
        var operationGeneration = ++_operationGeneration;
        var operationCollection = Collection?.Id;
        var operationName = Collection?.Name ?? "Photos";
        var progress = new Progress<ExtensionJobProgress>(value =>
        {
            if (_shuttingDown || !_busy || operationGeneration != _operationGeneration) return;
            StatusText.Text = $"{operationName} · {value.Phase} · {value.Completed:N0}" + (value.BytesRead > 0 ? $" · {PhotoFormatting.Bytes(value.BytesRead)} read" : "") + (value.Message is null ? "" : $" · {value.Message}");
            if (_discovering && !_liveRefresh && refreshClock.ElapsedMilliseconds >= 1000)
            { refreshClock.Restart(); _liveRefresh = true; _ = Track(RefreshCommittedPageAsync()); }
        });
        try
        {
            await work(progress, _operation.Token);
            var completedJob = operationCollection is long id ? await Task.Run(() => _catalog.LastJob(id), _lifetime) : null;
            SetNotice((discovering ? "Discovery finished" : completedJob?.Contains("Completed", StringComparison.Ordinal) == true ? "Analysis complete" : "Analysis finished") + (completedJob is null ? "" : " · " + completedJob), completedJob?.Contains("NeedsAttention", StringComparison.Ordinal) == true);
        }
        catch (OperationCanceledException) { SetNotice("Interrupted · Saved results retained. Refresh or Analyze to check them again."); }
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
    private void Cancel_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); SetNotice("Cancelling · Finishing the current checkpoint"); }
    private async void Collection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCollection || !IsInitialized || _discovering) return;
        if (!_busy) SetNotice("", false);
        _page = 0; _total = 0; FileList.ItemsSource = null; UpdateControls();
        await LoadPageAsync();
    }
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
            if (ReferenceEquals(FileList.SelectedItem, display)) UpdateSelectedPreview(display);
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
        if (FileList.SelectedItem is not PhotoDisplayRow row)
        {
            SelectionPanel.Visibility = Visibility.Collapsed; SelectedPreview.Source = null; SelectedPreview.Visibility = Visibility.Collapsed;
            SelectionText.Text = SelectedPathText.Text = SelectedNameText.Text = SelectedSummaryText.Text = PreviewStatusText.Text = "";
            RevealButton.IsEnabled = false; RetainButton.Visibility = Visibility.Collapsed; return;
        }
        SelectionPanel.Visibility = Visibility.Visible;
        SelectedNameText.Text = row.Row.Name; SelectedNameText.ToolTip = row.Row.Name;
        SelectedPathText.Text = row.Row.Path;
        SelectedSummaryText.Text = row.Row.SizeText + " · " + row.Row.TypeText + (row.Row.Width is int width && row.Row.Height is int height ? $"\n{width:N0} × {height:N0}" : "");
        RetainButton.Visibility = _mode == PhotoMode.Duplicates ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = row.Row.Detail + (row.Row.Hash is null ? "" : $"\nSHA-256 (default stream): {row.Row.Hash}") + (row.Row.GroupCount > 1 ? $"\n{row.Row.GroupCount:N0} distinct physical files in this group." : "");
        UpdateSelectedPreview(row); RevealButton.IsEnabled = true;
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
    private void UpdateSelectedPreview(PhotoDisplayRow row)
    {
        SelectedPreview.Source = row.Preview; SelectedPreview.Visibility = row.Preview is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewStatusText.Text = row.Preview is null ? row.PreviewStatus : row.Row.Role == "RAW source" ? "RAW preview · May differ from current edits" : "Preview";
        PreviewStatusText.ToolTip = row.PreviewStatus;
    }
    private void Retain_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not PhotoDisplayRow row || _mode != PhotoMode.Duplicates) return;
        SelectionText.Text = "Review preference: retain this physical copy and all its hard-link aliases.\n" + row.Row.Path + "\n\nOther locations remain untouched. Dependencies, alternate streams and links outside this collection still require review. Recovery is unknown.\n\n" + SelectionText.Text;
        FileDetails.IsExpanded = true;
        SetNotice("Keep preference noted · No files changed");
    }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    { if (FileList.SelectedItem is PhotoDisplayRow row) { try { _host.RevealLocation(row.Row.Path); } catch (Exception error) { ShowFailure(error); } } }
    private void CloseSelection_Click(object sender, RoutedEventArgs e) { FileList.SelectedItem = null; FileList.Focus(); }
    private void Columns_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; } }
    private void Column_Changed(object sender, RoutedEventArgs e)
    {
        TypeColumn.Visibility = ShowTypeColumn.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        CapturedColumn.Visibility = ShowCapturedColumn.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        StatusColumn.Visibility = ShowStatusColumn.IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateControls()
    {
        if (!IsInitialized) return;
        var hasCollection = Collection is not null;
        CollectionHeader.Visibility = ModeBar.Visibility = CollectionDetails.Visibility = hasCollection ? Visibility.Visible : Visibility.Collapsed;
        CollectionPathText.Text = Collection?.Root ?? "";
        StatusBand.Visibility = _busy || _noticeVisible ? Visibility.Visible : Visibility.Collapsed;
        PageFooter.Visibility = FileList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PageButtons.Visibility = _total > 100 ? Visibility.Visible : Visibility.Collapsed;
        EmptyActionButton.IsEnabled = !_busy;
        if (_discovering) { FreshnessText.Text = "Preliminary"; FreshnessText.Visibility = Visibility.Visible; }
        AddButton.IsEnabled = !_busy; RefreshButton.IsEnabled = AnalyzeButton.IsEnabled = !_busy && Collection is not null;
        CollectionList.IsEnabled = !_discovering; CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.IsEnabled = _page > 0; NextButton.IsEnabled = (_page + 1L) * 100 < _total;
        BrowseTab.IsEnabled = DuplicatesTab.IsEnabled = FamiliesTab.IsEnabled = true;
        FileList.IsEnabled = true;
    }
    private void ShowFailure(Exception error)
    {
        if (_shuttingDown) return;
        SetNotice("Photos could not finish: " + error.Message);
        if (FileList.Items.Count == 0 && Collection is not null)
            SetEmpty("Couldn't load this view", "Try loading the saved results again. Your originals are unchanged.", EmptyAction.Retry, "Try again");
        _host.Log("photos-workflow-failure", new { Error = error.GetType().Name });
    }
    private void SetNotice(string message, bool visible = true)
    { StatusText.Text = message; _noticeVisible = visible; StatusBand.Visibility = _busy || visible ? Visibility.Visible : Visibility.Collapsed; }
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
