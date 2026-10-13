using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
    private readonly HashSet<Task> _tasks = [];
    private readonly object _taskGate = new();
    private bool _shuttingDown, _busy, _started;
    private bool _noticeVisible;
    private enum EmptyAction { Add, Browse, Analyze, Refresh, Retry }
    private EmptyAction _emptyAction;
    private PhotoCollection? Collection => (CollectionList.SelectedItem as PhotoDisplayCollection)?.Value;
    internal PhotosView(IExtensionHost host, PhotoCatalog catalog, CancellationToken lifetime)
    {
        _host = host; _catalog = catalog; _lifetime = lifetime;
        _service = new(host, catalog); _thumbnails = new(host);
        InitializeComponent();
        CollectionList.ItemsSource = _collections;
        FileList.ItemsSource = _rows;
        PhotoGallery.ItemsSource = _rows;
        TrackViewportInput(FileList);
        TrackViewportInput(PhotoGallery);
        PhotoGallery.IsVisibleChanged += (_, _) => { if (PhotoGallery.IsVisible) RequestDisplayedPreviews(); };
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
        UpdateDisplayControls();
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        if (collection is null)
            SetEmpty("No photo folders", "", EmptyAction.Add, "Add folder");
        else if (_discovering)
            SetEmpty("Finding photos…", "", EmptyAction.Browse, "Browse photos");
        else if (_mode == PhotoMode.Families)
            SetEmpty("No related files found", "", EmptyAction.Browse, "Browse photos");
        else if (_mode == PhotoMode.Duplicates && collection.Count > 0)
        {
            var analyzed = lastJob?.StartsWith("Analysis", StringComparison.Ordinal) == true && lastJob.Contains("Completed", StringComparison.Ordinal);
            SetEmpty("No verified duplicates", analyzed ? "" : "Analyze photos to compare file contents.",
                analyzed ? EmptyAction.Browse : EmptyAction.Analyze, analyzed ? "Browse photos" : "Analyze photos");
        }
        else SetEmpty("No photos found", "No supported photo filenames in this folder.", EmptyAction.Refresh, "Refresh");
        EmptyActionButton.Visibility = _discovering ? Visibility.Collapsed : Visibility.Visible;
    }
    private void SetEmpty(string title, string message, EmptyAction action, string label)
    {
        EmptyTitle.Text = title; EmptyText.Text = message; EmptyText.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _emptyAction = action; EmptyActionButton.Content = label;
        LibraryIllustration.Visibility = action == EmptyAction.Add ? Visibility.Visible : Visibility.Collapsed;
        EmptySymbol.Visibility = action == EmptyAction.Add ? Visibility.Collapsed : Visibility.Visible;
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
        CollectionHeader.Visibility = ModeBar.Visibility = DisplayBar.Visibility = CollectionDetails.Visibility = hasCollection ? Visibility.Visible : Visibility.Collapsed;
        CollectionPathText.Text = Collection?.Root ?? "";
        StatusBand.Visibility = _busy || _noticeVisible ? Visibility.Visible : hasCollection ? Visibility.Hidden : Visibility.Collapsed;
        PageFooter.Visibility = FileList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PageButtons.Visibility = _total > PhotoCatalog.PageSize ? Visibility.Visible : Visibility.Collapsed;
        EmptyActionButton.IsEnabled = !_busy;
        if (_discovering) { FreshnessText.Text = "Preliminary"; FreshnessText.Visibility = Visibility.Visible; }
        AddButton.IsEnabled = !_busy; RefreshButton.IsEnabled = AnalyzeButton.IsEnabled = !_busy && Collection is not null;
        CollectionList.IsEnabled = !_discovering; CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.IsEnabled = _page > 0; NextButton.IsEnabled = (_page + 1L) * PhotoCatalog.PageSize < _total;
        BrowseTab.IsEnabled = DuplicatesTab.IsEnabled = FamiliesTab.IsEnabled = true;
        FileList.IsEnabled = true;
        UpdateDisplayControls();
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
