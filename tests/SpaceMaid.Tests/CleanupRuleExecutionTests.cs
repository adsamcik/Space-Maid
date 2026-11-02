using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

[Trait("Stage", "System")]
public sealed class CleanupRuleExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentRunDeletesOnlyVerifiedOutputAndContinuesPastLockedAndNewlyActiveBuilds(bool logical)
    {
        using var fixture = new TestDirectory();
        var good = CleanupRulePlanTests.Project(fixture.Path, "good", 60);
        var locked = CleanupRulePlanTests.Project(fixture.Path, "locked", 60);
        var active = CleanupRulePlanTests.Project(fixture.Path, "active", 60);
        var native = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var scan = logical ? AsLogical(native) : native;
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, DeveloperOpportunityFixture.Find(scan), discover: DeveloperOpportunityFixture.Find);
        var plan = CleanupPlanner.ReviewRules(basis, new(30), BuildCleanupAction.PermanentlyDelete, DeveloperOpportunityFixture.Find);
        plan = CleanupPlanner.Restrict(plan, plan.Preview.Locations);
        File.SetLastWriteTimeUtc(active.File, DateTime.UtcNow);
        CleanupBatchResult result;
        using (File.Open(locked.File, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            result = await new FileOperations().ExecuteCleanupAsync(plan, 0, new Progress(), default, (_, _) => { }, discover: DeveloperOpportunityFixture.Find);
        Assert.Null(result.ValidationError); Assert.Equal(1, result.DeletedCount); Assert.Equal(0, result.RecycledCount);
        Assert.Equal(good.Output, Assert.Single(result.Locations, item => item.Outcome == CleanupOutcome.PermanentlyDeleted).Location.Path);
        Assert.Equal(2, result.Locations.Count(item => item.Outcome == CleanupOutcome.Skipped));
        Assert.All(result.Locations.Where(item => item.Outcome == CleanupOutcome.Skipped), item => Assert.False(item.Attempted));
        Assert.False(Directory.Exists(good.Output));
        Assert.Equal("generated output", File.ReadAllText(locked.File));
        Assert.Equal("generated output", File.ReadAllText(active.File));
        foreach (var project in new[] { good, locked, active }) Assert.Equal("authored source", File.ReadAllText(project.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AContainingRuleKeepsBuildsThatBecomeUnverifiedAfterApproval(bool missingRule)
    {
        using var fixture = new TestDirectory();
        string environment = Directory.CreateDirectory(Path.Join(fixture.Path, ".venv")).FullName;
        File.WriteAllText(Path.Join(environment, "pyvenv.cfg"), "home = fixture");
        string cache = Directory.CreateDirectory(Path.Join(environment, "__pycache__")).FullName;
        string generated = Path.Join(cache, "module.pyc"); File.WriteAllText(generated, "owned fixture");
        BuildActivityFixture.AgeProject(fixture.Path, 60);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, DeveloperOpportunityFixture.Find(scan), discover: DeveloperOpportunityFixture.Find);
        var plan = CleanupPlanner.ReviewRules(basis, new(30), BuildCleanupAction.Recycle, DeveloperOpportunityFixture.Find);
        plan = CleanupPlanner.Restrict(plan, plan.Preview.Locations);
        Assert.Equal(environment, Assert.Single(plan.Targets).Path);
        IReadOnlyList<Opportunity> Unverified(ScanSnapshot current, CancellationToken token) => DeveloperOpportunityFixture.Find(current, token)
            .Where(item => !missingRule || !item.IsBuildOutput)
            .Select(item => item.IsBuildOutput ? item with { IsRegeneratable = false } : item).ToArray();
        var result = await new FileOperations().ExecuteCleanupAsync(plan, 0, new Progress(), default, (_, _) => { }, discover: Unverified);
        var kept = Assert.Single(result.Locations);
        Assert.Equal(CleanupOutcome.Skipped, kept.Outcome); Assert.False(kept.Attempted);
        Assert.Equal("owned fixture", File.ReadAllText(generated));
    }

    [Fact]
    public async Task LosingRebuildabilityAfterApprovalKeepsTheBuildWithoutAnyMutation()
    {
        using var fixture = new TestDirectory(); var project = CleanupRulePlanTests.Project(fixture.Path, "project", 60);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, DeveloperOpportunityFixture.Find(scan), discover: DeveloperOpportunityFixture.Find);
        var plan = CleanupPlanner.ReviewRules(basis, new(30), BuildCleanupAction.PermanentlyDelete, DeveloperOpportunityFixture.Find);
        IReadOnlyList<Opportunity> Unverified(ScanSnapshot current, CancellationToken token)
            => DeveloperOpportunityFixture.Find(current, token).Select(item => item with { IsRegeneratable = false }).ToArray();
        var result = await new FileOperations().ExecuteCleanupAsync(plan, 0, new Progress(), default, (_, _) => { }, discover: Unverified);
        var kept = Assert.Single(result.Locations);
        Assert.Equal(CleanupOutcome.Skipped, kept.Outcome); Assert.False(kept.Attempted);
        Assert.Equal("generated output", File.ReadAllText(project.File));
        Assert.Equal("authored source", File.ReadAllText(project.Source));
    }

    private sealed class Progress : IProgress<CleanupProgress> { public void Report(CleanupProgress value) { } }
    private static ScanSnapshot AsLogical(ScanSnapshot scan) => new(scan.RootPath, "Logical fixture", scan.Records,
        scan.Links, scan.NameArena, scan.RootIndex, scan.FirstLinks, scan.ChildHeads, scan.NextLinks, scan.DirectoryLogical,
        scan.DirectoryAllocated, false, false, false, scan.Warnings, scan.Metrics, scan.EntryFlags, scan.WarningCount,
        scan.DirectoryCount, scan.FallbackReason);
}
