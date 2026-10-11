using System.Windows;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class OnlineOnlyWindow : Window
{
    public OnlineOnlyWindow(OnlineOnlyPlan plan)
    {
        InitializeComponent();
        TargetPath.Text = plan.Path;
        ProviderText.Text = plan.Provider;
        PlanText.Text = $"{plan.EligibleCount:N0} files · {SizeText.Format(plan.LocalBytes)} local · {plan.SkippedCount:N0} skipped";
        WarningsText.Text = string.Join("\n", plan.Warnings);
        ConfirmButton.IsEnabled = plan.EligibleCount > 0;
    }
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
