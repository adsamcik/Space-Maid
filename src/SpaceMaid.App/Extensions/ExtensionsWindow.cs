using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpaceMaid.App.Extensions;

internal sealed class ExtensionsWindow : Window
{
    private readonly ExtensionRuntime _runtime;
    private readonly StackPanel _list = new() { Margin = new(0, 14, 0, 14) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    internal ExtensionsWindow(ExtensionRuntime runtime)
    {
        _runtime = runtime;
        Title = "Extensions"; Width = 680; Height = 500; MinWidth = 520; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackground"); SetResourceReference(ForegroundProperty, "WindowForeground");
        var panel = new DockPanel { Margin = new(24) };
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "Extensions", FontSize = 23, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Only add extensions you trust. Restart to apply changes.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0), Opacity = .75,
            ToolTip = "Extensions run with your account's permissions. Only add packages you trust." });
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var footer = new StackPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var add = new Button { Content = "Add extension", Margin = new(0, 0, 10, 0), Padding = new(12, 6, 12, 6), IsEnabled = !runtime.IsAdministrator };
        add.Click += Add_Click; buttons.Children.Add(add);
        var close = new Button { Content = "Close", IsCancel = true, Padding = new(20, 6, 20, 6) }; close.Click += (_, _) => Close(); buttons.Children.Add(close);
        footer.Children.Add(buttons); footer.Children.Add(_status); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        panel.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
        foreach (var entry in runtime.Entries) AddRow(entry);
        if (runtime.Entries.Count == 0) _list.Children.Add(new TextBlock { Text = "No extensions found." });
        _status.Text = runtime.IsAdministrator ? "Reopen Space Maid without administrator access to use extensions." : runtime.SettingsError ?? "";
    }
    private void AddRow(LoadedExtension entry)
    {
        var panel = new StackPanel { Margin = new(0, 0, 0, 14) };
        var enabled = new CheckBox { Content = entry.Name, IsChecked = _runtime.IsEnabled(entry), FontWeight = FontWeights.SemiBold, IsEnabled = !_runtime.IsAdministrator };
        enabled.Click += (_, _) =>
        {
            try { _runtime.SetEnabled(entry, enabled.IsChecked == true); _status.Text = "Saved · Restart to apply"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { enabled.IsChecked = _runtime.IsEnabled(entry); _status.Text = "Settings were not saved: " + ex.Message; }
        };
        panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock { Text = entry.Status + (entry.Error is null ? "" : " · " + entry.Error), TextWrapping = TextWrapping.Wrap, Margin = new(24, 4, 0, 0), Opacity = .75 });
        panel.Children.Add(new Expander { Header = "Location", Margin = new(24, 6, 0, 0), Content = new TextBox { Text = entry.Directory, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new(0), Background = System.Windows.Media.Brushes.Transparent, Margin = new(0, 6, 0, 0), FontSize = 12 } });
        if (entry.View is { } view && entry.Id != "space-maid.photos")
        {
            var open = new Button { Content = "Open workflow", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(24, 6, 0, 0), Padding = new(12, 6, 12, 6) };
            open.Click += (_, _) => OpenWorkflow(entry.Name, view);
            panel.Children.Add(open);
        }
        _list.Children.Add(panel);
    }
    private void OpenWorkflow(string name, FrameworkElement view)
    {
        if (view.Parent is not null) { _status.Text = "This workflow is already open."; return; }
        var window = new Window
        {
            Title = name, Owner = this, Content = view, Width = 1050, Height = 720, MinWidth = 640, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily, FontSize = FontSize
        };
        window.SetResourceReference(BackgroundProperty, "WindowBackground"); window.SetResourceReference(ForegroundProperty, "WindowForeground");
        // A plugin owns one view for the session. Detach it after closing so
        // the user can reopen it without creating another plugin/catalog.
        try { window.ShowDialog(); } finally { window.Content = null; }
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose extension folder", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        if (_runtime.TryAddTrustedDirectory(picker.FolderName, out var manifest, out var error))
        {
            var added = manifest!;
            AddRow(new(added.Id, added.Name, picker.FolderName, "Enabled · available after restart"));
            _status.Text = "Added · Restart to load";
        }
        else _status.Text = "The directory was not added: " + error;
    }
}
