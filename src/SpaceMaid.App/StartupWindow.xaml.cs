using System.Windows;

namespace SpaceMaid.App;

public partial class StartupWindow : Window
{
    public StartupWindow() => InitializeComponent();
    private void Enable_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
