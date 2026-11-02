using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckSidebar(MainWindow window, Application app, string artifactRoot)
    {
        var sidebar = (DockPanel)window.FindName("SidebarPanel");
        var toggle = (Button)window.FindName("SidebarToggle");
        var more = (Button)window.FindName("MoreButton");
        var optimize = (Button)window.FindName("OptimizationButton");
        var extensions = (RadioButton)window.FindName("ExtensionsTab");
        Assert.True(toggle.IsTabStop);
        Assert.True(more.IsTabStop);
        var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow).GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        Assert.All(runtime.Entries, entry => { Assert.Null(entry.Error); Assert.NotNull(entry.NavigationIcon); Assert.True(entry.NavigationIcon.IsFrozen); });
        Assert.DoesNotContain(Descendants(sidebar).OfType<TextBlock>(), block => block.Text == "Space Maid");
        extensions.IsChecked = true;
        window.UpdateLayout();
        var manager = Assert.IsType<ExtensionsView>(((ContentControl)window.FindName("ExtensionManagerContent")).Content);
        var search = (TextBox)manager.FindName("SearchBox");
        var list = (ListBox)manager.FindName("ExtensionList");
        string previousSearch = search.Text;
        search.Text = "agent";
        var selected = list.SelectedItem;
        var links = Descendants(sidebar).OfType<RadioButton>().ToArray();
        Assert.True(links.Length >= 10);
        Assert.All(links, link =>
        {
            Assert.NotNull(SidebarNavigation.GetIcon(link));
            Assert.Equal(link.Content, AutomationProperties.GetName(link));
            Assert.Equal(link.Content, link.ToolTip);
            Assert.True(link.IsTabStop);
        });

        void CheckLayout(bool collapsed)
        {
            window.UpdateLayout();
            Assert.Equal(collapsed ? "Expand sidebar" : "Collapse sidebar", AutomationProperties.GetName(toggle));
            Assert.Equal(AutomationProperties.GetName(toggle), toggle.ToolTip);
            Assert.Equal(collapsed, SidebarNavigation.GetIsCollapsed(sidebar));
            Assert.True(extensions.IsChecked);
            Assert.Equal("agent", search.Text);
            Assert.Same(selected, list.SelectedItem);
            foreach (var link in links)
            {
                var label = Assert.Single(Descendants(link).OfType<TextBlock>(), block => block.Text == (string)link.Content);
                Assert.Equal(collapsed ? Visibility.Collapsed : Visibility.Visible, label.Visibility);
                var glyph = Assert.Single(Descendants(link).OfType<Rectangle>(), rect => rect.OpacityMask is ImageBrush);
                Assert.Same(SidebarNavigation.GetIcon(link), ((ImageBrush)glyph.OpacityMask).ImageSource);
                Assert.InRange(glyph.ActualWidth, 21, 23);
                Assert.True(glyph.IsVisible);
                Assert.True(link.ActualWidth >= glyph.ActualWidth + 20);
            }
            Assert.True(ToolTipService.GetShowOnDisabled(optimize));
            Assert.NotNull(optimize.ToolTip);
            if (collapsed)
                Assert.InRange(Math.Abs(more.ActualWidth - links[0].ActualWidth), 0, 1);
        }

        try
        {
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme;
                foreach (var collapsed in new[] { false, true })
                {
                    if (SidebarNavigation.GetIsCollapsed(sidebar) != collapsed)
                        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    foreach (var size in new[] { (1220, 940), (1020, 820) })
                    {
                        Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"sidebar-{theme}-{(collapsed ? "collapsed" : "expanded")}-{size.Item1}.png"), size.Item1, size.Item2);
                        CheckLayout(collapsed);
                        Assert.True(((FrameworkElement)window.FindName("ExtensionManagerContent")).ActualWidth > 750);
                    }
                }
            }
#pragma warning restore WPF0001
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(more.ContextMenu!.IsOpen);
            Assert.Same(more, more.ContextMenu.PlacementTarget);
            more.ContextMenu.IsOpen = false;
            Assert.True(window.OpenExtension("space-maid.agents"));
            window.UpdateLayout();
            var agent = runtime.Entries.Single(entry => entry.Id == "space-maid.agents").View!;
            var parent = agent.Parent;
            Assert.True(agent.IsVisible);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(agent.IsVisible);
            Assert.Same(parent, agent.Parent);
            Assert.False(SidebarNavigation.GetIsCollapsed(sidebar));
        }
        finally
        {
            search.Text = previousSearch;
            window.SetSidebarCollapsed(false);
            ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        }
    }
}
