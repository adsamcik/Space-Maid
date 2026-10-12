using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SpaceMaid.Extensions.Photos;

public partial class PhotosView
{
    private CancellationTokenSource? _pageQuery;
    private PhotoMode _mode, _displayedMode;
    private PhotoSort _sort = PhotoSort.Name;
    private bool _descending, _suppressCollection, _applyingPage, _suppressSort;
    private int _page;
    private long _total, _pageGeneration, _collectionGeneration, _viewportInteraction;
    private long? _displayedCollection;
    private readonly ObservableCollection<PhotoDisplayCollection> _collections = [];
    private readonly ObservableCollection<PhotoDisplayRow> _rows = [];

    private void TrackViewportInput(Control control)
    {
        control.PreviewMouseWheel += (_, _) => _viewportInteraction++;
        control.PreviewMouseDown += (_, _) => _viewportInteraction++;
        control.PreviewKeyDown += (_, _) => _viewportInteraction++;
        control.PreviewTouchDown += (_, _) => _viewportInteraction++;
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
            FileList.Visibility = PhotoGallery.Visibility = Visibility.Collapsed; EmptyPanel.Visibility = Visibility.Visible;
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
            UpdateSortControls();
            UpdateEmptyState(collection, lastJob);
            UpdateCoverageNotice(collection, lastJob);
            CoverageText.Text = $"{collection.Status} · Observed {DisplayDate(collection.Observed)}\n" + (_mode switch
            {
                PhotoMode.Duplicates => "Default-stream equality only · Companion files and alternate streams are not equivalent by implication.",
                PhotoMode.Families => "Possible basename relationships within the same folder · No equivalence or edit-fidelity claim.",
                _ => lastJob ?? "Filenames provide format hints. Analyze verifies supported metadata and content."
            }) + (collection.Coverage is null ? "" : "\n" + collection.Coverage);
            if (_discovering) CoverageText.Text = "Preliminary metadata · Unvisited locations retain earlier observations.\n" + CoverageText.Text;
            PageText.Text = $"{_page * PhotoCatalog.PageSize + 1:N0}–{_page * PhotoCatalog.PageSize + result.Rows.Count:N0} of {_total:N0} files";
            PageText.ToolTip = $"Sorted by {_sort} ({(_descending ? "descending" : "ascending")}) · {PhotoCatalog.PageSize} rows per page";
            UpdateControls();
            RequestDisplayedPreviews();
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
        var content = ListButton.IsChecked == true ? (Control)FileList : PhotoGallery;
        var scroll = FindVisualChild<ScrollViewer>(content);
        var vertical = scroll?.VerticalOffset ?? 0; var horizontal = scroll?.HorizontalOffset ?? 0;
        var anchor = ReferenceEquals(content, FileList) && scroll?.CanContentScroll == true && VirtualizingPanel.GetScrollUnit(FileList) == ScrollUnit.Item && _rows.Count > 0
            ? _rows[Math.Min((int)vertical, _rows.Count - 1)] : null;
        // A wrapped gallery scrolls in pixels. Keep the first visible tile at
        // the same position when a refreshed page gains or loses earlier rows.
        if (ReferenceEquals(content, PhotoGallery) && scroll is not null)
            anchor = _rows.FirstOrDefault(row => PhotoGallery.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement tile &&
                tile.TranslatePoint(new Point(), PhotoGallery).Y + tile.ActualHeight > 0);
        var anchorTop = anchor is not null && ReferenceEquals(content, PhotoGallery) && PhotoGallery.ItemContainerGenerator.ContainerFromItem(anchor) is FrameworkElement anchorTile
            ? anchorTile.TranslatePoint(new Point(), PhotoGallery).Y : (double?)null;
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
            if (anchor is not null && _rows.Contains(anchor) && ReferenceEquals(content, FileList)) vertical = _rows.IndexOf(anchor) + vertical - Math.Floor(vertical);
            // Let WPF process the small collection delta before restoring its
            // viewport. A later query/user scroll wins over this restoration.
            var interaction = _viewportInteraction;
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (generation != _pageGeneration || interaction != _viewportInteraction) return;
                if (anchorTop is double earlierTop && anchor is not null && PhotoGallery.ItemContainerGenerator.ContainerFromItem(anchor) is FrameworkElement currentTile)
                    vertical = scroll.VerticalOffset + currentTile.TranslatePoint(new Point(), PhotoGallery).Y - earlierTop;
                scroll.ScrollToVerticalOffset(vertical); scroll.ScrollToHorizontalOffset(horizontal);
            }, DispatcherPriority.Loaded);
        }
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
        UpdateSortControls();
        await LoadPageAsync();
    }
    private async void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSort || !IsInitialized || FileList is null || SortChoice.SelectedItem is not ComboBoxItem { Tag: string name } ||
            !Enum.TryParse<PhotoSort>(name, out var sort) || sort == _sort) return;
        _sort = sort; _descending = sort == PhotoSort.Size; _page = 0;
        UpdateSortControls(); await LoadPageAsync();
    }
    private async void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        _descending = !_descending; _page = 0;
        UpdateSortControls(); await LoadPageAsync();
    }
    private void UpdateSortControls()
    {
        _suppressSort = true;
        try
        {
            SortChoice.SelectedItem = SortChoice.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, _sort.ToString()));
            SortDirectionButton.Content = _descending ? "↓" : "↑";
            SortDirectionButton.ToolTip = (_descending ? "Descending" : "Ascending") + " · Click to reverse";
            foreach (var column in FileList.Columns)
                column.SortDirection = column.SortMemberPath == _sort.ToString() ? _descending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
        }
        finally { _suppressSort = false; }
    }
    private void Display_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || PhotoGallery is null || FileList is null) return;
        _viewportInteraction++;
        UpdateDisplayControls();
        // Switching views reuses the page, selection and decoded previews.
        // A visible gallery's Loaded handlers request only its bounded rows.
        var selected = FileList.SelectedItem;
        if (ListButton.IsChecked == true && selected is not null) FileList.ScrollIntoView(selected);
        else if (selected is not null) PhotoGallery.ScrollIntoView(selected);
        RequestDisplayedPreviews();
    }
    private void UpdateDisplayControls()
    {
        var populated = _rows.Count > 0;
        var list = ListButton.IsChecked == true;
        FileList.Visibility = populated && list ? Visibility.Visible : Visibility.Collapsed;
        PhotoGallery.Visibility = populated && !list ? Visibility.Visible : Visibility.Collapsed;
        ColumnsButton.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) { _page = Math.Max(0, _page - 1); await LoadPageAsync(); }
    private async void Next_Click(object sender, RoutedEventArgs e) { if ((_page + 1L) * PhotoCatalog.PageSize < _total) _page++; await LoadPageAsync(); }
}
