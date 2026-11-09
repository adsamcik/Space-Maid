using System.Windows;
using System.Windows.Automation;
using System.IO;
using System.Text.Json;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private bool _sidebarCollapsed;
    private bool _sidebarReady;
    internal string SidebarSettingsPath { get; set; } = AppData.File("sidebar.json");

    private void LoadSidebarPreferences()
    {
        try
        {
            if (Operations.Files.FileExists(SidebarSettingsPath))
            {
                using var input = Operations.Files.OpenRead(SidebarSettingsPath);
                if (input.Length > 1024) throw new InvalidDataException("Sidebar preferences are too large.");
                ToolsToggle.IsChecked = JsonSerializer.Deserialize<bool>(input);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { ToolsToggle.ToolTip = "Sidebar preferences could not be loaded. Toggle Tools to save a new preference."; }
        _sidebarReady = true;
    }

    private void ToolsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ExtensionNavigation is null) return;
        bool expanded = ToolsToggle.IsChecked == true;
        ExtensionNavigation.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ToolsToggle.Content = expanded ? "Tools ▾" : "Tools ▸";
        ToolsToggle.ToolTip = expanded ? "Hide specialist tools" : "Show specialist tools";
        if (!_sidebarReady) return;
        try
        {
            Operations.Files.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(SidebarSettingsPath))!);
            string pending = SidebarSettingsPath + ".tmp";
            Operations.Files.WriteAllText(pending, JsonSerializer.Serialize(expanded));
            Operations.Files.MoveFile(pending, SidebarSettingsPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ToolsToggle.ToolTip = "Tools changed for this session. Sidebar preferences could not be saved."; }
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(!_sidebarCollapsed);

    internal void SetSidebarCollapsed(bool collapsed)
    {
        _sidebarCollapsed = collapsed;
        SidebarColumn.Width = new GridLength(collapsed ? 50 : 180);
        SidebarNavigation.SetIsCollapsed(SidebarPanel, collapsed);
        string action = collapsed ? "Expand sidebar" : "Collapse sidebar";
        SidebarToggle.ToolTip = action;
        AutomationProperties.SetName(SidebarToggle, action);
    }
}
