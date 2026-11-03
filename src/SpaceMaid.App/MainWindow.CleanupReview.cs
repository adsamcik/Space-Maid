using System.Windows;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private async void Review_Click(object sender, RoutedEventArgs e) => await ReviewSelectionAsync();

    internal async Task ReviewSelectionAsync()
    {
        if (_snapshot is not { } scan || _work is not null) return;
        if (!ExtensionsReady) await InitializeExtensionsAsync();
        if (_closing) return;
        InitializeCleanupWorkspace();
        if (_cleanupWorkspace is null) { StatusText.Text = "Cleanup is unavailable. Open Extensions to check its providers."; return; }
        var scopes = _rows.Where(row => row.Selected).Select(row => row.Path).ToArray();
        OpportunitiesTab.IsChecked = true; ShowCleanupWorkspace();
        _cleanupWorkspace.ReviewSelection(scan, _rows.Select(row => row.Item).ToArray(), scopes);
    }
}
