using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckReviewClarity(Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string repository = Path.Join(fixture.Path, "hub", "models--buttercrab--dia-v1-1.6b");
        Directory.CreateDirectory(Path.Join(repository, "refs"));
        string revision = Directory.CreateDirectory(Path.Join(repository, "snapshots", "revision")).FullName;
        string weights = Path.Join(revision, "model.safetensors");
        File.WriteAllText(weights, "Owned model fixture; review never deletes it.");
        string project = Directory.CreateDirectory(Path.Join(fixture.Path, "Orbit")).FullName;
        File.WriteAllText(Path.Join(project, "Orbit.csproj"), "<Project />");
        string output = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
        File.WriteAllText(Path.Join(output, "Orbit.dll"), "Owned build fixture");
        BuildActivityFixture.AgeProject(fixture.Path);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(5));
        var scan = scanning.GetAwaiter().GetResult();
        var findings = DeveloperOpportunityFixture.Find(scan);
        var model = Assert.Single(findings, finding => finding.Path == repository);
        var build = Assert.Single(findings, finding => finding.Path == output);
        var manual = CleanupPlanner.Prepare(scan, [model], discover: DeveloperOpportunityFixture.Find);
        var mixed = CleanupPlanner.Prepare(scan, [model, build], discover: DeveloperOpportunityFixture.Find);
        var kept = CleanupPlanner.Prepare(scan, [model], blockedPaths: new Dictionary<string, string> { [repository] = "Owned fixture kept" }, discover: DeveloperOpportunityFixture.Find);
        int executions = 0;
        ReviewWindow Open(CleanupPlan plan)
        {
            var window = new ReviewWindow(plan, "Owned fixtures · Preview only", (_, _, _) =>
            {
                executions++;
                throw new InvalidOperationException("Review-clarity checks must never execute cleanup.");
            });
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
            window.Show(); return window;
        }

        var review = Open(manual);
        try
        {
            var locations = (ListBox)review.FindName("LocationList");
            var row = Assert.IsType<CleanupReviewRow>(Assert.Single(locations.Items.Cast<object>()));
            var action = (Button)review.FindName("CleanupButton");
            Assert.True(((RadioButton)review.FindName("ManualFilter")).IsChecked);
            Assert.False(row.Selected); Assert.True(row.CanSelect); Assert.False(action.IsEnabled);
            Assert.Equal("Choose folders to include.", ((TextBlock)review.FindName("ResultText")).Text);
            Assert.Equal("buttercrab/dia-v1-1.6b", row.FolderName); Assert.Equal(repository, row.Path);
            var search = (TextBox)review.FindName("SearchBox");
            search.Text = "buttercrab/dia-v1-1.6b"; Assert.Single(locations.Items);
            locations.SelectedItem = row;
            Descendants((Border)review.FindName("FolderDetails")).OfType<Expander>().Single().IsExpanded = true;
            review.UpdateLayout();
            Assert.Contains(Descendants((FrameworkElement)review.FindName("FolderDetails")).OfType<TextBox>(), text => text.Text == repository);
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme;
                string suffix = theme == ThemeMode.Light ? "light" : "dark";
                Render(review, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-manual-review-{suffix}.png"), 980, 720);
                Render(review, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-manual-review-compact-{suffix}.png"), 760, 520);
            }
#pragma warning restore WPF0001
            row.Selected = true;
            PumpUntil(() => action.IsEnabled, TimeSpan.FromSeconds(5));
            row.Selected = false;
            Assert.False(action.IsEnabled);
            Assert.Equal("Choose folders to include.", ((TextBlock)review.FindName("ResultText")).Text);
        }
        finally { review.Close(); }

        review = Open(mixed);
        try
        {
            Assert.True(((RadioButton)review.FindName("ReadyFilter")).IsChecked);
            var locations = (ListBox)review.FindName("LocationList");
            Assert.True(Assert.IsType<CleanupReviewRow>(Assert.Single(locations.Items.Cast<object>())).Selected);
            Assert.Equal("Needs review · 1", ((RadioButton)review.FindName("ManualFilter")).Content);
            ((RadioButton)review.FindName("ManualFilter")).IsChecked = true;
            var row = Assert.IsType<CleanupReviewRow>(Assert.Single(locations.Items.Cast<object>()));
            Assert.False(row.Selected); Assert.Equal(repository, row.Path);
        }
        finally { review.Close(); }

        review = Open(kept);
        try
        {
            Assert.True(((RadioButton)review.FindName("KeptFilter")).IsChecked);
            var row = Assert.IsType<CleanupReviewRow>(Assert.Single(((ListBox)review.FindName("LocationList")).Items.Cast<object>()));
            Assert.False(row.CanSelect); row.Selected = true; Assert.False(row.Selected);
            Assert.False(((Button)review.FindName("CleanupButton")).IsEnabled);
            Assert.Contains("kept", ((TextBlock)review.FindName("ResultText")).Text);
        }
        finally { review.Close(); }
        Assert.Equal(0, executions);
        Assert.Equal("Owned model fixture; review never deletes it.", File.ReadAllText(weights));
        Assert.True(File.Exists(Path.Join(output, "Orbit.dll")));
    }
}
