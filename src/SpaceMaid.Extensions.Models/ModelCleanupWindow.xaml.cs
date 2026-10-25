using System.Windows;

namespace SpaceMaid.Extensions.Models;

public partial class ModelCleanupWindow : Window
{
    internal ModelCleanupWindow(OllamaCleanupPlan plan)
    {
        InitializeComponent(); DataContext = plan;
        SetResourceReference(BackgroundProperty, "WindowBackground"); SetResourceReference(ForegroundProperty, "WindowForeground");
        Loaded += (_, _) => CancelButton.Focus();
    }
    private void Delete_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
