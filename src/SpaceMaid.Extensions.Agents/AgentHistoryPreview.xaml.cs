using System.Windows;

namespace SpaceMaid.Extensions.Agents;

public partial class AgentHistoryPreview : Window
{
    internal AgentHistoryPreview(AgentHistoryPlan plan)
    {
        InitializeComponent(); DataContext = plan;
        SetResourceReference(BackgroundProperty, "WindowBackground"); SetResourceReference(ForegroundProperty, "WindowForeground");
        Loaded += (_, _) => CancelButton.Focus();
    }
    private void Acknowledgement_Changed(object sender, RoutedEventArgs e)
    { if (RecycleButton is not null) RecycleButton.IsEnabled = StoppedAcknowledgement.IsChecked == true; }
    private void Recycle_Click(object sender, RoutedEventArgs e)
    { if (StoppedAcknowledgement.IsChecked == true) DialogResult = true; }
}
