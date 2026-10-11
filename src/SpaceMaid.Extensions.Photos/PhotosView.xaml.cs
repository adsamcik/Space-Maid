using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Photos;

public partial class PhotosView : UserControl
{
    private readonly IExtensionHost _host;
    private readonly PhotoCatalog _catalog;
    private readonly PhotosService _service;
    private readonly ThumbnailCache _thumbnails;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _operation, _pageRequests, _pageQuery;
    private PhotoMode _mode;
    private PhotoSort _sort = PhotoSort.Name;
    private bool _descending, _busy, _started, _suppressCollection;
    private int _page;
    private long _total, _pageGeneration, _collectionGeneration, _selectionGeneration;
    private readonly ObservableCollection<PhotoDisplayCollection> _collections = [];
    private readonly ObservableCollection<PhotoDisplayRow> _rows = [];
    private bool _applyingPage;
    private long _viewportInteraction;
    private long? _displayedCollection;
    private PhotoMode _displayedMode;
    private readonly HashSet<Task> _tasks = [];
    private readonly object _taskGate = new();
    private bool _shuttingDown;
    private bool _discovering, _liveRefresh;
    private bool _noticeVisible;
    private enum EmptyAction { Add, Browse, Analyze, Refresh, Retry }
    private EmptyAction _emptyAction;
    private long _operationGeneration;
    private PhotoCollection? Collection => (CollectionList.SelectedItem as PhotoDisplayCollection)?.Value;
    internal PhotosView(IExtensionHost host, PhotoCatalog catalog, CancellationToken lifetime)
    {
        _host = host; _catalog = catalog; _lifetime = lifetime;
        _service = new(host, catalog); _thumbnails = new(host);
        InitializeComponent();
        CollectionList.ItemsSource = _collections;
        FileList.ItemsSource = _rows;
        FileList.PreviewMouseWheel += (_, _) => _viewportInteraction++;
        FileList.PreviewMouseDown += (_, _) => _viewportInteraction++;
        FileList.PreviewKeyDown += (_, _) => _viewportInteraction++;
        FileList.PreviewTouchDown += (_, _) => _viewportInteraction++;
        FileList.Columns.First(column => column.SortMemberPath == "Name").SortDirection = ListSortDirection.Ascending;
        Loaded += View_Loaded;
        Unloaded += (_, _) => { if (!_shuttingDown) { _pageRequests?.Cancel(); _pageQuery?.Cancel(); } };
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
        var generation = ++_collectionGeneration;
        var collections = await Task.Run(_catalog.Collections, _lifetime);
        if (_shuttingDown || generation != _collectionGeneration) return;
        var wanted = select ?? Collection?.Id;
        _suppressCollection = true;
        try
        {
            var ids = collections.Select(x => x.Id).ToHashSet();
            for (var index = _collections.Count - 1; index >= 0; index--)
                if (!ids.Contains(_collections[index].Id)) _collections.RemoveAt(index);
            for (var index = 0; index < collections.Count; index++)
            {
                var value = collections[index];
                var item = _collections.FirstOrDefault(x => x.Id == value.Id);
                if (item is null) _collections.Insert(index, item = new(value));
                else { item.Update(value); if (_collections.IndexOf(item) != index) _collections.Move(_collections.IndexOf(item), index); }
            }
            var selected = _collections.FirstOrDefault(x => x.Id == wanted) ?? _collections.FirstOrDefault();
            if (!ReferenceEquals(CollectionList.SelectedItem, selected)) CollectionList.SelectedItem = selected;
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
            _pageGeneration++; _pageQuery?.Cancel(); _pageRequests?.Cancel(); _rows.Clear(); _total = 0; _displayedCollection = null;
            UpdateEmptyState(null, null); UpdateControls(); return;
        }
        var generation = ++_pageGeneration;
        var mode = _mode; var sort = _sort; var descending = _descending; var requestedPage = _page;
        if (_pageRequests is null || _pageRequests.IsCancellationRequested) ResetPreviewRequests();
        _pageQuery?.Cancel(); _pageQuery?.Dispose(); _pageQuery = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var token = _pageQuery.Token; var watch = Stopwatch.StartNew();
        if (_displayedCollection != collection.Id)
        {
            _rows.Clear();
            SetEmpty("Loading photos…", "", EmptyAction.Retry, "Try again");
            EmptyActionButton.Visibility = Visibility.Collapsed;
            FileList.Visibility = Visibility.Collapsed; EmptyPanel.Visibility = Visibility.Visible;
            UpdateCoverageNotice(collection, null);
        }
        try
        {
            var result = await Task.Run(() => _catalog.Page(collection.Id, mode, sort, descending, requestedPage), token);
            token.ThrowIfCancellationRequested();
            var lastJob = await Task.Run(() => _catalog.LastJob(collection.Id), token);
            if (generation != _pageGeneration || token.IsCancellationRequested || Collection?.Id != collection.Id) return;
            // A newly requested page can win the race to the catalog before
            // BeginDiscovery runs. Refresh has already revoked the UI's trust.
            if (_discovering) result = mode == PhotoMode.Duplicates ? new([], 0, 0) :
                result with { Rows = result.Rows.Select(row => WithoutAnalysis(row, mode)).ToArray() };
            _page = result.Page; _total = result.Total;
            GroupColumn.Visibility = mode == PhotoMode.Browse ? Visibility.Collapsed : Visibility.Visible;
            ApplyPage(result, collection, mode, generation);
            foreach (var column in FileList.Columns)
                column.SortDirection = column.SortMemberPath == sort.ToString() ? descending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
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
            UpdateControls();
            foreach (var row in _rows)
                if (FileList.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow) _ = PreviewAsync(row);
            _host.Log("photos-page", new { collection.Id, Mode = _mode.ToString(), Page = _page, Rows = result.Rows.Count, Total = result.Total, ElapsedMs = watch.ElapsedMilliseconds });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error); }
    }

    private void ApplyPage(PhotoPage page, PhotoCollection collection, PhotoMode mode, long generation)
    {
        var selected = FileList.SelectedItem as PhotoDisplayRow;
        var selectedChanged = _displayedMode != mode;
        var structureChanged = false;
        // Capture at publication, so scrolling or selection while the database
        // query runs is respected. Only this bounded page is reconciled.
        var scroll = FindVisualChild<ScrollViewer>(FileList);
        var vertical = scroll?.VerticalOffset ?? 0; var horizontal = scroll?.HorizontalOffset ?? 0;
        var anchor = scroll?.CanContentScroll == true && VirtualizingPanel.GetScrollUnit(FileList) == ScrollUnit.Item && _rows.Count > 0
            ? _rows[Math.Min((int)vertical, _rows.Count - 1)] : null;
        _applyingPage = true;
        try
        {
            var paths = page.Rows.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
            for (var index = _rows.Count - 1; index >= 0; index--)
                if (!paths.Contains(_rows[index].Row.Path)) { _rows.RemoveAt(index); structureChanged = true; }
            var existing = _rows.ToDictionary(x => x.Row.Path, StringComparer.Ordinal);
            for (var index = 0; index < page.Rows.Count; index++)
            {
                var value = page.Rows[index];
                if (!existing.TryGetValue(value.Path, out var display)) { _rows.Insert(index, new(value, collection.Root)); structureChanged = true; }
                else
                {
                    if (display.Update(value) && ReferenceEquals(display, selected)) selectedChanged = true;
                    var before = _rows.IndexOf(display);
                    if (before != index) { _rows.Move(before, index); structureChanged = true; }
                }
            }
            var retained = selected is not null && _rows.Contains(selected) ? selected : null;
            if (!ReferenceEquals(FileList.SelectedItem, retained)) FileList.SelectedItem = retained;
            selectedChanged |= !ReferenceEquals(selected, retained);
        }
        finally { _applyingPage = false; }
        _displayedCollection = collection.Id; _displayedMode = mode;
        // Stop queued work for an old page/order, retaining decoded images for
        // surviving rows. An unchanged page keeps its in-flight requests too.
        if (structureChanged) ResetPreviewRequests();
        if (selectedChanged) _ = Track(SelectionAsync());
        if (scroll is not null && structureChanged)
        {
            if (anchor is not null && _rows.Contains(anchor)) vertical = _rows.IndexOf(anchor) + vertical - Math.Floor(vertical);
            // Let WPF process the small collection delta before restoring its
            // viewport. A later query/user scroll wins over this restoration.
            var interaction = _viewportInteraction;
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (generation != _pageGeneration || interaction != _viewportInteraction) return;
                scroll.ScrollToVerticalOffset(vertical); scroll.ScrollToHorizontalOffset(horizontal);
            }, DispatcherPriority.Loaded);
        }
    }

    private void ResetPreviewRequests()
    {
        _pageRequests?.Cancel(); _pageRequests?.Dispose();
        _pageRequests = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        foreach (var row in _rows) row.CancelPreviewRequest();
    }
    private static PhotoRow WithoutAnalysis(PhotoRow row, PhotoMode mode) => row with
    {
        AnalysisCurrent = false, Hash = null, Verified = null, Format = null,
        Width = null, Height = null, Orientation = null, Captured = null,
        Status = row.AnalysisCurrent ? "Earlier observation · Refreshing metadata" : row.Status,
        GroupLabel = mode == PhotoMode.Families ? row.GroupLabel : null,
        GroupCount = mode == PhotoMode.Families ? row.GroupCount : 0
    };
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
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
            SetEmpty("Add your first photo folder", "", EmptyAction.Add, "Add folder");
        else if (_discovering)
            SetEmpty("Finding photos…", "", EmptyAction.Browse, "Browse photos");
        else if (_mode == PhotoMode.Families)
            SetEmpty("No related files found", "Matching RAW, image and sidecar filenames.", EmptyAction.Browse, "Browse photos");
        else if (_mode == PhotoMode.Duplicates && collection.Count > 0)
        {
            var analyzed = lastJob?.StartsWith("Analysis", StringComparison.Ordinal) == true && lastJob.Contains("Completed", StringComparison.Ordinal);
            SetEmpty("No verified duplicates", analyzed ? "" : "Analyze photos to compare file contents.",
                analyzed ? EmptyAction.Browse : EmptyAction.Analyze, analyzed ? "Browse photos" : "Analyze photos");
        }
        else SetEmpty("No photos found", "No supported photo filenames in this folder.", EmptyAction.Refresh, "Refresh folder");
        EmptyActionButton.Visibility = _discovering ? Visibility.Collapsed : Visibility.Visible;
    }
    private void SetEmpty(string title, string message, EmptyAction action, string label)
    {
        EmptyTitle.Text = title; EmptyText.Text = message; EmptyText.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _emptyAction = action; EmptyActionButton.Content = label;
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
        _busy = true; _discovering = discovering; _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _pageGeneration++; _pageQuery?.Cancel(); ResetPreviewRequests();
        if (discovering)
        {
            // Discovery revokes content authorization for the whole collection,
            // including locations not yet visited. Keep row/selection identity,
            // but never present the previous analysis as current evidence.
            foreach (var row in _rows) row.Update(WithoutAnalysis(row.Row, _mode) with { Status = "Earlier observation · Refreshing metadata" });
            _ = Track(SelectionAsync());
        }
        UpdateControls();
        SetNotice(phase); JobProgress.Visibility = Visibility.Visible;
        var refreshClock = Stopwatch.StartNew();
        var acceptProgress = true;
        var operationGeneration = ++_operationGeneration;
        var operationCollection = Collection?.Id;
        var operationName = Collection?.Name ?? "Photos";
        var progress = new Progress<ExtensionJobProgress>(value =>
        {
            if (_shuttingDown || !_busy || !acceptProgress || operationGeneration != _operationGeneration) return;
            StatusText.Text = $"{operationName} · {value.Phase} · {value.Completed:N0}" + (value.BytesRead > 0 ? $" · {PhotoFormatting.Bytes(value.BytesRead)} read" : "");
            StatusText.ToolTip = StatusText.Text + (value.Message is null ? "" : $" · {value.Message}");
            if (_discovering && !_liveRefresh && refreshClock.ElapsedMilliseconds >= 1000)
            { refreshClock.Restart(); _liveRefresh = true; _ = Track(RefreshCommittedPageAsync()); }
        });
        try
        {
            await work(progress, _operation.Token);
            acceptProgress = false;
            var completedJob = operationCollection is long id ? await Task.Run(() => _catalog.LastJob(id), _lifetime) : null;
            var needsAttention = completedJob?.Contains("NeedsAttention", StringComparison.Ordinal) == true;
            SetNotice((discovering ? "Discovery finished" : completedJob?.Contains("Completed", StringComparison.Ordinal) == true ? "Analysis complete" : "Analysis finished") + (needsAttention ? " · Needs attention" : ""), needsAttention);
            StatusText.ToolTip = completedJob ?? StatusText.Text;
        }
        catch (OperationCanceledException) { SetNotice("Interrupted · Saved results kept"); }
        catch (Exception error) { ShowFailure(error); }
        finally
        {
            acceptProgress = false; _discovering = false;
            try { if (!_lifetime.IsCancellationRequested) await LoadCollectionsAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { ShowFailure(error); }
            finally
            {
                _busy = false; _operation.Dispose(); _operation = null; JobProgress.Visibility = Visibility.Hidden; UpdateControls();
            }
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
        _page = 0; _total = 0; _rows.Clear(); _displayedCollection = null; ResetPreviewRequests(); UpdateControls();
        await LoadPageAsync();
    }
    private async void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || FileList is null) return;
        _mode = sender == DuplicatesTab ? PhotoMode.Duplicates : sender == FamiliesTab ? PhotoMode.Families : PhotoMode.Browse;
        _page = 0; ResetPreviewRequests(); await LoadPageAsync();
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
        var row = display.Row; var version = display.PreviewVersion;
        try
        {
            var result = await _thumbnails.GetAsync(row, token);
            if (token.IsCancellationRequested || version != display.PreviewVersion || !_rows.Contains(display)) return;
            display.TrySetPreview(result, version);
            if (result.Image is null && display.Row.AnalysisCurrent && display.Row.ContentAvailable) display.Requested = false;
            if (ReferenceEquals(FileList.SelectedItem, display)) UpdateSelectedPreview(display);
        }
        catch (OperationCanceledException) { if (version == display.PreviewVersion) display.Requested = false; }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && _rows.Contains(display)) display.TrySetPreview(new(null, "Preview unavailable"), version);
            _host.Log("photos-preview-failure", new { Error = error.GetType().Name });
        }
    }
    private async void File_Changed(object sender, SelectionChangedEventArgs e)
    { if (!_applyingPage) await Track(SelectionAsync()); }
    private async Task SelectionAsync()
    {
        try
        {
        if (_shuttingDown) return;
        var generation = ++_selectionGeneration;
        if (FileList.SelectedItem is not PhotoDisplayRow row)
        {
            SelectionPanel.Visibility = Visibility.Collapsed; SelectedPreview.Source = null; SelectedPreview.Visibility = Visibility.Collapsed;
            SelectionText.Text = SelectedPathText.Text = SelectedNameText.Text = SelectedSummaryText.Text = PreviewStatusText.Text = "";
            RevealButton.IsEnabled = false; RetainButton.Visibility = Visibility.Collapsed; return;
        }
        var snapshot = row.Row; var mode = _mode; var collectionId = Collection?.Id;
        SelectionPanel.Visibility = Visibility.Visible;
        SelectedNameText.Text = row.Row.Name; SelectedNameText.ToolTip = row.Row.Name;
        SelectedPathText.Text = row.Row.Path;
        SelectedSummaryText.Text = row.Row.SizeText + " · " + row.Row.TypeText + (row.Row.Width is int width && row.Row.Height is int height ? $"\n{width:N0} × {height:N0}" : "");
        RetainButton.Visibility = _mode == PhotoMode.Duplicates ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = row.Row.Detail + (row.Row.Hash is null ? "" : $"\nSHA-256 (default stream): {row.Row.Hash}") + (row.Row.GroupCount > 1 ? $"\n{row.Row.GroupCount:N0} distinct physical files in this group." : "");
        UpdateSelectedPreview(row); RevealButton.IsEnabled = true;
        await PreviewAsync(row);
        if (collectionId is long id && generation == _selectionGeneration && ReferenceEquals(FileList.SelectedItem, row) && ReferenceEquals(snapshot, row.Row))
        {
            var related = await Task.Run(() => _catalog.ReviewLocations(id, snapshot, mode), _lifetime);
            if (generation == _selectionGeneration && Collection?.Id == id && _mode == mode && ReferenceEquals(FileList.SelectedItem, row) && ReferenceEquals(snapshot, row.Row)) SelectionText.Text += related;
        }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error); }
    }
    private void UpdateSelectedPreview(PhotoDisplayRow row)
    {
        SelectedPreview.Source = row.Preview; SelectedPreview.Visibility = row.Preview is null ? Visibility.Hidden : Visibility.Visible;
        SelectedPreviewPlaceholder.Visibility = row.Preview is null ? Visibility.Visible : Visibility.Hidden;
        PreviewStatusText.Text = row.Preview is null ? row.PreviewStatus : row.Row.Role == "RAW source" ? "RAW preview · May differ from current edits" : "Preview";
        PreviewStatusText.ToolTip = row.PreviewStatus;
    }
    private void Retain_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not PhotoDisplayRow row || _mode != PhotoMode.Duplicates) return;
        SelectionText.Text = "Review preference: retain this physical copy and all its hard-link aliases.\n" + row.Row.Path + "\n\nOther locations remain untouched. Dependencies, alternate streams and links outside this collection still require review. Recovery is unknown.\n\n" + SelectionText.Text;
        FileDetails.IsExpanded = true;
        SetNotice("Marked to keep · No files changed");
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
        StatusBand.Visibility = _busy || _noticeVisible ? Visibility.Visible : hasCollection ? Visibility.Hidden : Visibility.Collapsed;
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
            SetEmpty("Couldn't load this view", "", EmptyAction.Retry, "Try again");
        _host.Log("photos-workflow-failure", new { Error = error.GetType().Name });
    }
    private void SetNotice(string message, bool visible = true)
    {
        StatusText.Text = message; StatusText.ToolTip = message; _noticeVisible = visible;
        StatusBand.Visibility = _busy || visible ? Visibility.Visible : Collection is not null ? Visibility.Hidden : Visibility.Collapsed;
    }
    private Task Track(Task task)
    {
        lock (_taskGate) _tasks.Add(task);
        _ = task.ContinueWith(_ => { lock (_taskGate) _tasks.Remove(task); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
    internal async Task ShutdownAsync()
    {
        _shuttingDown = true; _operation?.Cancel(); _pageRequests?.Cancel(); _pageQuery?.Cancel();
        Task[] tasks; lock (_taskGate) tasks = _tasks.ToArray();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { } catch (Exception) { /* Each view operation reports its own failure. */ }
        _pageRequests?.Dispose(); _pageRequests = null;
        _pageQuery?.Dispose(); _pageQuery = null;
    }
    private static string DisplayDate(string? date) => DateTime.TryParse(date, out var value) ? value.ToLocalTime().ToString("g") : "never";
}
