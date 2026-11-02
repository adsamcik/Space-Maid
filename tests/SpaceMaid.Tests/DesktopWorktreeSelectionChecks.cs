using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckWorktreeGroupSelection(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        var creating = GitWorktreeAnalyzerTests.GitFixture.Create(fixture.Path);
        PumpUntil(() => creating.IsCompleted, TimeSpan.FromSeconds(15)); using var repo = creating.GetAwaiter().GetResult();
        string dirty = Path.Join(repo.Parent, "dirty-checkout");
        var adding = GitWorktreeAnalyzerTests.GitFixture.Run(repo.Main, repo.Old, "worktree", "add", "-b", "dirty", dirty, "main");
        PumpUntil(() => adding.IsCompleted, TimeSpan.FromSeconds(10)); adding.GetAwaiter().GetResult();
        File.WriteAllText(Path.Join(dirty, "source.txt"), "uncommitted work must stay");
        GitWorktreeAnalyzerTests.GitFixture.Age(dirty, repo.Old);
        var aging = GitWorktreeAnalyzerTests.GitFixture.AgeReflogs(dirty, repo.Main, "dirty", repo.Old);
        PumpUntil(() => aging.IsCompleted, TimeSpan.FromSeconds(10)); aging.GetAwaiter().GetResult();
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(5)); var scan = scanning.GetAwaiter().GetResult();
        var findings = DeveloperOpportunityFixture.Find(scan);
        var worktrees = findings.Where(item => item.IsWorktree).ToArray(); Assert.Equal(2, worktrees.Length);
        var loading = window.InitializeExplorerAsync(scan, findings, default);
        PumpUntil(() => loading.IsCompleted, TimeSpan.FromSeconds(5)); loading.GetAwaiter().GetResult();
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, findings, OpportunityGrouping.Build(scan, findings), null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        var cards = (ItemsControl)OpportunityControl(window, "CleanupGroupCards");
        var group = Assert.Single(cards.Items.Cast<OpportunityGroupRow>(), row => row.Group.Role == OpportunityGroupRole.Worktrees);
        window.UpdateLayout();
        var container = cards.ItemContainerGenerator.ContainerFromItem(group);
        var checkbox = Assert.Single(Descendants(container).OfType<CheckBox>(), item => ReferenceEquals(item.DataContext, group));
        Assert.True(checkbox.IsEnabled);
        checkbox.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.All(group.Matches, row => Assert.True(row.Selected));
        var checking = Assert.Single(Descendants(container).OfType<Button>(), button => Equals(button.Content, "Check worktrees"));
        Assert.True(checking.IsVisible); Assert.True(checking.IsEnabled);
#pragma warning disable WPF0001
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"worktree-select-{theme}-1220.png"), 1220, 910);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"worktree-select-{theme}-1020.png"), 1020, 790);
        }
#pragma warning restore WPF0001
        window.UpdateLayout();
        checking = Assert.Single(Descendants(cards).OfType<Button>(), button => Equals(button.Content, "Check worktrees"));
        checking.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var facts = (Dictionary<string, GitWorktreeAssessment>)typeof(MainWindow)
            .GetField("_worktreeAssessments", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        PumpUntil(() => facts.Count == 2 && ((Button)OpportunityControl(window, "CheckWorktreesButton")).IsEnabled, TimeSpan.FromSeconds(20));
        Assert.True(((Border)OpportunityControl(window, "WorktreeCleanupControls")).IsVisible);
        Assert.True(facts[repo.Checkout].VerifiedClean); Assert.False(facts[dirty].VerifiedClean);
        var matched = Assert.Single(cards.Items.Cast<OpportunityGroupRow>(), row => row.Group.Role == OpportunityGroupRole.MatchedWorktrees);
        Assert.Equal(repo.Checkout, Assert.Single(matched.Matches).Path);
        Assert.True(matched.Checked);
        var capture = typeof(MainWindow).GetMethod("SelectedCheckedWorktrees", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var policies = (IReadOnlyDictionary<string, WorktreeCleanupPolicy>)capture.Invoke(window, [worktrees])!;
        Assert.Equal(2, policies.Count);
        var guarding = window.Operations.CheckWorktreesAsync(scan, worktrees, policies);
        PumpUntil(() => guarding.IsCompleted, TimeSpan.FromSeconds(15)); var blocked = guarding.GetAwaiter().GetResult();
        Assert.Contains(dirty, blocked.Keys); Assert.DoesNotContain(repo.Checkout, blocked.Keys);
        Assert.Equal("owned tracked source", File.ReadAllText(repo.Source));
        Assert.Equal("uncommitted work must stay", File.ReadAllText(Path.Join(dirty, "source.txt")));
        ((Button)OpportunityControl(window, "ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((System.Windows.Controls.Primitives.ToggleButton)OpportunityControl(window, "WorktreeOptionsToggle")).IsChecked = false;
    }
}
