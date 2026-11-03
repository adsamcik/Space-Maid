using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckResilientCleanupReview(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory(); using var outside = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "sample.csproj"), "<Project />");
        string good = Directory.CreateDirectory(Path.Join(fixture.Path, "bin")).FullName;
        string bad = Directory.CreateDirectory(Path.Join(fixture.Path, "obj")).FullName;
        File.WriteAllBytes(Path.Join(good, "good.dll"), new byte[8192]);
        File.WriteAllBytes(Path.Join(bad, "bad.o"), new byte[4096]);
        File.WriteAllText(Path.Join(outside.Path, "keep.txt"), "outside contents");
        BuildActivityFixture.AgeProject(fixture.Path);
        string junction = Path.Join(bad, "linked");
        var linking = OpportunityReviewValidatorTests.CreateJunctionAsync(junction, outside.Path);
        PumpUntil(() => linking.IsCompleted, TimeSpan.FromSeconds(5)); linking.GetAwaiter().GetResult();
        var scanTask = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanTask.IsCompleted, TimeSpan.FromSeconds(5));
        var scan = scanTask.GetAwaiter().GetResult(); var findings = OpportunityDetector.Find(scan);
        var load = window.InitializeExplorerAsync(scan, findings, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, findings, OpportunityGrouping.Build(scan, findings), null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        ((ComboBox)OpportunityControl(window, "OpportunityGroupingPicker")).SelectedIndex = 2;
        ((Button)OpportunityControl(window, "AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((TextBox)OpportunityControl(window, "OpportunitySearch")).Text = "";
        var list = (ListBox)OpportunityControl(window, "OpportunityGrid");
        PumpUntil(() => list.Items.Count == 2, TimeSpan.FromSeconds(3));
        foreach (var row in list.Items.Cast<OpportunityRow>()) row.Selected = true;
        CleanupBatchResult? result = null; CleanupWorkspace? workspace = null;
        try
        {
            workspace = OpenWorkspaceReview(window, 3);
            Assert.DoesNotContain(app.Windows.OfType<ReviewWindow>(), candidate => candidate.IsVisible);
            Assert.Equal(good, Assert.Single(workspace.Prepared!.Plan.Preview.Locations).Path);
            Assert.Contains(workspace.Prepared.Plan.BlockedLocations, item => item.Opportunity.Path == bad);
            ((Button)workspace.FindName("KeptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var inspector = Assert.IsType<CleanupInspector>(((Border)workspace.FindName("InspectorHost")).Child);
            Assert.True(((RadioButton)inspector.FindName("SummaryTab")).IsChecked);
            ((RadioButton)inspector.FindName("ListTab")).IsChecked = true;
            var locations = (ListBox)inspector.FindName("FolderList");
            var kept = Assert.IsType<CleanupInspectionEntry>(Assert.Single(locations.Items.Cast<object>()));
            Assert.Equal(bad, kept.Location.Path); Assert.False(kept.Included); Assert.False(kept.CanChoose);
            Assert.Equal(CleanupInspectionDecision.Kept, kept.Decision);
            locations.SelectedItem = kept;
            ((Border)workspace.FindName("InspectorHost")).BringIntoView();
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-skipped-link.png"), 1220, 940);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-skipped-link-compact.png"), 1020, 820);
            ((Button)workspace.FindName("CleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => workspace.Result is not null && workspace.RunTask is null, TimeSpan.FromSeconds(25)); result = workspace.Result;
            Assert.Equal(1, result!.RecycledCount); Assert.False(result.Cancelled);
            Assert.Contains(result.Locations, item => item.Location.Path == bad && item.Outcome == CleanupOutcome.Skipped && !item.Attempted);
            Assert.False(Directory.Exists(good)); Assert.True(Directory.Exists(bad));
            Assert.DoesNotContain("could not be checked:", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.Equal("outside contents", File.ReadAllText(Path.Join(outside.Path, "keep.txt")));
            Assert.Equal(4096, new FileInfo(Path.Join(bad, "bad.o")).Length);
        }
        finally
        {
            result ??= workspace?.Result;
            Directory.Delete(junction);
            if (result is not null) foreach (var item in result.Locations)
                if (item.Recycle?.RecycledPath is { } destination) DeletionTests.RestoreFixture(destination, item.Location.Path);
        }
    }

    private static void CheckOpportunityValidation(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "sample.csproj"), "<Project />");
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "bin")).FullName;
        string obj = Directory.CreateDirectory(Path.Join(fixture.Path, "obj")).FullName;
        string output = Path.Join(bin, "sample.dll"); File.WriteAllBytes(output, new byte[4096]);
        File.WriteAllBytes(Path.Join(obj, "sample.o"), new byte[8192]);
        BuildActivityFixture.AgeProject(fixture.Path);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(5)); var scan = scanning.GetAwaiter().GetResult();
        var findings = OpportunityDetector.Find(scan);
        var load = window.InitializeExplorerAsync(scan, findings, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, findings, OpportunityGrouping.Build(scan, findings), null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        ((ComboBox)OpportunityControl(window, "OpportunityGroupingPicker")).SelectedIndex = 2;
        ((Button)OpportunityControl(window, "AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var list = (ListBox)OpportunityControl(window, "OpportunityGrid");
        Assert.Equal(2, list.Items.Count); Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.False(row.Selected));
        Assert.True(((Button)OpportunityControl(window, "ReviewButton")).IsEnabled);
        File.WriteAllBytes(output, new byte[16384]); Directory.Delete(obj, recursive: true);
        File.WriteAllText(window.CleanupSettingsPath, "{invalid"); window.LoadBuildCleanupSettings();
        window.MarkScanStale("Owned inventory has changed.");
        var workspace = OpenWorkspaceReview(window, 3);
        Assert.DoesNotContain(app.Windows.OfType<ReviewWindow>(), candidate => candidate.IsVisible);
        Assert.Equal("4 KiB", ((TextBlock)workspace.FindName("EstimateText")).Text);
        Assert.Contains("Scan from", ((TextBlock)workspace.FindName("FreshnessText")).Text);
        Assert.Equal(fixture.Path, workspace.Prepared!.Definition.DiscoveryRootPath);
        Assert.Same(scan, workspace.Prepared.Plan.Snapshot);
        Assert.Equal(obj, Assert.Single(workspace.Prepared.Plan.BlockedLocations).Opportunity.Path);
        ((Button)workspace.FindName("KeptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var inspector = Assert.IsType<CleanupInspector>(((Border)workspace.FindName("InspectorHost")).Child);
        ((RadioButton)inspector.FindName("ListTab")).IsChecked = true;
        var kept = Assert.IsType<CleanupInspectionEntry>(Assert.Single(((ListBox)inspector.FindName("FolderList")).Items.Cast<object>()));
        Assert.Equal(obj, kept.Location.Path); Assert.False(kept.CanChoose);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-rule-kept.png"), 1220, 940);
        Assert.Same(scan, Assert.IsType<ExplorerRows>(((ListBox)window.FindName("SpaceTree")).ItemsSource).Scan);
        Assert.Equal(16384, new FileInfo(output).Length);
        Assert.Equal("{invalid", File.ReadAllText(window.CleanupSettingsPath));
        ((TextBox)OpportunityControl(window, "BuildKeepRecentDaysBox")).Text = "3"; Assert.True(window.SaveBuildRetention());
    }
    private static void CheckOpportunityCleanup(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        Directory.CreateDirectory(Path.Join(fixture.Path, ".git"));
        string source = Path.Join(fixture.Path, "sample.csproj"); File.WriteAllText(source, "<Project />");
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "bin")).FullName;
        string obj = Directory.CreateDirectory(Path.Join(fixture.Path, "obj")).FullName;
        File.WriteAllText(Path.Join(bin, "sample.dll"), "rebuildable fixture");
        File.WriteAllText(Path.Join(obj, "sample.o"), "rebuildable fixture");
        string module = Directory.CreateDirectory(Path.Join(fixture.Path, "module")).FullName;
        File.WriteAllText(Path.Join(module, "module.csproj"), "<Project />");
        string retained = Directory.CreateDirectory(Path.Join(module, "bin")).FullName;
        string retainedFile = Path.Join(retained, "retained.dll");
        File.WriteAllText(retainedFile, "old build output");
        BuildActivityFixture.AgeProject(fixture.Path);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(5)); var scan = scanning.GetAwaiter().GetResult();
        var findings = OpportunityDetector.Find(scan);
        var load = window.InitializeExplorerAsync(scan, findings, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, findings, OpportunityGrouping.Build(scan, findings), null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        ((ComboBox)OpportunityControl(window, "OpportunityGroupingPicker")).SelectedIndex = 2;
        ((Button)OpportunityControl(window, "AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var list = (ListBox)OpportunityControl(window, "OpportunityGrid");
        Assert.Equal(3, list.Items.Count);
        foreach (var row in list.Items.Cast<OpportunityRow>()) row.Selected = true;
        CleanupBatchResult? result = null;
        try
        {
            var workspace = OpenWorkspaceReview(window, 3);
            Assert.DoesNotContain(app.Windows.OfType<ReviewWindow>(), candidate => candidate.IsVisible);
            Assert.Equal(new[] { bin, obj, retained }.Order(), workspace.Prepared!.Plan.Preview.Locations.Select(row => row.Path).Order());
            Assert.True(Directory.Exists(bin)); Assert.True(Directory.Exists(obj));
            Assert.DoesNotContain(workspace.Store.LoadRuns(), record => record.Definition.DiscoveryRootPath == fixture.Path);
            var action = (Button)workspace.FindName("CleanupButton"); Assert.True(action.IsEnabled);
            Assert.False(action.IsDefault); // Enter while editing must not authorize deletion.
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-confirmation.png"), 1220, 940);
            File.WriteAllText(retainedFile, "recent build output preserved after confirmation");
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, ((StackPanel)workspace.FindName("RunningPanel")).Visibility);
            Assert.True(((Button)workspace.FindName("StopButton")).IsEnabled);
            PumpUntil(() => workspace.Result is not null && workspace.RunTask is null, TimeSpan.FromSeconds(25)); result = workspace.Result;
            Assert.NotNull(result); Assert.Equal(2, result.RecycledCount); Assert.False(result.Cancelled);
            Assert.Equal(retained, Assert.Single(result.Locations, item => item.Outcome == CleanupOutcome.Skipped).Location.Path);
            Assert.Equal(2, result.Locations.Count(item => item.Outcome == CleanupOutcome.Recycled));
            Assert.Contains("Affected folders refreshed", result.RefreshNote);
            Assert.Contains("Recycled data still uses drive space", ((TextBlock)workspace.FindName("ResultSummary")).Text);
            ((Button)workspace.FindName("InspectResultsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var inspector = Assert.IsType<CleanupInspector>(((Border)workspace.FindName("InspectorHost")).Child);
            ((ComboBox)inspector.FindName("OutcomePicker")).SelectedIndex = 3;
            ((RadioButton)inspector.FindName("ListTab")).IsChecked = true;
            var movedRows = ((ListBox)inspector.FindName("FolderList")).Items.Cast<CleanupInspectionEntry>().ToArray();
            Assert.Equal(2, movedRows.Length); Assert.All(movedRows, row => Assert.Equal(CleanupInspectionDecision.Removed, row.Decision));
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-results.png"), 1220, 940);
            Assert.False(Directory.Exists(bin)); Assert.False(Directory.Exists(obj)); Assert.Equal("<Project />", File.ReadAllText(source));
            var updated = Assert.IsType<ExplorerRows>(((ListBox)window.FindName("SpaceTree")).ItemsSource).Scan;
            Assert.Null(updated.FindEntry(bin)); Assert.Null(updated.FindEntry(obj)); Assert.NotNull(updated.FindEntry(source));
            var remaining = Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>()));
            Assert.Equal(retained, remaining.Path); Assert.False(remaining.Selected);
            Assert.Equal("recent build output preserved after confirmation", File.ReadAllText(retainedFile));
            Assert.Equal(new FileInfo(retainedFile).Length, remaining.Item.LogicalBytes);
            var remainingEstimate = window.Operations.EstimateCleanup(updated, OpportunityDetector.Find(updated));
            Assert.Equal(SizeText.Format(remainingEstimate.EstimatedBytes), ((TextBlock)window.FindName("CandidateText")).Text);
            string log = File.ReadAllText(Assert.IsType<string>(((Button)window.FindName("OpenLogButton")).ToolTip));
            Assert.Contains("cleanup-confirmed", log); Assert.Contains("cleanup-location-result", log); Assert.Contains("cleanup-complete", log);
            foreach (string line in log.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                using var entry = JsonDocument.Parse(line);
                if (entry.RootElement.GetProperty("Event").GetString() == "recheck-read-start")
                    Assert.NotEqual(retained, entry.RootElement.GetProperty("Data").GetProperty("Path").GetString());
            }
        }
        finally
        {
            if (result is not null)
                foreach (var item in result.Locations)
                    if (item.Recycle?.RecycledPath is { } destination) DeletionTests.RestoreFixture(destination, item.Location.Path);
        }
    }

    private static CleanupWorkspace OpenWorkspaceReview(MainWindow window, int days)
    {
        var review = window.ReviewSelectionAsync();
        PumpUntil(() => review.IsCompleted, TimeSpan.FromSeconds(10)); review.GetAwaiter().GetResult();
        var workspace = Assert.IsType<CleanupWorkspace>(window.CleanupWorkspaceView);
        ((TextBox)workspace.FindName("DaysBox")).Text = days.ToString(System.Globalization.CultureInfo.InvariantCulture);
        WaitForWorkspacePlan(workspace, days);
        return workspace;
    }

    private static void WaitForWorkspacePlan(CleanupWorkspace workspace, int days, bool requireIncluded = true, BuildCleanupAction? action = null)
        => PumpUntil(() => workspace.PreviewTask is { IsCompleted: true } && workspace.Prepared is { } prepared
            && prepared.Definition.InactiveDays == days && (action is null || prepared.Definition.BuildAction == action)
            && (!requireIncluded || ((Button)workspace.FindName("CleanupButton")).IsEnabled), TimeSpan.FromSeconds(15));

    private static void CheckBulkCleanupReview(Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        var recentFiles = new List<string>();
        for (int index = 0; index < 1000; index++)
        {
            string project = Directory.CreateDirectory(Path.Join(fixture.Path, $"project-{index:0000}")).FullName;
            bool cargo = index is >= 700 and < 800, generic = index is >= 800 and < 900;
            File.WriteAllText(Path.Join(project, cargo ? "Cargo.toml" : generic ? "package.json" : "app.csproj"), "owned fixture");
            string output = Directory.CreateDirectory(Path.Join(project, cargo ? "target" : generic ? "dist" : "bin")).FullName;
            string file = Path.Join(output, cargo ? "result.rlib" : generic ? "result.js" : "result.dll");
            File.WriteAllBytes(file, new byte[1024]);
            if (cargo) File.WriteAllText(Path.Join(output, ".rustc_info.json"), "owned fixture");
            if (index >= 900) recentFiles.Add(file);
        }
        BuildActivityFixture.AgeProject(fixture.Path);
        foreach (string file in recentFiles) File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(10)); var scan = scanning.GetAwaiter().GetResult();
        var findings = DeveloperOpportunityFixture.Find(scan);
        Assert.Equal(1000, findings.Count);
        var plan = CleanupPlanner.Prepare(scan, findings, discover: DeveloperOpportunityFixture.Find);
        Assert.Equal(900, plan.Targets.Count); Assert.Equal(100, plan.BlockedLocations.Count);
        Assert.Equal(800, plan.BuildAssessments.Values.Count(item => item.State == CleanupReadiness.Ready));
        Assert.Equal(100, plan.BuildAssessments.Values.Count(item => item.State == CleanupReadiness.NeedsReview));
        var groups = findings.GroupBy(item => item.Title).Select(group => new OpportunityGroup(group.Key, group.Key,
            fixture.Path, "Build files", group.ToArray(), group.Sum(item => item.AllocatedBytes))).ToArray();
        CleanupPlan? confirmed = null;
        var dialog = new ReviewWindow(plan, "Owned illustrative inventory · No files will move.", (chosen, _, _) =>
        {
            confirmed = chosen;
            return Task.FromResult(new CleanupBatchResult(chosen.Preview.Locations.Select(item =>
                new CleanupLocationResult(item, CleanupOutcome.NotProcessed, "Fixture preview only")).ToArray(), null));
        }, groups);
        dialog.ShowActivated = false; dialog.ShowInTaskbar = false; dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000; dialog.Top = -10000; dialog.Show();
        try
        {
            var locations = (ListBox)dialog.FindName("LocationList");
            var navigation = (ListBox)dialog.FindName("GroupList");
            var search = (TextBox)dialog.FindName("SearchBox");
            var cleanup = (Button)dialog.FindName("CleanupButton");
            var estimate = (TextBlock)dialog.FindName("EstimateCaption");
            var all = navigation.Items.Cast<CleanupReviewGroup>().Single(group => group.Key == "all");
            var rows = all.Rows;
            Assert.Equal(1000, rows.Count); Assert.Equal(800, rows.Count(row => row.Selected));
            Assert.All(rows.Where(row => row.Assessment.State == CleanupReadiness.NeedsReview), row => Assert.False(row.Selected));
            Assert.All(rows.Where(row => row.Assessment.State == CleanupReadiness.Protected), row => Assert.False(row.CanSelect));
            Assert.Equal(100, locations.Items.Count);
            Assert.Contains("of 800", ((TextBlock)dialog.FindName("PageText")).Text);
            ((Button)dialog.FindName("NextPage")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.StartsWith("101–200", ((TextBlock)dialog.FindName("PageText")).Text);
            Assert.Equal(100, locations.Items.Count);
            search.Text = "project-0012";
            var excluded = Assert.IsType<CleanupReviewRow>(Assert.Single(locations.Items.Cast<object>()));
            excluded.Selected = false;
            PumpUntil(() => cleanup.IsEnabled && estimate.Text.Contains("799 folders", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            search.Text = "";
            Assert.False(rows.Single(row => row.Path.Contains("project-0012", StringComparison.Ordinal)).Selected);
            var cargo = navigation.Items.Cast<CleanupReviewGroup>().Single(group => group.Name == "Cargo build output");
            navigation.SelectedItem = cargo; cargo.Checked = false;
            PumpUntil(() => cleanup.IsEnabled && estimate.Text.Contains("699 folders", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            ((RadioButton)dialog.FindName("ManualFilter")).IsChecked = true;
            var manualGroup = navigation.Items.Cast<CleanupReviewGroup>().Single(group => group.Name == "Possible Node build output");
            navigation.SelectedItem = manualGroup; Assert.Equal(100, locations.Items.Count);
            Assert.All(locations.Items.Cast<CleanupReviewRow>(), row => Assert.False(row.Selected));
            manualGroup.Checked = true;
            PumpUntil(() => cleanup.IsEnabled && estimate.Text.Contains("799 folders", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            ((RadioButton)dialog.FindName("KeptFilter")).IsChecked = true;
            Assert.Equal(100, locations.Items.Count);
            Assert.All(locations.Items.Cast<CleanupReviewRow>(), row => { row.Selected = true; Assert.False(row.Selected); Assert.False(row.CanSelect); });
            ((RadioButton)dialog.FindName("ReadyFilter")).IsChecked = true;
            navigation.SelectedItem = navigation.Items.Cast<CleanupReviewGroup>().Single(group => group.Key == "all");
            locations.SelectedIndex = 0;
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme;
                string suffix = theme == ThemeMode.Light ? "light" : "dark";
                Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", $"bulk-cleanup-review-{suffix}.png"), 980, 720);
                Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", $"bulk-cleanup-review-compact-{suffix}.png"), 760, 520);
            }
#pragma warning restore WPF0001
            Assert.InRange(Descendants(locations).OfType<ListBoxItem>().Count(), 1, 30);
            cleanup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => dialog.Result is not null, TimeSpan.FromSeconds(5));
            Assert.NotNull(confirmed); Assert.Equal(799, confirmed.Preview.Locations.Count);
            Assert.Equal(plan.BuildPolicy, confirmed.BuildPolicy);
            Assert.DoesNotContain(confirmed.Preview.Locations, item => item.Path.Contains("project-0012", StringComparison.Ordinal));
            Assert.DoesNotContain(confirmed.Preview.Locations, item => item.Title == "Cargo build output");
            Assert.DoesNotContain(confirmed.Preview.Locations, item => recentFiles.Any(file => file.StartsWith(item.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
            Assert.All(confirmed.Targets, target => Assert.Same(plan.Targets.Single(item => item.Path == target.Path), target));
            Assert.All(findings, finding => Assert.True(Directory.Exists(finding.Path)));
        }
        finally { dialog.Close(); }
    }
}
