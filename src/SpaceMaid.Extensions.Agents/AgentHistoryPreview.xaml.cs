using System.Windows;

namespace SpaceMaid.Extensions.Agents;

public partial class AgentHistoryPreview : Window
{
    internal AgentHistoryPreview(AgentHistoryPlan plan) : this(plan, administratorRetry: false) { }

    internal AgentHistoryPreview(AgentHistoryPlan plan, bool administratorRetry)
    {
        InitializeComponent(); DataContext = plan;
        if (administratorRetry)
        {
            Title = "Retry recycling history folder";
            Heading.Text = "Retry moving this folder to the Recycle Bin?";
            RecycleButton.Content = "Retry with administrator permission";
        }
        SetResourceReference(BackgroundProperty, "WindowBackground"); SetResourceReference(ForegroundProperty, "WindowForeground");
        Loaded += (_, _) => CancelButton.Focus();
    }
    private void Acknowledgement_Changed(object sender, RoutedEventArgs e)
    { if (RecycleButton is not null) RecycleButton.IsEnabled = StoppedAcknowledgement.IsChecked == true; }
    private void Recycle_Click(object sender, RoutedEventArgs e)
    { if (StoppedAcknowledgement.IsChecked == true) DialogResult = true; }
}
