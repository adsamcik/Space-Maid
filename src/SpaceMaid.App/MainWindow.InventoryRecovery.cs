using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private ScanSnapshot? _fullRescanRecovery;

    internal void OfferFullInventoryRescan(ScanSnapshot scan)
    {
        if (!ReferenceEquals(_snapshot, scan)) return;
        _fullRescanRecovery = scan;
        ScanButton.Content = "Rescan inventory";
        ScanButton.ToolTip = "Refresh the scanned inventory; completed file operations will not be repeated.";
        TreeNote.Text = "OUT OF DATE · Rescan inventory";
    }

    private void ClearFullInventoryRescan()
    {
        _fullRescanRecovery = null;
        ScanButton.Content = "Scan";
        ScanButton.ToolTip = null;
    }
}
