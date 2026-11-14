using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private void UpdateExplorerActions()
    {
        if (SelectionActions is null) return;
        if (RetryRecycleButton is { } retry)
        {
            retry.Visibility = CanRetryRecycleWithAdministrator ? Visibility.Visible : Visibility.Collapsed;
            retry.ToolTip = AdministratorRetryTarget?.Path;
        }
        var selected = SelectedTreeRows();
        bool single = selected.Length == 1 && !_selectionIsGroup;
        var row = single ? selected[0] : null;
        bool current = _snapshot is { } scan && selected.Length > 0
            && selected.All(item => ReferenceEquals(item.Scan, scan));
        bool available = current && CanUseFolderMenu(_snapshot!);
        string? binRoot = row is { Entry.IsDirectory: true } ? RecycleBinService.RootForFolder(row.Scan.EntryPath(row.Entry.LinkIndex)!) : null;
        string? blocked = _selectionIsGroup ? "Select an individual folder to act on it."
            : _foregroundBusy || _work is not null ? "Updating results. File actions will be available when this finishes."
            : _recycleCheck is not null ? "Finish the current recycle confirmation first."
            : row is { Entry.IsDirectory: true } && binRoot is null ? RecycleBlockReason(row.Scan, row.Entry.LinkIndex)
            : null;
        SelectionTitle.Text = _selectionIsGroup ? "Grouped entries" : selected.Length > 1 ? $"{selected.Length:N0} items selected" : row?.Name ?? "Selected folder";
        SelectionDetails.Visibility = selected.Length == 1 ? Visibility.Visible : Visibility.Collapsed;
        EntryDetail.Visibility = selected.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenSelectionButton.IsEnabled = available && single;
        SelectionMoreButton.IsEnabled = available && !_selectionIsGroup;
        RecycleSelectionButton.Content = binRoot is not null ? "Empty Recycle Bin…"
            : selected.Length > 1 ? "Recycle selected folders…" : "Move to Recycle Bin…";
        RecycleSelectionButton.Visibility = selected.Any(item => item.Entry.IsDirectory) ? Visibility.Visible : Visibility.Collapsed;
        RecycleSelectionButton.IsEnabled = available && blocked is null && selected.Any(item => item.Entry.IsDirectory);
        RecycleSelectionButton.ToolTip = blocked ?? (binRoot is not null
            ? "Review permanently emptying Windows-listed items for your account on this drive."
            : "Review the selected folders before moving them to the Recycle Bin.");
        SelectionActionHint.Text = blocked ?? "";
        SelectionActionHint.Visibility = blocked is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not { } scan || !CanUseFolderMenu(scan) || _selectionIsGroup) return;
        var selected = SelectedTreeRows();
        if (selected.Length != 1 || !ReferenceEquals(selected[0].Scan, scan)
            || scan.EntryPath(selected[0].Entry.LinkIndex) is not { } path) return;
        try { _explorerRecheckPath = Operations.OpenInExplorer(path); }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        { StatusText.Text = "Could not open this location. Refresh the folder and try again. " + error.Message; }
    }

    private async void RecycleSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not { } scan || !CanUseFolderMenu(scan) || _selectionIsGroup) return;
        var selected = SelectedTreeRows();
        if (selected.Length == 1 && selected[0].Entry.IsDirectory
            && RecycleBinService.RootForFolder(scan.EntryPath(selected[0].Entry.LinkIndex)!) is { } root)
            await EmptyRecycleBinAsync(root);
        else await RecycleSelectedFoldersAsync();
    }

    private void SelectionMore_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTreeRows();
        if (_selectionIsGroup || selected.Length == 0) return;
        var menu = SelectionMoreButton.ContextMenu;
        if (!PrepareFolderMenu(menu, selected[0].Scan, selected[0].Entry.LinkIndex, treeMenu: true)) return;
        menu.PlacementTarget = SelectionMoreButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }
}
