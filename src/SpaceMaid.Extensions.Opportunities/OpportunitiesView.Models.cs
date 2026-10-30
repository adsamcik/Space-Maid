using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Core.IO;

namespace SpaceMaid.Extensions.Opportunities;

public partial class OpportunitiesView
{
    private readonly DiskAccess _files;
    private CancellationTokenSource? _modelRead;
    private string? _modelLibraryPath;
    private ModelLibrary? _modelLibrary;

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
            if (_modelRead == read) { _modelRead = null; RefreshModelsButton.IsEnabled = true; }
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
    }
}
