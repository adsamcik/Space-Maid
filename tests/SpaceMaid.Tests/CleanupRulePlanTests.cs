using System.IO;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CleanupRulePlanTests
{
    [Fact]
    public async Task AgeChangesKeepTheCapturedTargetsAndPermanentChoiceThroughRestriction()
    {
        using var fixture = new TestDirectory();
        var old = Project(fixture.Path, "old", 60); var newer = Project(fixture.Path, "newer", 10);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, DeveloperOpportunityFixture.Find(scan), discover: DeveloperOpportunityFixture.Find);
        Assert.Equal(2, basis.Targets.Count); Assert.Equal(BuildCleanupAction.Recycle, basis.BuildAction);
        var reviewed = CleanupPlanner.ReviewRules(basis, new(30), BuildCleanupAction.PermanentlyDelete, DeveloperOpportunityFixture.Find);
        Assert.Equal(old.Output, Assert.Single(reviewed.Preview.Locations).Path);
        Assert.Contains(reviewed.BlockedLocations, item => item.Opportunity.Path == newer.Output);
        var selected = CleanupPlanner.Restrict(reviewed, reviewed.Preview.Locations);
        Assert.Equal(BuildCleanupAction.PermanentlyDelete, selected.BuildAction);
        Assert.Same(basis.Targets.Single(item => item.Path == old.Output), Assert.Single(selected.Targets));
        Assert.DoesNotContain(selected.Preview.Cautions, text => text.Contains("Folders are moved to the Recycle Bin only"));
        Assert.Contains(selected.Preview.Cautions, text => text.Contains("cannot be restored"));
        Directory.Move(old.Output, old.Output + "-held"); Directory.CreateDirectory(old.Output);
        File.WriteAllText(old.File, "replacement");
        var edited = CleanupPlanner.ReviewRules(basis, new(7), BuildCleanupAction.Recycle, DeveloperOpportunityFixture.Find);
        Assert.Same(basis.Targets.Single(item => item.Path == old.Output), edited.Targets.Single(item => item.Path == old.Output));
        Assert.Throws<InvalidOperationException>(() => edited.Targets.Single(item => item.Path == old.Output).ValidateCurrent());
        Assert.Equal("replacement", File.ReadAllText(old.File));
    }

    [Fact]
    public async Task NestedRulesRemainIndependentUntilTheChosenRulesAreCollapsed()
    {
        using var fixture = new TestDirectory();
        string environment = Directory.CreateDirectory(Path.Join(fixture.Path, ".venv")).FullName;
        File.WriteAllText(Path.Join(environment, "pyvenv.cfg"), "home = fixture");
        string cache = Directory.CreateDirectory(Path.Join(environment, "__pycache__")).FullName;
        File.WriteAllBytes(Path.Join(cache, "module.pyc"), new byte[4096]);
        BuildActivityFixture.AgeProject(fixture.Path, 60);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var findings = DeveloperOpportunityFixture.Find(scan);
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, findings, discover: DeveloperOpportunityFixture.Find);
        Assert.Equal(2, basis.Targets.Count);
        var reviewed = CleanupPlanner.ReviewRules(basis, new(14), BuildCleanupAction.Recycle, DeveloperOpportunityFixture.Find);
        Assert.Equal(2, reviewed.Preview.Locations.Count);
        var child = CleanupPlanner.Restrict(reviewed, reviewed.Preview.Locations.Where(item => item.Path == cache));
        Assert.Equal(cache, Assert.Single(child.Targets).Path); Assert.Single(child.Preview.Locations);
        var both = CleanupPlanner.Restrict(reviewed, reviewed.Preview.Locations);
        Assert.Equal(environment, Assert.Single(both.Targets).Path); Assert.Single(both.Preview.Locations);
        Assert.Equal(basis.Preview.EstimatedBytes, both.Preview.EstimatedBytes);
        Assert.True(File.Exists(Path.Join(cache, "module.pyc")));
    }

    [Fact]
    public async Task DisappearingRulesAndRebuildabilityDowngradesAreKeptWithoutBreakingOtherRules()
    {
        using var fixture = new TestDirectory();
        var removed = Project(fixture.Path, "removed", 60); var downgraded = Project(fixture.Path, "downgraded", 60);
        var good = Project(fixture.Path, "good", 60);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, DeveloperOpportunityFixture.Find(scan), discover: DeveloperOpportunityFixture.Find);
        IReadOnlyList<Opportunity> Changed(ScanSnapshot current, CancellationToken token) => DeveloperOpportunityFixture.Find(current, token)
            .Where(item => item.Path != removed.Output).Select(item => item.Path == downgraded.Output ? item with { IsRegeneratable = false } : item).ToArray();
        var reviewed = CleanupPlanner.ReviewRules(basis, new(30), BuildCleanupAction.PermanentlyDelete, Changed);
        Assert.Equal(good.Output, Assert.Single(reviewed.Preview.Locations).Path);
        Assert.Equal(2, reviewed.BlockedLocations.Count);
        Assert.True(File.Exists(removed.File)); Assert.True(File.Exists(downgraded.File));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AContainingEnvironmentCannotBypassAnUnverifiedBuildRule(bool missingRule)
    {
        using var fixture = new TestDirectory();
        string environment = Directory.CreateDirectory(Path.Join(fixture.Path, ".venv")).FullName;
        File.WriteAllText(Path.Join(environment, "pyvenv.cfg"), "home = fixture");
        string cache = Directory.CreateDirectory(Path.Join(environment, "__pycache__")).FullName;
        File.WriteAllText(Path.Join(cache, "module.pyc"), "owned fixture"); BuildActivityFixture.AgeProject(fixture.Path, 60);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, DeveloperOpportunityFixture.Find(scan), discover: DeveloperOpportunityFixture.Find);
        IReadOnlyList<Opportunity> Unverified(ScanSnapshot current, CancellationToken token) => DeveloperOpportunityFixture.Find(current, token)
            .Where(item => !missingRule || !item.IsBuildOutput)
            .Select(item => item.IsBuildOutput ? item with { IsRegeneratable = false } : item).ToArray();
        var reviewed = CleanupPlanner.ReviewRules(basis, new(30), BuildCleanupAction.Recycle, Unverified);
        Assert.Empty(reviewed.Targets); Assert.Equal(2, reviewed.BlockedLocations.Count);
        Assert.Contains(reviewed.BlockedLocations, item => item.Opportunity.Path == environment && item.Reason.Contains("unverified build"));
    }

    [Fact]
    public async Task UnverifiedBuildsStayKeptForBothActions()
    {
        using var fixture = new TestDirectory(); var project = Project(fixture.Path, "project", 60);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        IReadOnlyList<Opportunity> Unverified(ScanSnapshot current, CancellationToken token)
            => DeveloperOpportunityFixture.Find(current, token).Select(item => item with { IsRegeneratable = false }).ToArray();
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, Unverified(scan, default), discover: Unverified);
        foreach (var action in Enum.GetValues<BuildCleanupAction>())
        {
            var reviewed = CleanupPlanner.ReviewRules(basis, new(14), action, Unverified);
            Assert.Empty(reviewed.Targets); Assert.Empty(reviewed.Preview.Locations);
            Assert.Contains("verified as rebuildable", Assert.Single(reviewed.BlockedLocations).Reason);
        }
        Assert.Equal("generated output", File.ReadAllText(project.File));
    }

    internal static (string Root, string Output, string File, string Source) Project(string root, string name, int days)
    {
        string project = Directory.CreateDirectory(Path.Join(root, name)).FullName;
        File.WriteAllText(Path.Join(project, "app.csproj"), "<Project />");
        string source = Path.Join(project, "Program.cs"); File.WriteAllText(source, "authored source");
        string output = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
        string file = Path.Join(output, "result.dll"); File.WriteAllText(file, "generated output");
        BuildActivityFixture.AgeProject(project, days);
        return (project, output, file, source);
    }
}
