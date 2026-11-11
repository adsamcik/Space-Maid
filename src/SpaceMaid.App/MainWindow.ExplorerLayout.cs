using System.Windows;
using System.Windows.Controls;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private bool _compactExplorer;
    private GridLength _listPaneWidth = new(1, GridUnitType.Star);
    private GridLength _mapPaneWidth = new(1.1, GridUnitType.Star);

    private void Explorer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || ExplorePanel.ActualWidth <= 0) return;
        bool compact = ExplorePanel.ActualWidth < 880;
        if (compact == _compactExplorer) return;
        if (compact)
        {
            _listPaneWidth = ListColumn.Width;
            _mapPaneWidth = MapColumn.Width;
        }
        _compactExplorer = compact;
        UpdateExplorerLayout();
    }

    private void ExplorerView_Changed(object sender, RoutedEventArgs e)
    {
        // Checked also fires while InitializeComponent is creating the controls.
        if (MapLayout is not null) UpdateExplorerLayout();
    }

    private void UpdateExplorerLayout()
    {
        bool mapOnly = _compactExplorer && MapViewButton.IsChecked == true;
        bool listOnly = _compactExplorer && !mapOnly;
        bool leavingViewSwitch = !_compactExplorer && CompactViews.IsKeyboardFocusWithin;
        bool movingFocus = listOnly && SpaceMap.IsKeyboardFocusWithin
            || mapOnly && ListPane.IsKeyboardFocusWithin
            || _compactExplorer && ExplorerDivider.IsKeyboardFocusWithin;
        CompactViews.Visibility = _compactExplorer ? Visibility.Visible : Visibility.Collapsed;
        ExplorerDivider.Visibility = _compactExplorer ? Visibility.Collapsed : Visibility.Visible;
        ListColumn.MinWidth = MapColumn.MinWidth = _compactExplorer ? 0 : 340;
        ListColumn.Width = _compactExplorer ? new(1, GridUnitType.Star) : _listPaneWidth;
        DividerColumn.Width = new(_compactExplorer ? 0 : 12);
        MapColumn.Width = _compactExplorer ? new(0) : _mapPaneWidth;
        ListPane.Visibility = mapOnly ? Visibility.Collapsed : Visibility.Visible;
        MapLayout.Visibility = listOnly ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRowSpan(ListPane, _compactExplorer ? 1 : 2);
        Grid.SetColumnSpan(ListPane, _compactExplorer ? 3 : 1);
        foreach (var element in new FrameworkElement[] { MapPane, MapLayout, EntryDetail })
        {
            Grid.SetColumn(element, _compactExplorer ? 0 : 2);
            Grid.SetColumnSpan(element, _compactExplorer ? 3 : 1);
        }
        // The same selection footer stays available beneath either compact view.
        Grid.SetRow(MapPane, listOnly ? 2 : 1);
        Grid.SetRowSpan(MapPane, listOnly ? 1 : 2);
        EntryDetail.Margin = new(12, listOnly ? 8 : 0, 12, 12);
        if (movingFocus) (mapOnly ? MapViewButton : ListViewButton).Focus();
        else if (leavingViewSwitch) ExplorerDivider.Focus();
    }

    private void FocusExplorerContent()
    {
        if (_compactExplorer && MapViewButton.IsChecked == true)
        {
            SpaceMap.UpdateLayout();
            if (SpaceMap.Buttons.FirstOrDefault(button => button.IsVisible)?.Focus() != true)
                MapViewButton.Focus();
        }
        // Focus the chosen row so the list cannot restore a previously focused
        // row and unexpectedly navigate back to it.
        else if (SpaceTree.SelectedItem is { } selected
            && SpaceTree.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem container) container.Focus();
        else SpaceTree.Focus();
    }
}
