using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    internal string ColumnLayoutPath { get; set; } = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceMaid", "tree-columns.json");
    private readonly DispatcherTimer _columnSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Dictionary<ScanSortColumn, GridViewColumn> _treeColumns = [];
    private bool _changingColumns, _fitNameColumn = true, _columnsReady;
    private string? _columnSettingsError;

    private void InitializeTreeColumns()
    {
        _treeColumns = TreeColumns.Columns.ToDictionary(column => Enum.Parse<ScanSortColumn>(((GridViewColumnHeader)column.Header).Tag.ToString()!));
        foreach (var column in _treeColumns.Values)
        {
            var header = (GridViewColumnHeader)column.Header;
            header.ContextMenu = new ContextMenu();
            header.ContextMenuOpening += ColumnHeader_ContextMenuOpening;
            DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn))!
                .AddValueChanged(column, ColumnWidth_Changed);
        }
        ((INotifyCollectionChanged)TreeColumns.Columns).CollectionChanged += ColumnOrder_Changed;
        _columnSaveTimer.Tick += (_, _) => SaveColumnLayout();
        ApplyColumnLayout(TreeColumnLayout.Default);
        _columnsReady = true;
    }

    private void LoadColumnLayout()
    {
        try { ApplyColumnLayout(TreeColumnLayout.Load(ColumnLayoutPath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { ReportColumnSettingsError(error); }
    }

    private void ApplyColumnLayout(TreeColumnLayout layout)
    {
        var viewport = CaptureTreeViewport();
        _changingColumns = true;
        try
        {
            _fitNameColumn = layout.FitName;
            TreeColumns.Columns.Clear();
            foreach (var state in layout.Columns)
            {
                var column = _treeColumns[state.Id];
                column.Width = state.Width;
                if (state.Visible) TreeColumns.Columns.Add(column);
            }
        }
        finally { _changingColumns = false; }
        FitNameColumn();
        RestoreTreeViewport(viewport);
    }

    private void Tree_SizeChanged(object sender, SizeChangedEventArgs e) => FitNameColumn();

    private ScanSortColumn VisibleTreeSort(ScanSnapshot scan)
    {
        if (TreeColumns.Columns.Contains(_treeColumns[_treeSortColumn])
            && (_treeSortColumn != ScanSortColumn.Allocated || scan.HasAllocationData)) return _treeSortColumn;
        if (scan.HasAllocationData && TreeColumns.Columns.Contains(AllocationColumn)) return ScanSortColumn.Allocated;
        return TreeColumns.Columns.Contains(LogicalColumn) ? ScanSortColumn.Logical : ScanSortColumn.Name;
    }

    private void FitNameColumn()
    {
        if (!_fitNameColumn || !_treeColumns.TryGetValue(ScanSortColumn.Name, out var name) || SpaceTree.ActualWidth <= 0) return;
        _changingColumns = true;
        try
        {
            double others = TreeColumns.Columns.Where(column => column != name).Sum(column => double.IsNaN(column.Width) ? column.ActualWidth : column.Width);
            // Leave room for the native row margins and vertical scrollbar.
            name.Width = Math.Clamp(SpaceTree.ActualWidth - others - 30, 180, 1600);
        }
        finally { _changingColumns = false; }
    }

    private void ColumnWidth_Changed(object? sender, EventArgs e)
    {
        if (_changingColumns || !_columnsReady) return;
        if (ReferenceEquals(sender, NameColumn)) _fitNameColumn = false;
        if (sender is GridViewColumn column && !double.IsNaN(column.Width))
        {
            _changingColumns = true;
            try { column.Width = Math.Clamp(column.Width, ReferenceEquals(column, NameColumn) ? 140 : 48, 1600); }
            finally { _changingColumns = false; }
        }
        FitNameColumn();
        QueueColumnSave();
    }

    private void ColumnOrder_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_changingColumns) QueueColumnSave();
    }

    private void QueueColumnSave()
    {
        if (!_columnsReady || _closing) return;
        _columnSaveTimer.Stop(); _columnSaveTimer.Start();
    }

    internal void SaveColumnLayout()
    {
        _columnSaveTimer.Stop();
        if (!_columnsReady) return;
        try
        {
            var order = TreeColumns.Columns.Concat(_treeColumns.Values.Where(column => !TreeColumns.Columns.Contains(column)));
            var states = order.Select(column => new TreeColumnState(Enum.Parse<ScanSortColumn>(((GridViewColumnHeader)column.Header).Tag.ToString()!),
                TreeColumns.Columns.Contains(column), Math.Clamp(double.IsNaN(column.Width) ? column.ActualWidth : column.Width,
                    ReferenceEquals(column, NameColumn) ? 140 : 48, 1600))).ToArray();
            new TreeColumnLayout(_fitNameColumn, states).Save(ColumnLayoutPath);
            _columnSettingsError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { ReportColumnSettingsError(error); }
    }

    private void ReportColumnSettingsError(Exception error)
    {
        _columnSettingsError = "Column preferences couldn’t be saved or loaded.";
        Debug.WriteLine($"Tree columns: {error}");
        ColumnsButton.ToolTip = _columnSettingsError;
    }

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var menu = CreateColumnMenu();
        menu.PlacementTarget = ColumnsButton; menu.Placement = PlacementMode.Top;
        ColumnsButton.ContextMenu = menu; menu.IsOpen = true;
    }

    private void ColumnHeader_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        var header = (GridViewColumnHeader)sender;
        var menu = CreateColumnMenu(); menu.PlacementTarget = header; header.ContextMenu = menu; menu.IsOpen = true;
    }

    private ContextMenu CreateColumnMenu()
    {
        var menu = new ContextMenu();
        // A popup does not inherit the list's disabled state while sorting.
        menu.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = ExplorePanel });
        foreach (var state in TreeColumnLayout.Default.Columns)
        {
            var column = _treeColumns[state.Id];
            var item = new MenuItem { Header = state.Id == ScanSortColumn.Share ? "% parent" : state.Id.ToString(),
                Tag = state.Id, IsCheckable = true, IsChecked = TreeColumns.Columns.Contains(column),
                IsEnabled = state.Id != ScanSortColumn.Name, StaysOpenOnClick = true };
            item.Click += async (_, _) =>
            {
                var viewport = CaptureTreeViewport();
                _changingColumns = true;
                try
                {
                    if (item.IsChecked) TreeColumns.Columns.Add(column);
                    else TreeColumns.Columns.Remove(column);
                }
                finally { _changingColumns = false; }
                FitNameColumn(); RestoreTreeViewport(viewport); QueueColumnSave();
                if (!item.IsChecked && _treeSortColumn == state.Id && _snapshot is not null)
                    await SortTreeAsync(TreeColumns.Columns.Contains(AllocationColumn) ? ScanSortColumn.Allocated : ScanSortColumn.Name);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset columns" };
        reset.Click += async (_, _) =>
        {
            ApplyColumnLayout(TreeColumnLayout.Default); QueueColumnSave();
            if (_snapshot is not null && !TreeColumns.Columns.Contains(_treeColumns[_treeSortColumn]))
                await SortTreeAsync(ScanSortColumn.Allocated);
        };
        menu.Items.Add(reset);
        if (_columnSettingsError is not null) menu.Items.Add(new MenuItem { Header = _columnSettingsError, IsEnabled = false });
        return menu;
    }
}
