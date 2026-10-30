using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckCleanupResults(Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        foreach (var (name, bytes) in new[] { ("Orbit", 4096), ("Lunar", 8192), ("Comet", 16384) })
        {
            string project = Directory.CreateDirectory(Path.Join(fixture.Path, name)).FullName;
            File.WriteAllText(Path.Join(project, name + ".csproj"), "<Project />");
            string output = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
            File.WriteAllBytes(Path.Join(output, name + ".dll"), new byte[bytes]);
        }
        BuildActivityFixture.AgeProject(fixture.Path);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(5));
        var scan = scanning.GetAwaiter().GetResult();
        var plan = CleanupPlanner.Prepare(scan, OpportunityDetector.Find(scan), discover: DeveloperOpportunityFixture.Find);
        Assert.Equal(3, plan.Targets.Count);

        foreach (string scenario in new[] { "mixed", "interrupted", "all-moved" })
        {
            var completion = new TaskCompletionSource<CleanupBatchResult>();
            IProgress<CleanupProgress>? progress = null;
            var dialog = new ReviewWindow(plan, "Owned fixture · Simulated recycling", (_, updates, _) =>
            { progress = updates; return completion.Task; });
            dialog.ShowActivated = false; dialog.ShowInTaskbar = false;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -10000; dialog.Top = -10000;
            dialog.Show();
            try
            {
                var list = (ListBox)dialog.FindName("LocationList");
                var rows = list.Items.Cast<CleanupReviewRow>().ToArray();
                var estimate = (TextBlock)dialog.FindName("EstimateText");
                var caption = (TextBlock)dialog.FindName("EstimateCaption");
                ((Button)dialog.FindName("CleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotNull(progress);
                var moved = new CleanupLocationResult(plan.Preview.Locations[0], CleanupOutcome.Recycled, "Moved to Recycle Bin.", true);
                progress.Report(new(1, 3, moved.Location.Path, moved.Message, moved));
                var movedRow = rows.Single(row => row.Path == moved.Location.Path);
                var expected = CleanupPlanner.Create(scan, plan.Preview.Locations.Skip(1));
                PumpUntil(() => movedRow.IsRecycled && estimate.Text == SizeText.Format(expected.EstimatedBytes)
                    && caption.Text.StartsWith("2 folders remaining", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
                Assert.False(completion.Task.IsCompleted); // Still waiting for the host's post-cleanup refresh.
                Assert.False(movedRow.Selected); Assert.Equal(0, movedRow.Bytes); Assert.Equal("—", movedRow.Size);
                dialog.UpdateLayout();
                var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(movedRow));
                Assert.Contains(Descendants(container).OfType<TextBlock>(), text => text.Text == movedRow.FolderName
                    && text.TextDecorations.Any(decoration => decoration.Location == TextDecorationLocation.Strikethrough));
                Assert.Contains(Descendants(container).OfType<TextBlock>(), text => text.Text == "Moved to Recycle Bin" && text.IsVisible);
                Assert.All(rows.Where(row => row != movedRow), row => Assert.False(row.IsRecycled));

                if (scenario == "interrupted") completion.SetException(new IOException("Owned refresh failure"));
                else if (scenario == "all-moved")
                    completion.SetResult(new(plan.Preview.Locations.Select(item =>
                        new CleanupLocationResult(item, CleanupOutcome.Recycled, "Moved to Recycle Bin.", true)).ToArray(), null));
                else
                {
                    // A skipped folder grew while a cancelled folder lost contents.
                    // Prefer the host's post-cleanup inventory to the confirmation data.
                    var skipped = plan.Preview.Locations[1];
                    File.WriteAllBytes(Path.Join(skipped.Path, "additional.dll"), new byte[32768]);
                    var cancelled = plan.Preview.Locations[2];
                    File.WriteAllBytes(Directory.GetFiles(cancelled.Path).Single(), new byte[1024]);
                    var rescanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
                    PumpUntil(() => rescanning.IsCompleted, TimeSpan.FromSeconds(5));
                    var fresh = rescanning.GetAwaiter().GetResult();
                    var findings = OpportunityDetector.Find(fresh);
                    var originalFindings = OpportunityDetector.Find(scan);
                    var validation = new OpportunityReviewValidation(scan, originalFindings, originalFindings, [], [], true, []);
                    completion.SetResult(new([moved, new(skipped, CleanupOutcome.Skipped, "Activity changed."),
                        new(cancelled, CleanupOutcome.Cancelled, "Cancelled; contents may have moved.", true)], validation)
                        { RefreshedSnapshot = fresh, RefreshedFindings = findings });
                    var remainingKeys = plan.Preview.Locations.Skip(1).Select(item => item.Path).ToHashSet();
                    expected = CleanupPlanner.Create(fresh, findings.Where(item => remainingKeys.Contains(item.Path)));
                }
                PumpUntil(() => dialog.Execution!.IsCompleted && Equals("Close", ((Button)dialog.FindName("CloseButton")).Content) && estimate.Text != "…"
                    && caption.Text.Contains("remaining", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
                Assert.False(((Button)dialog.FindName("CleanupButton")).IsEnabled);
                if (scenario == "all-moved")
                {
                    Assert.All(rows, row => Assert.True(row.IsRecycled));
                    Assert.Equal(SizeText.Format(0), estimate.Text); Assert.StartsWith("0 folders remaining", caption.Text);
                }
                else
                {
                    Assert.True(movedRow.IsRecycled);
                    Assert.All(rows.Where(row => row != movedRow), row => Assert.False(row.IsRecycled));
                    Assert.Equal(SizeText.Format(expected.EstimatedBytes), estimate.Text);
                    Assert.Equal(scenario == "interrupted", caption.Text.Contains("Previous estimate", StringComparison.Ordinal));
                    ((RadioButton)dialog.FindName("ManualFilter")).IsChecked = true;
                    Assert.Equal(2, list.Items.Count);
                    Assert.All(list.Items.Cast<CleanupReviewRow>(), row =>
                    { Assert.False(row.IsRecycled); Assert.Equal(CleanupReadiness.NeedsReview, row.DisplayState); });
                    if (scenario == "mixed")
                    {
                        var groups = ((ListBox)dialog.FindName("GroupList")).Items.Cast<CleanupReviewGroup>();
                        Assert.Equal(rows.Where(row => !row.IsRecycled).Sum(row => row.Bytes), groups.Single(group => group.Key == "all").Bytes);
                    }
                    ((RadioButton)dialog.FindName("ReadyFilter")).IsChecked = true;
                }
                if (scenario == "mixed")
                {
#pragma warning disable WPF0001
                    foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                    {
                        app.ThemeMode = theme;
                        string suffix = theme == ThemeMode.Light ? "light" : "dark";
                        Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-results-updated-{suffix}.png"), 980, 720);
                        Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-results-updated-compact-{suffix}.png"), 760, 520);
                    }
#pragma warning restore WPF0001
                }
            }
            finally { completion.TrySetCanceled(); dialog.Close(); }
        }
        Assert.All(plan.Preview.Locations, item => Assert.True(Directory.Exists(item.Path)));
    }
}
