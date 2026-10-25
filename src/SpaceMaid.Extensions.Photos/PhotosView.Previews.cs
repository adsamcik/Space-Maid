using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SpaceMaid.Extensions.Photos;

public partial class PhotosView
{
    private CancellationTokenSource? _pageRequests;
    private long _selectionGeneration;
    private DispatcherOperation? _previewDispatch;
    private readonly Dictionary<PhotoDisplayRow, CancellationTokenSource> _previewRequests = [];

    private void ResetPreviewRequests()
    {
        _pageRequests?.Cancel(); _pageRequests?.Dispose();
        _pageRequests = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var requests = _previewRequests.Values.ToArray(); _previewRequests.Clear();
        foreach (var request in requests) { request.Cancel(); request.Dispose(); }
        foreach (var row in _rows) row.CancelPreviewRequest();
    }

    private void GalleryItem_Loaded(object sender, RoutedEventArgs e) => RequestDisplayedPreviews();
    private void RequestDisplayedPreviews()
    {
        if (_previewDispatch?.Status == DispatcherOperationStatus.Pending) return;
        // A WrapPanel realizes the whole bounded page. Request source reads
        // only for the viewport, with the current selection admitted first.
        _previewDispatch = Dispatcher.InvokeAsync(() =>
        {
            _previewDispatch = null;
            if (_shuttingDown || _discovering) return;
            var selected = FileList.SelectedItem as PhotoDisplayRow;
            var view = FileList.Visibility == Visibility.Visible ? (Control)FileList : PhotoGallery;
            if (!view.IsVisible) return;
            var visible = _rows.Where(row => InPreviewViewport(view, row)).ToArray();
            var wanted = visible.ToHashSet(); if (selected is not null) wanted.Add(selected);
            // Scrolling must release queued work from the previous viewport,
            // rather than leaving the next selection behind off-screen jobs.
            foreach (var row in _previewRequests.Keys.Where(row => !wanted.Contains(row)).ToArray())
            {
                var request = _previewRequests[row]; _previewRequests.Remove(row);
                row.CancelPreviewRequest(); request.Cancel(); request.Dispose();
            }
            if (selected is not null) _ = PreviewAsync(selected);
            foreach (var row in visible)
                if (!ReferenceEquals(row, selected)) _ = PreviewAsync(row);
        }, DispatcherPriority.Loaded);
    }

    private static bool InPreviewViewport(Control view, PhotoDisplayRow row)
    {
        var container = view switch
        {
            DataGrid list => list.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement,
            ListBox gallery => gallery.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement,
            _ => null
        };
        return container is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } &&
            container.TransformToAncestor(view).TransformBounds(new Rect(container.RenderSize)).IntersectsWith(new Rect(view.RenderSize));
    }
    private void File_LoadingRow(object? sender, DataGridRowEventArgs e) => RequestDisplayedPreviews();
    private Task PreviewAsync(PhotoDisplayRow display) => Track(PreviewCoreAsync(display));
    private async Task PreviewCoreAsync(PhotoDisplayRow display)
    {
        if (display.Requested || _pageRequests is null || _discovering) return;
        display.Requested = true;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_pageRequests.Token);
        _previewRequests.Add(display, request); var token = request.Token;
        var row = display.Row; var version = display.PreviewVersion;
        try
        {
            var result = await _thumbnails.GetAsync(row, token);
            if (token.IsCancellationRequested || version != display.PreviewVersion || !_rows.Contains(display)) return;
            display.TrySetPreview(result, version);
            if (result.Image is null && display.Row.AnalysisCurrent && display.Row.ContentAvailable)
            {
                display.Requested = false;
                // A shared cache request may still be finishing cancellation
                // from the previous viewport. Retry after it leaves the cache.
                if (result.Status == "Preview cancelled") RequestDisplayedPreviews();
            }
            if (ReferenceEquals(FileList.SelectedItem, display)) UpdateSelectedPreview(display);
        }
        catch (OperationCanceledException) { if (version == display.PreviewVersion) display.Requested = false; }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && _rows.Contains(display)) display.TrySetPreview(new(null, "Preview unavailable"), version);
            _host.Log("photos-preview-failure", new { Error = error.GetType().Name });
        }
        finally
        {
            if (_previewRequests.TryGetValue(display, out var current) && ReferenceEquals(current, request))
            { _previewRequests.Remove(display); request.Dispose(); }
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
        PreviewStatusText.Text = row.Preview is null ? row.PreviewStatus : row.Row.Role == "RAW source" ? "RAW preview · May differ from current edits" : "";
        PreviewStatusText.Visibility = PreviewStatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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
    private void CloseSelection_Click(object sender, RoutedEventArgs e)
    {
        FileList.SelectedItem = null;
        if (ListButton.IsChecked == true) FileList.Focus(); else PhotoGallery.Focus();
    }
}
