using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckRuleCleanupRun(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        var old = CleanupRulePlanTests.Project(fixture.Path, "old", 60);
        var newer = CleanupRulePlanTests.Project(fixture.Path, "newer", 10);
        var recent = CleanupRulePlanTests.Project(fixture.Path, "recent", 0);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(5)); var scan = scanning.GetAwaiter().GetResult();
        var findings = DeveloperOpportunityFixture.Find(scan);
        var loading = window.InitializeExplorerAsync(scan, findings, default);
        PumpUntil(() => loading.IsCompleted, TimeSpan.FromSeconds(5)); loading.GetAwaiter().GetResult();
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, findings, OpportunityGrouping.Build(scan, findings), null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        ((TextBox)OpportunityControl(window, "BuildKeepRecentDaysBox")).Text = "3"; Assert.True(window.SaveBuildRetention());
        Exception? failure = null; bool inspected = false; CleanupBatchResult? result = null;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            var dialog = app.Windows.OfType<ReviewWindow>().FirstOrDefault(candidate => candidate.IsVisible);
            if (dialog is null || inspected) return;
            inspected = true;
            try
            {
                var action = (Button)dialog.FindName("CleanupButton");
                PumpUntil(() => action.IsEnabled, TimeSpan.FromSeconds(5)); dialog.UpdateLayout();
                var rules = (ItemsControl)dialog.FindName("RuleList");
                var rule = Assert.IsType<CleanupRuleReviewRow>(Assert.Single(rules.Items.Cast<object>()));
                Assert.True(rule.Checked); Assert.Equal(0, rule.BuildActionIndex); Assert.Contains("2 included", rule.Effect);
                Assert.Equal(Visibility.Collapsed, ((Grid)dialog.FindName("FolderBrowser")).Visibility);
                var days = Descendants(rules).OfType<TextBox>().Single();
                var mode = Descendants(rules).OfType<ComboBox>().Single();
                days.Text = "30"; WaitForPlan(); Assert.Contains("1 included", rule.Effect);
                mode.SelectedIndex = 1; WaitForPlan();
                Assert.Equal(Visibility.Visible, ((TextBlock)dialog.FindName("PermanentWarning")).Visibility);
                Assert.Contains("permanently", action.Content.ToString());
                Assert.DoesNotContain("Folders are moved to the Recycle Bin only", ((TextBlock)dialog.FindName("CautionText")).Text);
                ((Button)dialog.FindName("InspectFoldersButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var list = (ListBox)dialog.FindName("LocationList");
                Assert.IsType<CleanupReviewRow>(Assert.Single(list.Items.Cast<object>())).Selected = false;
                ((Button)dialog.FindName("BackToRulesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                days.Text = "7"; WaitForPlan(); Assert.Contains("1 included", rule.Effect);
                ((Button)dialog.FindName("InspectFoldersButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(list.Items.Cast<CleanupReviewRow>().Single(row => row.Path == old.Output).Selected);
                list.Items.Cast<CleanupReviewRow>().Single(row => row.Path == old.Output).Selected = true;
                ((Button)dialog.FindName("BackToRulesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                days.Text = "0"; PumpUntil(() => ((TextBlock)dialog.FindName("ResultText")).Text.Contains("1–365"), TimeSpan.FromSeconds(5));
                Assert.False(action.IsEnabled);
                days.Text = "30"; WaitForPlan();
#pragma warning disable WPF0001
                foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    app.ThemeMode = theme; string suffix = theme == ThemeMode.Light ? "light" : "dark";
                    Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-rules-{suffix}.png"), 980, 720);
                    Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-rules-compact-{suffix}.png"), 760, 520);
                }
#pragma warning restore WPF0001
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => dialog.Result is not null, TimeSpan.FromSeconds(20)); result = dialog.Result;
                Assert.Equal(1, result!.DeletedCount); Assert.Equal(0, result.RecycledCount);
                Assert.False(Directory.Exists(old.Output)); Assert.True(Directory.Exists(newer.Output)); Assert.True(Directory.Exists(recent.Output));
                Assert.Contains("1 deleted", rule.Effect);
                Assert.Equal(Visibility.Visible, ((ScrollViewer)dialog.FindName("RulesScroll")).Visibility);
                Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-rule-results.png"), 980, 720);
                void WaitForPlan() => PumpUntil(() => action.IsEnabled && ((TextBlock)dialog.FindName("EstimateText")).Text != "…", TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { failure = error; }
            finally { dialog.Close(); }
        }, Dispatcher.CurrentDispatcher);
        try
        {
            var review = window.ReviewSelectionAsync();
            PumpUntil(() => review.IsCompleted, TimeSpan.FromSeconds(40)); review.GetAwaiter().GetResult();
            if (failure is not null) throw failure;
            Assert.True(inspected); Assert.NotNull(result);
            foreach (var project in new[] { old, newer, recent }) Assert.Equal("authored source", File.ReadAllText(project.Source));
            string log = File.ReadAllText(Assert.IsType<string>(((Button)window.FindName("OpenLogButton")).ToolTip));
            Assert.Equal(1, log.Split('\n').Count(line => line.Contains("\"Event\":\"cleanup-confirmed\"")));
            Assert.Equal(30, window.CurrentBuildPolicy.KeepRecentDays);
        }
        finally { timer.Stop(); }
    }

    private static void CheckNestedRuleExclusions(Application app)
    {
        using var fixture = new TestDirectory();
        string environment = Directory.CreateDirectory(Path.Join(fixture.Path, ".venv")).FullName;
        File.WriteAllText(Path.Join(environment, "pyvenv.cfg"), "home = fixture");
        string cache = Directory.CreateDirectory(Path.Join(environment, "__pycache__")).FullName;
        File.WriteAllText(Path.Join(cache, "module.pyc"), "owned cache");
        CleanupRulePlanTests.Project(fixture.Path, "build", 60);
        BuildActivityFixture.AgeProject(fixture.Path, 60);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(5)); var scan = scanning.GetAwaiter().GetResult();
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, DeveloperOpportunityFixture.Find(scan), discover: DeveloperOpportunityFixture.Find);
        var plan = CleanupPlanner.ReviewRules(basis, new(14), BuildCleanupAction.Recycle, DeveloperOpportunityFixture.Find);
        CleanupPlan? chosen = null; int executions = 0;
        var dialog = new ReviewWindow(plan, "Owned fixtures · simulated grouped results", (approved, _, _) =>
        {
            executions++; chosen = approved;
            return Task.FromResult(new CleanupBatchResult(approved.Preview.Locations.Select(item =>
                new CleanupLocationResult(item, CleanupOutcome.Recycled, "Simulated ancestor result")).ToArray(), null));
        });
        dialog.EnableRuleReview(basis, DeveloperOpportunityFixture.Find, false);
        dialog.ShowActivated = false; dialog.ShowInTaskbar = false; dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000; dialog.Top = -10000; dialog.Show();
        try
        {
            var action = (Button)dialog.FindName("CleanupButton");
            PumpUntil(() => action.IsEnabled, TimeSpan.FromSeconds(5));
            var rules = ((ItemsControl)dialog.FindName("RuleList")).Items.Cast<CleanupRuleReviewRow>().ToArray();
            var parent = rules.Single(rule => rule.Title.Contains("environment", StringComparison.OrdinalIgnoreCase));
            parent.Checked = true; PumpUntil(() => action.IsEnabled, TimeSpan.FromSeconds(5));
            ((Button)dialog.FindName("InspectFoldersButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var list = (ListBox)dialog.FindName("LocationList");
            list.Items.Cast<CleanupReviewRow>().Single(row => row.Path == cache).Selected = false;
            ((RadioButton)dialog.FindName("ManualFilter")).IsChecked = true;
            Assert.False(list.Items.Cast<CleanupReviewRow>().Single(row => row.Path == environment).Selected);
            ((RadioButton)dialog.FindName("ReadyFilter")).IsChecked = true;
            ((CheckBox)dialog.FindName("VisibleSelection")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((CheckBox)dialog.FindName("VisibleSelection")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)dialog.FindName("BackToRulesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            rules.Single(rule => rule.IsBuildRule).KeepDays = "30";
            PumpUntil(() => ((TextBlock)dialog.FindName("EstimateText")).Text != "…", TimeSpan.FromSeconds(5));
            Assert.False(action.IsEnabled); // Explicit bulk exclusions survive the rule edit.
            rules.Single(rule => rule.IsBuildRule).Checked = true;
            ((Button)dialog.FindName("InspectFoldersButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            foreach (var row in list.Items.Cast<CleanupReviewRow>()) row.Selected = true;
            ((RadioButton)dialog.FindName("ManualFilter")).IsChecked = true;
            list.Items.Cast<CleanupReviewRow>().Single(row => row.Path == environment).Selected = true;
            ((Button)dialog.FindName("BackToRulesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => action.IsEnabled, TimeSpan.FromSeconds(5));
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => dialog.Result is not null, TimeSpan.FromSeconds(5));
            Assert.Equal(1, executions); Assert.NotNull(chosen);
            Assert.Contains(chosen.Targets, target => target.Path == environment);
            Assert.DoesNotContain(chosen.Targets, target => target.Path == cache);
            ((RadioButton)dialog.FindName("ReadyFilter")).IsChecked = true;
            Assert.True(list.Items.Cast<CleanupReviewRow>().Single(row => row.Path == cache).IsRecycled);
            Assert.Equal("owned cache", File.ReadAllText(Path.Join(cache, "module.pyc"))); // Simulation only.
        }
        finally { dialog.Close(); }
    }

    private static void CheckManyCleanupRules(string artifactRoot)
    {
        using var fixture = new TestDirectory();
        for (int index = 0; index < 300; index++) CleanupRulePlanTests.Project(fixture.Path, $"Atlas-{index:D3}", 60);
        string node = Directory.CreateDirectory(Path.Join(fixture.Path, "Orbit-web")).FullName;
        File.WriteAllText(Path.Join(node, "package.json"), "{}");
        string modules = Directory.CreateDirectory(Path.Join(node, "node_modules")).FullName;
        File.WriteAllBytes(Path.Join(modules, "package.js"), new byte[8192]);
        string environment = Directory.CreateDirectory(Path.Join(fixture.Path, "Speech-tools", ".venv")).FullName;
        File.WriteAllText(Path.Join(environment, "pyvenv.cfg"), "home = fixture");
        File.WriteAllBytes(Path.Join(environment, "library.dat"), new byte[16384]);
        BuildActivityFixture.AgeProject(fixture.Path, 60);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(10)); var scan = scanning.GetAwaiter().GetResult();
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, DeveloperOpportunityFixture.Find(scan), discover: DeveloperOpportunityFixture.Find);
        var plan = CleanupPlanner.ReviewRules(basis, new(30), BuildCleanupAction.Recycle, DeveloperOpportunityFixture.Find);
        var dialog = new ReviewWindow(plan, "Owned many-project fixture · Preview only", (_, _, _) => throw new InvalidOperationException("Preview must not execute."));
        dialog.EnableRuleReview(basis, DeveloperOpportunityFixture.Find, false);
        dialog.ShowActivated = false; dialog.ShowInTaskbar = false; dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000; dialog.Top = -10000; dialog.Show();
        try
        {
            PumpUntil(() => ((Button)dialog.FindName("CleanupButton")).IsEnabled, TimeSpan.FromSeconds(5));
            var rules = ((ItemsControl)dialog.FindName("RuleList")).Items.Cast<CleanupRuleReviewRow>().ToArray();
            Assert.Equal(3, rules.Length); Assert.Contains("300 included", rules.Single(rule => rule.IsBuildRule).Effect);
            Assert.False(rules.Single(rule => rule.Title.Contains("environment", StringComparison.OrdinalIgnoreCase)).Checked);
            Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-many-rules.png"), 980, 720);
            Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-many-rules-compact.png"), 760, 520);
            Assert.Equal(Visibility.Collapsed, ((Grid)dialog.FindName("FolderBrowser")).Visibility);
        }
        finally { dialog.Close(); }
    }
}
