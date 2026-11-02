using System.Windows;
using System.Windows.Automation;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private bool _sidebarCollapsed;

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
