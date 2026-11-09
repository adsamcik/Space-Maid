using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private ContextMenu? _activeFolderMenu;

    private void FolderMenuClosed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_activeFolderMenu, sender)) _activeFolderMenu = null;
        SignalRecycleStateChange();
    }

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.Handled) return;
        var item = ItemsControl.ContainerFromElement(SpaceTree, e.OriginalSource as DependencyObject) as ListBoxItem;
        var row = item?.DataContext as ExplorerRow;
        // Apps / Shift+F10 can originate on the list itself. A mouse click on
        // empty space must never inherit an unrelated selected folder.
        if (row is null && e.CursorLeft < 0)
        {
            row = SpaceTree.SelectedItem as ExplorerRow;
            item = SpaceTree.ItemContainerGenerator.ContainerFromIndex(SpaceTree.SelectedIndex) as ListBoxItem;
        }
        if (row is null || !PrepareFolderMenu(SpaceTree.ContextMenu, row.Scan, row.Entry.LinkIndex, treeMenu: true))
        { e.Handled = true; return; }
        ContextMenuService.SetPlacementTarget(SpaceTree, item ?? (UIElement)SpaceTree);
    }

    private bool PrepareFolderMenu(ContextMenu menu, ScanSnapshot scan, int link, bool treeMenu = false)
    {
        if (!CanUseFolderMenu(scan)) return false;
        var entry = scan.Entry(link);
        string? path = scan.EntryPath(link);
        if (path is null || link < -1) return false;
        if (!SelectContextEntry(scan, link, preserveSelection: treeMenu)) return false;
        var selection = treeMenu ? SelectedTreeRows() : [];
        bool batch = selection.Length > 1;
        menu.Items.Clear();
        menu.FontFamily = FontFamily; menu.FontSize = FontSize;
        if (entry.IsDirectory)
        {
            var open = new MenuItem { Header = "Open in File Explorer" };
            open.Click += (_, _) =>
            {
                if (CanUseFolderMenu(scan)) OpenFolderInExplorer(path);
            };
            menu.Items.Add(open);
        }
        if (entry.IsDirectory || batch)
        {
            int[] targets = batch ? RecycleLinksInTreeOrder(selection) : [link];
            string? recycleRoot = !batch ? RecycleBinService.RootForFolder(path) : null;
            string? blocked = batch ? _recycleCheck is not null ? "Finish the current recycle confirmation first."
                : selection.Any(row => row.Entry.IsDirectory) ? null : "Select folders to recycle."
                : recycleRoot is not null ? null : RecycleBlockReason(scan, link);
            var recycle = new MenuItem
            {
                Header = batch ? "Move selected folders to Recycle Bin…" : recycleRoot is null ? "Move to Recycle Bin" : "Empty Recycle Bin…",
                IsEnabled = blocked is null,
                ToolTip = recycleRoot is null ? blocked : $"Permanently deletes items Windows lists in your Recycle Bin on {recycleRoot}"
            };
            ToolTipService.SetShowOnDisabled(recycle, true);
            recycle.Click += async (_, _) =>
            {
                if (!CanUseFolderMenu(scan)) return;
                if (recycleRoot is not null) await EmptyRecycleBinAsync(recycleRoot);
                else await DeleteFoldersAsync(scan, targets);
            };
            menu.Items.Add(recycle);
        }
        if (entry.IsDirectory)
        {
            var recheck = new MenuItem { Header = "Recheck this folder" };
            recheck.Click += async (_, _) => { if (CanUseFolderMenu(scan)) await RecheckFolderAsync(path); };
            menu.Items.Add(recheck);
            if (entry.Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                && RecycleBinService.RootForFolder(path) is { } leftoverRoot)
            {
                var leftovers = new MenuItem { Header = "Review Recycle Bin leftovers…" };
                leftovers.Click += async (_, _) => { if (CanUseFolderMenu(scan)) await ReviewRecycleLeftoversAsync(leftoverRoot); };
                menu.Items.Add(leftovers);
            }
        }
        var checking = new MenuItem { Header = "Checking cloud availability…", IsEnabled = false };
        menu.Items.Add(checking);
        var marker = new object(); menu.Tag = marker;
        _activeFolderMenu = menu;
        menu.Closed -= FolderMenuClosed;
        menu.Closed += FolderMenuClosed;
        _ = AddCloudMenuAsync(menu, checking, marker, scan, link, path);
        return true;
    }

    private async Task AddCloudMenuAsync(ContextMenu menu, MenuItem checking, object marker, ScanSnapshot scan, int link, string path)
    {
        var state = await Operations.InspectCloudAsync(path);
        if (!CanUseFolderMenu(scan) || !ReferenceEquals(menu.Tag, marker)) return;
        menu.Items.Remove(checking);
        if (state is not null)
        {
            bool enabled = state.IsDirectory || state.CanRequestOnlineOnly;
            var online = new MenuItem { Header = "Make online-only", IsEnabled = enabled,
                ToolTip = enabled ? $"Managed by {state.Provider}"
                    : state.LocalBytes == 0 ? "This file is already online-only." : "This file is unsynced or excluded by the provider." };
            ToolTipService.SetShowOnDisabled(online, true);
            online.Click += async (_, _) => { if (CanUseFolderMenu(scan)) await MakeOnlineOnlyAsync(scan, link); };
            menu.Items.Add(online);
        }
        if (menu.Items.Count == 0) menu.IsOpen = false;
    }

    private bool CanUseFolderMenu(ScanSnapshot scan) => !_closing && _work is null && !_browsing
        && ReferenceEquals(_snapshot, scan);

    private bool SelectContextEntry(ScanSnapshot scan, int link, bool preserveSelection = false)
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
        if (preserveSelection && SpaceTree.SelectedItems.OfType<ExplorerRow>().Any(row => row.Entry.LinkIndex == link))
        {
            _selectedLink = link;
            _selectionIsGroup = false;
            _explorerRevision++;
        }
        else SelectTree(link);
        EntryDetail.DataContext = new ExplorerRow(scan, scan.Entry(link), 0, _explorer!.IsExpanded(link))
        { PreferLogicalSize = _explorer.PreferLogicalSize };
        EntryDetail.Visibility = Visibility.Visible;
        SelectedEntryPath.Visibility = scan.EntryPath(link) == _currentPath ? Visibility.Collapsed : Visibility.Visible;
        return true;
    }
}
