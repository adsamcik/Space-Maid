using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(SpaceTree, e.OriginalSource as DependencyObject) as ListBoxItem;
        var row = item?.DataContext as ExplorerRow;
        // Apps / Shift+F10 can originate on the list itself. A mouse click on
        // empty space must never inherit an unrelated selected folder.
        if (row is null && e.CursorLeft < 0)
        {
            row = SpaceTree.SelectedItem as ExplorerRow;
            item = SpaceTree.ItemContainerGenerator.ContainerFromIndex(SpaceTree.SelectedIndex) as ListBoxItem;
        }
        if (row is null || !PrepareFolderMenu(SpaceTree.ContextMenu, row.Scan, row.Entry.LinkIndex))
        { e.Handled = true; return; }
        ContextMenuService.SetPlacementTarget(SpaceTree, item ?? (UIElement)SpaceTree);
    }

    private bool PrepareFolderMenu(ContextMenu menu, ScanSnapshot scan, int link)
    {
        if (!CanUseFolderMenu(scan)) return false;
        var entry = scan.Entry(link);
        string? path = scan.EntryPath(link);
        if (path is null || link < -1) return false;
        if (!SelectContextEntry(scan, link)) return false;
        menu.Items.Clear();
        menu.FontFamily = FontFamily; menu.FontSize = FontSize;
        if (entry.IsDirectory)
        {
            var open = new MenuItem { Header = "Open in File Explorer" };
            open.Click += (_, _) =>
            {
                if (CanUseFolderMenu(scan)) OpenFolderInExplorer(path);
            };
            string? blocked = _scanStale ? "Rescan before deleting another folder."
                : FolderDeletionTarget.SelectionBlockReason(scan, link);
            var recycle = new MenuItem { Header = "Move to Recycle Bin", IsEnabled = blocked is null, ToolTip = blocked };
            ToolTipService.SetShowOnDisabled(recycle, true);
            recycle.Click += async (_, _) =>
            {
                if (CanUseFolderMenu(scan)) await DeleteFolderAsync(scan, link);
            };
            menu.Items.Add(open); menu.Items.Add(recycle);
            var recheck = new MenuItem { Header = "Recheck this folder" };
            recheck.Click += async (_, _) => { if (CanUseFolderMenu(scan)) await RecheckFolderAsync(path); };
            menu.Items.Add(recheck);
        }
        var checking = new MenuItem { Header = "Checking cloud availability…", IsEnabled = false };
        menu.Items.Add(checking);
        var marker = new object(); menu.Tag = marker;
        _ = AddCloudMenuAsync(menu, checking, marker, scan, link, path);
        return true;
    }

    private async Task AddCloudMenuAsync(ContextMenu menu, MenuItem checking, object marker, ScanSnapshot scan, int link, string path)
    {
        var state = await Task.Run(() => CloudFiles.Inspect(path));
        if (!CanUseFolderMenu(scan) || !ReferenceEquals(menu.Tag, marker)) return;
        menu.Items.Remove(checking);
        if (state is not null)
        {
            bool enabled = !_scanStale && (state.IsDirectory || state.CanRequestOnlineOnly);
            var online = new MenuItem { Header = "Make online-only", IsEnabled = enabled,
                ToolTip = _scanStale ? "Recheck this folder or rescan first." : enabled ? $"Managed by {state.Provider}"
                    : state.LocalBytes == 0 ? "This file is already online-only." : "This file is unsynced or excluded by the provider." };
            ToolTipService.SetShowOnDisabled(online, true);
            online.Click += async (_, _) => { if (CanUseFolderMenu(scan)) await MakeOnlineOnlyAsync(scan, link); };
            menu.Items.Add(online);
        }
        if (menu.Items.Count == 0) menu.IsOpen = false;
    }

    private bool CanUseFolderMenu(ScanSnapshot scan) => !_closing && _work is null && !_browsing && !_deleting
        && ReferenceEquals(_snapshot, scan);

    private bool SelectContextEntry(ScanSnapshot scan, int link)
    {
        // Context selection does not drill into the folder or replace the
        // map button while its menu is opening. Details and Delete still
        // reflect the clicked entry; menu actions capture their own target.
        if (_explorer is null) return false;
        if (_explorer.Find(link) < 0)
        {
            // A displayed map folder can have collapsed tree ancestors. Their
            // child orders were cached when displaying the map; reveal that
            // path without sorting on the UI thread or rebuilding the map.
            var ancestors = AncestorLinks(scan, link);
            _syncTree = true;
            try
            {
                while (ancestors.TryPop(out int ancestor))
                {
                    if (_explorer.IsExpanded(ancestor)) continue;
                    if (!_childOrders.TryGetValue(scan.Entry(ancestor).FileIndex, out var children)) return false;
                    _explorer.Expand(ancestor, children);
                }
            }
            finally { _syncTree = false; }
            if (_explorer.Find(link) < 0) return false;
        }
        SelectTree(link);
        EntryDetail.DataContext = new ExplorerRow(scan, scan.Entry(link), 0, _explorer!.IsExpanded(link));
        EntryDetail.Visibility = Visibility.Visible;
        return true;
    }
}
