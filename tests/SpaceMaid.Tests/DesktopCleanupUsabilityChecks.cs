using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckCleanupUsability(MainWindow window, Application app, string artifactRoot)
    {
        const string hub = ".cache/huggingface/hub/";
        string[] repositories = ["datasets--zeta--speech", "models--alpha--voice",
            .. Enumerable.Range(1, 24).Select(index => $"models--example--voice-{index:D2}")];
        var scan = AiStorageFixture.Inventory(repositories.Select(name => hub + name + "/snapshots/revision/weights.bin")
            .Concat(["Code/Orbit/Orbit.csproj", "Code/Orbit/bin/Orbit.dll"]), repositories.Select(name => hub + name + "/refs"));
        var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow).GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var analysis = runtime.Opportunities.Analyze(scan, default);
        var loading = window.InitializeExplorerAsync(scan, analysis.Findings, default);
        PumpUntil(() => loading.IsCompleted, TimeSpan.FromSeconds(5)); loading.GetAwaiter().GetResult();
        var grouping = (ComboBox)OpportunityControl(window, "OpportunityGroupingPicker");
        var sort = (ComboBox)OpportunityControl(window, "OpportunitySortPicker");
        var search = (TextBox)OpportunityControl(window, "OpportunitySearch");
        var list = (ListBox)OpportunityControl(window, "OpportunityGrid");
        var navigation = (ListBox)OpportunityControl(window, "OpportunityGroupList");
        var cards = (ItemsControl)OpportunityControl(window, "CleanupGroupCards");
        var selectedView = (ToggleButton)OpportunityControl(window, "SelectedOnlyToggle");
        grouping.SelectedIndex = 0; search.Text = "";
        ((ComboBox)OpportunityControl(window, "OpportunityTypeFilter")).SelectedIndex = 0;
        var publish = typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!;
        publish.Invoke(window, [scan, analysis.Findings, analysis.Groups, null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        Assert.Equal("Cleanup", ((RadioButton)window.FindName("OpportunitiesTab")).Content);
        OpenRepositories();
        sort.SelectedIndex = 1;
        Assert.Equal("alpha/voice", Assert.IsType<OpportunityRow>(list.Items[0]).Name);
        Assert.Equal("zeta/speech", Assert.IsType<OpportunityRow>(list.Items[^1]).Name);
        var model = Assert.IsType<OpportunityRow>(list.Items[0]); model.Selected = true;
        grouping.SelectedIndex = 1;
        navigation.SelectedItem = navigation.Items.Cast<OpportunityGroupRow>().Single(group => group.Matches.Any(row => row.Item.IsBuildOutput));
        var build = Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>())); build.Selected = true;
        search.Text = "Orbit";
        PumpUntil(() => list.Items.Count == 1, TimeSpan.FromSeconds(3));
        selectedView.IsChecked = true; selectedView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, list.Items.Count);
        Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
        Assert.Equal("Selected folders", ((TextBlock)OpportunityControl(window, "OpportunityHeading")).Text);
        selectedView.IsChecked = false; selectedView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Orbit", search.Text); Assert.Single(list.Items);
        Assert.Same(build, list.SelectedItem);
        var rules = (ToggleButton)OpportunityControl(window, "BuildRulesToggle");
        Assert.Equal(Visibility.Visible, rules.Visibility);
        rules.IsChecked = true; window.UpdateLayout();
        Assert.Equal(Visibility.Visible, ((Border)OpportunityControl(window, "BuildRulesPanel")).Visibility);
        rules.IsChecked = false;
        ((Button)OpportunityControl(window, "ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(model.Selected); Assert.False(build.Selected);

        grouping.SelectedIndex = 0;
        OpenRepositories();
        search.Text = "example";
        PumpUntil(() => list.Items.Count == 24, TimeSpan.FromSeconds(3));
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-browse-context.png"), 1020, 790);
        list.SelectedIndex = 18; list.ScrollIntoView(list.SelectedItem); window.UpdateLayout();
        string highlighted = Assert.IsType<OpportunityRow>(list.SelectedItem).Path;
        var scroll = Descendants(list).OfType<ScrollViewer>().First();
        double offset = scroll.VerticalOffset; Assert.True(offset > 0);
        ((Button)OpportunityControl(window, "BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        OpenRepositories();
        Assert.Equal("example", search.Text);
        Assert.Equal(highlighted, Assert.IsType<OpportunityRow>(list.SelectedItem).Path);
        PumpUntil(() => Math.Abs(Descendants(list).OfType<ScrollViewer>().First().VerticalOffset - offset) < 0.1, TimeSpan.FromSeconds(3));
        Assert.Equal(Visibility.Collapsed, ((ToggleButton)OpportunityControl(window, "BuildRulesToggle")).Visibility);
        ((ToggleButton)OpportunityControl(window, "ViewOptionsToggle")).IsChecked = true; window.UpdateLayout();
        Assert.DoesNotContain(Descendants((Border)OpportunityControl(window, "ViewOptionsPanel")).OfType<TextBox>(),
            box => box.Name == "BuildKeepRecentDaysBox");
        ((ToggleButton)OpportunityControl(window, "ViewOptionsToggle")).IsChecked = false;

        Assert.IsType<OpportunityRow>(list.SelectedItem).Selected = true;
        selectedView.IsChecked = true; selectedView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(list.Items);
#pragma warning disable WPF0001
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            string suffix = theme == ThemeMode.Light ? "light" : "dark";
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-selected-compact-{suffix}.png"), 1020, 790);
        }
#pragma warning restore WPF0001
        ((Button)OpportunityControl(window, "ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(list.Items); Assert.False(((Button)OpportunityControl(window, "ReviewButton")).IsEnabled);
        selectedView.IsChecked = false; selectedView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)OpportunityControl(window, "BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var remainingRepositories = repositories.Where(name => AiStorageFixture.Full(hub + name) != highlighted).ToArray();
        var refreshed = AiStorageFixture.Inventory(remainingRepositories.Select(name => hub + name + "/snapshots/revision/weights.bin")
            .Concat(["Code/Orbit/Orbit.csproj", "Code/Orbit/bin/Orbit.dll"]), remainingRepositories.Select(name => hub + name + "/refs"));
        var refreshedAnalysis = runtime.Opportunities.Analyze(refreshed, default);
        loading = window.InitializeExplorerAsync(refreshed, refreshedAnalysis.Findings, default);
        PumpUntil(() => loading.IsCompleted, TimeSpan.FromSeconds(5)); loading.GetAwaiter().GetResult();
        publish.Invoke(window, [refreshed, refreshedAnalysis.Findings, refreshedAnalysis.Groups, null, null, false]);
        OpenRepositories();
        Assert.Equal("", search.Text);
        Assert.DoesNotContain(list.Items.Cast<OpportunityRow>(), row => row.Path == highlighted || row.Selected);
        ((Button)OpportunityControl(window, "BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        sort.SelectedIndex = 0;

        void OpenRepositories()
        {
            window.UpdateLayout();
            var root = cards.Items.Cast<OpportunityGroupRow>().Single(group => group.Name == "AI models & data");
            var container = cards.ItemContainerGenerator.ContainerFromItem(root);
            Descendants(container).OfType<Expander>().Single(expander => expander.Name == "CleanupBreakdownExpander").IsExpanded = true;
            window.UpdateLayout();
            var child = root.Children.Single(group => group.Name == "Hugging Face");
            Descendants(container).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, child)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
