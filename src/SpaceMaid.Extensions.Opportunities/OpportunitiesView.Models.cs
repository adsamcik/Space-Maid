using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Core.IO;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Opportunities;

public partial class OpportunitiesView
{
    private readonly DiskAccess _files;
    private CancellationTokenSource? _modelRead;
    private string? _modelLibraryPath;
    private ModelLibrary? _modelLibrary;
    private CancellationTokenSource? _modelRemoval;

    private void UpdateModelLibrary()
    {
        string? path = OpportunityLocations.Visibility == Visibility.Visible && Detail?.Title == "Ollama model library" ? Detail.Path : null;
        bool show = path is not null;
        bool single = show && _visibleOpportunityRows.Count == 1;
        OllamaModelsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        bool filtered = !string.IsNullOrWhiteSpace(OpportunitySearch.Text) || OpportunityTypeFilter.SelectedItem is string type && type != "All types";
        OpportunityFilters.Visibility = single && !filtered ? Visibility.Collapsed : Visibility.Visible;
        OpportunityGrid.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        OpportunityRowsHeight.Height = show ? single ? new GridLength(0) : new GridLength(110) : new GridLength(1, GridUnitType.Star);
        ModelRowsHeight.Height = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (path == _modelLibraryPath) return;
        _modelRemoval?.Cancel();
        _modelRead?.Cancel(); _modelLibraryPath = path; _modelLibrary = null;
        ModelSearch.Text = ""; LibraryModelList.ItemsSource = null;
        if (path is not null) _ = ReadModelLibraryAsync();
    }
    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await ReadModelLibraryAsync();
    private async Task ReadModelLibraryAsync()
    {
        if (_disposed || _modelLibraryPath is not { } path) return;
        string? selected = (LibraryModelList.SelectedItem as LibraryModel)?.Name;
        _modelRead?.Cancel();
        using var read = new CancellationTokenSource();
        _modelRead = read; _modelLibrary = null; LibraryModelList.ItemsSource = null;
        UpdateModelRemoval();
        RefreshModelsButton.IsEnabled = false; ModelStatus.Text = "Reading models…";
        try
        {
            var library = await Task.Run(() => OllamaLibrary.Read(_files, path, read.Token), read.Token);
            if (read.IsCancellationRequested || _disposed || _modelRead != read) return;
            _modelLibrary = library; FilterModels();
            LibraryModelList.SelectedItem = LibraryModelList.Items.Cast<LibraryModel>().FirstOrDefault(model => model.Name == selected);
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception error) when (OllamaLibrary.Expected(error))
        {
            if (!read.IsCancellationRequested && !_disposed && _modelRead == read)
            {
                ModelStatus.Text = "Models could not be read. Check folder access, then refresh.";
                ModelStatus.ToolTip = error.Message;
            }
        }
        finally
        {
            if (_modelRead == read) { _modelRead = null; RefreshModelsButton.IsEnabled = _modelRemoval is null; UpdateModelRemoval(); }
        }
    }
    private void ModelSearch_Changed(object sender, TextChangedEventArgs e) => FilterModels();
    private void FilterModels()
    {
        if (LibraryModelList is null || _modelLibrary is not { } library) return;
        string? selected = (LibraryModelList.SelectedItem as LibraryModel)?.Name;
        string query = ModelSearch.Text.Trim();
        var models = library.Models.Where(model => model.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        LibraryModelList.ItemsSource = models;
        LibraryModelList.SelectedItem = models.FirstOrDefault(model => model.Name == selected);
        ModelStatus.Text = $"{models.Length:N0}" + (models.Length == library.Models.Count ? "" : $" of {library.Models.Count:N0}")
            + (library.Models.Count == 1 ? " model" : " models") + (library.Note.Length > 0 ? " · " + library.Note : "");
        ModelStatus.ToolTip = null;
        UpdateModelRemoval();
    }
    private void LibraryModel_Changed(object sender, SelectionChangedEventArgs e) => UpdateModelRemoval();
    private void UpdateModelRemoval()
    {
        if (RemoveModelButton is null) return;
        RemoveModelButton.IsEnabled = !_busy && _modelRead is null && _modelRemoval is null && _host.CanRemoveModels
            && LibraryModelList.SelectedItem is LibraryModel { Digest: not null, Bytes: not null };
        RemoveModelButton.ToolTip = _host.CanRemoveModels ? "Check this model and its library in Ollama, then review permanent removal."
            : "Enable the AI models extension and reopen Space Maid to remove models.";
    }
    private async void RemoveModel_Click(object sender, RoutedEventArgs e) => await RemoveLibraryModelAsync();
    internal async Task RemoveLibraryModelAsync()
    {
        if (!RemoveModelButton.IsEnabled || _modelLibraryPath is not { } path
            || LibraryModelList.SelectedItem is not LibraryModel { Digest: { } digest, Bytes: { } bytes } selected) return;
        var probe = selected.BlobDigest is not null ? selected : _modelLibrary?.Models.FirstOrDefault(model => model.BlobDigest is not null);
        using var removal = new CancellationTokenSource();
        _modelRemoval = removal; UpdateModelRemoval();
        RefreshModelsButton.IsEnabled = LibraryModelList.IsEnabled = ModelSearch.IsEnabled = false;
        ModelStatus.Text = "Checking the model and its library in Ollama…";
        try
        {
            var request = new ModelRemovalRequest(path, selected.Name, digest, bytes, probe?.Name ?? "", probe?.Digest ?? "", probe?.BlobDigest ?? "");
            var result = await _host.ReviewModelRemovalAsync(request, removal.Token);
            if (_disposed || _modelLibraryPath != path) return;
            if (result.Attempted) await ReadModelLibraryAsync();
            if (_disposed || _modelLibraryPath != path) return;
            ModelStatus.Text = result.Message + (result.Attempted && _modelLibrary is null
                ? " Models could not be refreshed. Check folder access, then refresh."
                : _modelLibrary?.Note is { Length: > 0 } note ? " " + note : "");
        }
        finally
        {
            _modelRemoval = null;
            RefreshModelsButton.IsEnabled = LibraryModelList.IsEnabled = ModelSearch.IsEnabled = true;
            UpdateModelRemoval();
        }
    }
}
