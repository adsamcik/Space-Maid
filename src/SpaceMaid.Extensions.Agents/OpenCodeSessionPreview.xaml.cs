using System.Windows;

namespace SpaceMaid.Extensions.Agents;

public partial class OpenCodeSessionPreview : Window
{
    internal OpenCodeSessionPreview(OpenCodeSessionPlan plan)
    {
        InitializeComponent(); DataContext = plan;
        SetResourceReference(BackgroundProperty, "WindowBackground"); SetResourceReference(ForegroundProperty, "WindowForeground");
        Loaded += (_, _) => CancelButton.Focus();
    }
    private void Acknowledgement_Changed(object sender, RoutedEventArgs e)
    { if (DeleteButton is not null) DeleteButton.IsEnabled = PausedAcknowledgement.IsChecked == true; }
    private void Delete_Click(object sender, RoutedEventArgs e)
    { if (PausedAcknowledgement.IsChecked == true) DialogResult = true; }
}
