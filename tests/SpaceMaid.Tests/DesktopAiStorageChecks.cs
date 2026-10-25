using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckAiStorage(MainWindow window, Application app, string artifactRoot)
    {
        var scan = AiStorageFixture.Inventory(AiStorageFixture.ExampleFiles, [".cache/huggingface/hub/models--org--model/refs"]);
        var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow)
            .GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var analysis = runtime.Opportunities.Analyze(scan, default);
        var load = window.InitializeExplorerAsync(scan, analysis.Findings, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        ((TextBox)window.FindName("OpportunitySearch")).Text = "";
        ((ComboBox)window.FindName("OpportunityTypeFilter")).SelectedIndex = 0;
        ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex = 0;
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, analysis.Findings, analysis.Groups, null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var cards = (ItemsControl)window.FindName("CleanupGroupCards");
        var models = Assert.Single(cards.Items.Cast<OpportunityGroupRow>(), row => row.Name == "AI models & data");
        Assert.Equal(3, models.Children.Count); Assert.False(models.CanSelectGroup);
        Assert.Contains(cards.Items.Cast<OpportunityGroupRow>(), row => row.Name == "Agent file checkpoints");
        ((TextBlock)window.FindName("StatusText")).Text = "Illustrative inventory · AI tools and local models";
        ((TextBlock)window.FindName("ScannedSizeText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("ScannedSizeCaption")).Text = "On disk";
        ((TextBlock)window.FindName("CandidateText")).Text = SizeText.Format(new FileOperations().EstimateCleanup(scan, analysis.Findings).EstimatedBytes);
        ((TextBlock)window.FindName("CandidateCaption")).Text = $"Observed · {analysis.Findings.Count} folders";
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-ai-{(mode == ThemeMode.Light ? "light" : "dark")}.png"), 1220, 910);
        }
#pragma warning restore WPF0001
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-ai-compact.png"), 1020, 790);
        window.UpdateLayout();
        var container = cards.ItemContainerGenerator.ContainerFromItem(models);
        Assert.Single(Descendants(container).OfType<Expander>()).IsExpanded = true; window.UpdateLayout();
        var ollama = Assert.Single(models.Children, row => row.Name == "Ollama");
        Assert.False(ollama.CanSelectGroup);
        Descendants(container).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, ollama))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var list = (ListBox)window.FindName("OpportunityGrid");
        var row = Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>()));
        Assert.Equal("Manage in owning tool", row.Review); Assert.Contains("ollama list", row.Item.SuggestedAction);
        Assert.False(row.CanSelect); Assert.False(((Button)window.FindName("SelectListedButton")).IsEnabled);
        row.Selected = true; Assert.False(row.Selected);
        Assert.Contains("Space Maid keeps", row.Item.Caution);
        Assert.Equal("AI models & data › Ollama", ((TextBlock)window.FindName("CleanupScopeText")).Text);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-ai-model-details.png"), 1220, 910);
        ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex = 1;
    }
}
