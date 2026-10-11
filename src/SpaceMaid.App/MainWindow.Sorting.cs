using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private ScanSortColumn _treeSortColumn = ScanSortColumn.Allocated;
    private bool _treeDescending = true;
    private ScanSortColumn _preliminarySortColumn = ScanSortColumn.Allocated;
    private bool _preliminaryDescending = true, _preliminaryUserSorted;

    private async void TreeSort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<ScanSortColumn>(tag, out var column))
            await SortTreeAsync(column);
    }

    internal Task SortTreeAsync(ScanSortColumn column) => BrowseAsync(async token =>
    {
        var scan = _snapshot!;
        bool descending = _treeSortColumn == column ? !_treeDescending : column != ScanSortColumn.Name;
        var directories = _childOrders.Keys.ToArray();
        var orders = await Task.Run(() => directories.ToDictionary(directory => directory,
            directory => scan.SortedChildren(directory, column, descending, token)), token);
        token.ThrowIfCancellationRequested();
        int selected = _selectedLink;
        bool grouped = _selectionIsGroup;
        _syncTree = true;
        try
        {
            _explorer!.Reorder(orders, token);
            _childOrders.Clear();
            foreach (var order in orders) _childOrders.Add(order.Key, order.Value);
            _treeSortColumn = column;
            _treeDescending = descending;
            SelectTree(selected);
            _selectionIsGroup = grouped;
        }
        finally { _syncTree = false; }
        UpdateTreeSortHeaders();
    });

    private void UpdateTreeSortHeaders()
    {
        SetSortHeader(NameHeader, "Name", ScanSortColumn.Name, _treeSortColumn, _treeDescending);
        SetSortHeader(AllocationHeader, "Allocated", ScanSortColumn.Allocated, _treeSortColumn, _treeDescending);
        SetSortHeader(LogicalHeader, "Logical", ScanSortColumn.Logical, _treeSortColumn, _treeDescending);
        SetSortHeader(ShareHeader, "% parent", ScanSortColumn.Share, _treeSortColumn, _treeDescending);
    }

    private static void SetSortHeader(Button button, string label, ScanSortColumn column, ScanSortColumn selected, bool descending)
    {
        bool active = column == selected;
        button.Content = label + (active ? descending ? " ↓" : " ↑" : "");
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        string state = active ? descending ? ", descending" : ", ascending" : "";
        AutomationProperties.SetName(button, $"Sort by {label}{state}");
        button.ToolTip = $"Sort by {label}{state}. Click to {(active && descending ? "sort ascending" : active ? "sort descending" : column == ScanSortColumn.Name ? "sort A–Z" : "show largest first")}.";
    }

    private void PreliminarySort_Click(object sender, RoutedEventArgs e)
    {
        if (_preliminary is null || sender is not Button { Tag: string tag }
            || !Enum.TryParse<ScanSortColumn>(tag, out var column)) return;
        _preliminaryDescending = _preliminarySortColumn == column ? !_preliminaryDescending : column != ScanSortColumn.Name;
        _preliminarySortColumn = column;
        _preliminaryUserSorted = true;
        RefreshPreliminaryRows();
    }

    private void RefreshPreliminaryRows()
    {
        if (_preliminary is not { } preliminary) return;
        var selected = (PreliminaryFiles.SelectedItem as PreliminaryRow)?.File;
        var rows = preliminary.LargestFiles.Select(file => new PreliminaryRow(file, preliminary.HasAllocationData)).ToArray();
        Array.Sort(rows, (a, b) =>
        {
            int comparison = _preliminarySortColumn switch
            {
                ScanSortColumn.Name => StringComparer.OrdinalIgnoreCase.Compare(a.File.Name, b.File.Name),
                ScanSortColumn.Allocated => preliminary.HasAllocationData ? a.File.AllocatedBytes.CompareTo(b.File.AllocatedBytes) : 0,
                _ => a.File.LogicalBytes.CompareTo(b.File.LogicalBytes)
            };
            if (comparison != 0) return _preliminaryDescending ? -comparison : comparison;
            int name = StringComparer.OrdinalIgnoreCase.Compare(a.File.Name, b.File.Name);
            return name != 0 ? name : StringComparer.OrdinalIgnoreCase.Compare(a.File.Path, b.File.Path);
        });
        PreliminaryFiles.ItemsSource = rows;
        if (selected is not null) PreliminaryFiles.SelectedItem = rows.FirstOrDefault(row => row.File == selected);
        SetSortHeader(PreliminaryNameHeader, "File / exact path", ScanSortColumn.Name, _preliminarySortColumn, _preliminaryDescending);
        SetSortHeader(PreliminaryAllocationHeader, "Allocated", ScanSortColumn.Allocated, _preliminarySortColumn, _preliminaryDescending);
        SetSortHeader(PreliminaryLogicalHeader, "Logical", ScanSortColumn.Logical, _preliminarySortColumn, _preliminaryDescending);
    }
}
