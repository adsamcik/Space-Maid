using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpaceMaid.Extensions.Photos;

public partial class PhotosView
{
    private bool _changingCollection, _collectionReloadRequired;

    private void CollectionOptions_Click(object sender, RoutedEventArgs e)
    {
        CollectionList.ContextMenu.PlacementTarget = (Button)sender;
        CollectionList.ContextMenu.IsOpen = true;
    }

    private async void ChangeLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _collectionReloadRequired || Collection is not { } collection) return;
        var picker = new OpenFolderDialog { Title = "Choose the collection's current location · Photos stay untouched" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await Track(ChangeCollectionLocationAsync(collection, picker.FolderName));
    }

    private Task ChangeCollectionLocationAsync(PhotoCollection collection, string path) => ChangeCollectionIndexAsync(collection,
        token => _service.ChangeCollectionRootAsync(collection, path, token),
        "Collection location updated. Refresh filenames, then Analyze to verify content. Original files and keep preferences stay untouched.");

    private async void RemoveCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _collectionReloadRequired || Collection is not { } collection) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Remove the indexed locations and job history for ‘{collection.Name}’?\n\nPhotos, sidecars and keep preferences stay untouched. Other collections stay. You can add this folder again.",
            "Remove collection index", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await Track(RemoveCollectionIndexAsync(collection));
    }

    private Task RemoveCollectionIndexAsync(PhotoCollection collection) => ChangeCollectionIndexAsync(collection,
        token => _service.RemoveCollectionAsync(collection, token), "Collection index removed. Photos, sidecars and keep preferences stay untouched.");

    private async Task ChangeCollectionIndexAsync(PhotoCollection collection, Func<CancellationToken, Task> change, string completed)
    {
        if (_busy || _collectionReloadRequired || _shuttingDown || Collection?.Id != collection.Id) return;
        _busy = _changingCollection = true;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _pageGeneration++; _pageQuery?.Cancel(); ResetPreviewRequests(); UpdateControls();
        bool committed = false;
        try
        {
            await change(_operation.Token);
            committed = _collectionReloadRequired = true;
            _selectionGeneration++; _rows.Clear(); FileList.SelectedItem = null;
            SelectedPreview.Source = null; SelectionPanel.Visibility = Visibility.Collapsed;
            _page = 0; _total = 0; _displayedCollection = null;
            await LoadCollectionsAsync(collection.Id);
            SetNotice(completed + (_pageLoadFailure is null ? "" : " The view could not refresh. Use Try again. " + _pageLoadFailure));
        }
        catch (OperationCanceledException) when (!committed) { SetNotice("Collection change cancelled. The previous index is retained."); }
        catch (Exception error)
        {
            if (!committed) ShowFailure(error);
            else
            {
                SetNotice(completed + " The view could not refresh: " + error.Message);
                SetEmpty("Collection index updated", "Reload the collection list to continue. The completed index change will not be repeated.", EmptyAction.ReloadCollections, "Reload collection list");
                EmptyPanel.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _busy = _changingCollection = false; _operation.Dispose(); _operation = null; UpdateControls();
            RequestDisplayedPreviews();
        }
    }
}
