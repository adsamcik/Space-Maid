using System.Windows.Controls;

namespace SpaceMaid.App;

internal sealed class ExplorerListView : ListView
{
    internal void RestoreSelection(IReadOnlyList<ExplorerRow> rows) => SetSelectedItems(rows);
}
