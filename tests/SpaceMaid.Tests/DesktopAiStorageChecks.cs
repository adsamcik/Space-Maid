using SpaceMaid.Extensions.Contracts;
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
        string[] repositories = ["models--buttercrab--dia-v1-1.6b", "models--nari-labs--Dia-1.6B-0626", "models--nari-labs--Dia-1.6B",
            "models--NbAiLab--nb-wav2vec2-1b-nynorsk", "models--jonatasgrosman--wav2vec2-large-xlsr-53-japanese",
            .. Enumerable.Range(1, 20).Select(index => $"models--example--speech-model-{index:D2}")];
        const string hub = ".cache/huggingface/hub/";
        var scan = AiStorageFixture.Inventory(AiStorageFixture.ExampleFiles.Concat(repositories.SelectMany((name, index) =>
                Enumerable.Range(1, index < 3 ? 6 : 1).Select(shard => $"{hub}{name}/snapshots/revision/model-{shard}.safetensors"))),
            repositories.Append("models--org--model").Select(name => hub + name + "/refs"));
        var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow)
            .GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var analysis = runtime.Opportunities.Analyze(scan, default);
        var load = window.InitializeExplorerAsync(scan, analysis.Findings, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        ((TextBox)OpportunityControl(window, "OpportunitySearch")).Text = "";
        ((ComboBox)OpportunityControl(window, "OpportunityTypeFilter")).SelectedIndex = 0;
        ((ComboBox)OpportunityControl(window, "OpportunityGroupingPicker")).SelectedIndex = 0;
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, analysis.Findings, analysis.Groups, null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        ((Button)OpportunityControl(window, "BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var cards = (ItemsControl)OpportunityControl(window, "CleanupGroupCards");
        var models = Assert.Single(cards.Items.Cast<OpportunityGroupRow>(), row => row.Name == "AI models & data");
        Assert.Equal(3, models.Children.Count); Assert.True(models.CanSelectGroup);
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
        var list = (ListBox)OpportunityControl(window, "OpportunityGrid");
        var row = Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>()));
        Assert.Equal("Manage in owning tool", row.Review); Assert.Contains("AI models", row.Item.SuggestedAction);
        Assert.False(row.CanSelect); Assert.False(((Button)OpportunityControl(window, "SelectListedButton")).IsEnabled);
        row.Selected = true; Assert.False(row.Selected);
        Assert.Contains("Space Maid keeps", row.Item.Caution);
        Assert.Equal("AI models & data › Ollama", ((TextBlock)OpportunityControl(window, "CleanupScopeText")).Text);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-ai-model-details.png"), 1220, 910);
        ((Button)OpportunityControl(window, "BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        models = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == "AI models & data");
        container = cards.ItemContainerGenerator.ContainerFromItem(models);
        Assert.Single(Descendants(container).OfType<Expander>(), expander => Equals(expander.Header, "By tool and project")).IsExpanded = true; window.UpdateLayout();
        var huggingFace = Assert.Single(models.Children, row => row.Name == "Hugging Face");
        Descendants(container).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, huggingFace))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(26, list.Items.Count);
        ((Button)OpportunityControl(window, "SelectListedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
        Assert.Equal("26 locations selected", ((TextBlock)OpportunityControl(window, "SelectionText")).Text);
        window.MarkScanStale("Illustrative inventory · Review rechecks selected folders");
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            string theme = mode == ThemeMode.Light ? "light" : "dark";
            foreach (var (width, height, size) in new[] { (1220, 910, "normal"), (1020, 790, "compact") })
            {
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-huggingface-{size}-{theme}.png"), width, height);
                Assert.True(list.ActualWidth > 320);
                Assert.Equal(((OpportunityRow)list.SelectedItem).Name, ((TextBlock)OpportunityControl(window, "DetailName")).Text);
                Assert.All(Descendants(list).OfType<TextBlock>().Where(text => text.Text == "Hugging Face repository downloads"),
                    text => Assert.Equal(Visibility.Collapsed, text.Visibility));
            }
        }
#pragma warning restore WPF0001
        var detailsExpander = (Expander)OpportunityControl(window, "FolderDetailsExpander");
        detailsExpander.IsExpanded = true; window.UpdateLayout();
        Assert.Contains(Descendants(detailsExpander).OfType<TextBlock>(), text => text.Text.Contains("Windows without symlinks"));
        detailsExpander.IsExpanded = false;
        var search = (TextBox)OpportunityControl(window, "OpportunitySearch");
        search.Text = "buttercrab";
        PumpUntil(() => list.Items.Count == 1, TimeSpan.FromSeconds(3));
        Assert.Contains("25 outside this view", ((TextBlock)OpportunityControl(window, "SelectionText")).Text);
        Assert.Equal("models--buttercrab--dia-v1-1.6b", Assert.IsType<OpportunityRow>(list.Items[0]).Name);
        search.Text = "no-such-model";
        PumpUntil(() => list.Items.Count == 0, TimeSpan.FromSeconds(3));
        Assert.Equal(Visibility.Visible, ((StackPanel)OpportunityControl(window, "EmptyState")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)OpportunityControl(window, "DetailPanel")).Visibility);
        search.Text = "";
        PumpUntil(() => list.Items.Count == 26, TimeSpan.FromSeconds(3));
        ((Button)OpportunityControl(window, "BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)OpportunityControl(window, "ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((ComboBox)OpportunityControl(window, "OpportunityGroupingPicker")).SelectedIndex = 1;
    }
}
