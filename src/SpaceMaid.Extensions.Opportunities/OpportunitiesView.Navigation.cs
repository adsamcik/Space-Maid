using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.Extensions.Opportunities;

public partial class OpportunitiesView
{
    // Only the last visited cleanup scope is retained, for this inventory/session.
    // Checkbox consent stays on the current rows, never in navigation history.
    private sealed record BrowseLocation(string? Scope, string? Group, OpportunityGroup? Project, string Query, string? Type, string? DetailPath, double Offset);
    private BrowseLocation? _lastCleanupBrowse, _selectionBrowse;
    private bool SelectedView => SelectedOnlyToggle.IsChecked == true;

    private BrowseLocation CaptureBrowseLocation() => new(_cleanupDetailKey ?? _activeOpportunityGroup, _activeOpportunityGroup,
        _cleanupProjectScope is { } project ? new(project.Key, project.Name, project.Path, project.Kind, [], 0) : null,
        OpportunitySearch.Text, OpportunityTypeFilter.SelectedItem as string, Detail?.Path, LocationScroll()?.VerticalOffset ?? 0);

    private ScrollViewer? LocationScroll(DependencyObject? root = null)
    {
        root ??= OpportunityGrid;
        if (root is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (LocationScroll(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }

    private void RestoreBrowseLocation(BrowseLocation location)
    {
        _opportunitySearchTimer.Stop();
        _activeOpportunityGroup = location.Group; _cleanupProjectScope = location.Project;
        _syncOpportunityBrowser = true;
        try
        {
            OpportunitySearch.Text = location.Query;
            OpportunityTypeFilter.SelectedItem = location.Type;
            if (OpportunityTypeFilter.SelectedIndex < 0) OpportunityTypeFilter.SelectedIndex = 0;
        }
        finally { _syncOpportunityBrowser = false; }
        ApplyOpportunityFilters(location.DetailPath);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_disposed && !SelectedView && _activeOpportunityGroup == location.Group)
                LocationScroll()?.ScrollToVerticalOffset(location.Offset);
        }));
    }

    private bool RestoreLastCleanupBrowse(string scope)
    {
        if (_lastCleanupBrowse is not { } location || location.Scope != scope) return false;
        RestoreBrowseLocation(location); return true;
    }

    private void ShowSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (SelectedView)
        {
            _selectionBrowse = CaptureBrowseLocation();
            _opportunitySearchTimer.Stop(); ClearOpportunityFilters(); ApplyOpportunityFilters();
        }
        else if (_selectionBrowse is { } location)
        {
            _selectionBrowse = null; RestoreBrowseLocation(location);
        }
    }
}
