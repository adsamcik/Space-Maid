using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CleanupInspectionTests
{
    [Fact]
    public void ReviewRemainingOnlyOffersApprovedWorkThatWasNeverAttempted()
    {
        var basis = CleanupRunStoreTests.Record(@"G:\owned");
        var cases = new[] { ("Ready", CleanupOutcome.NotProcessed, false, true), ("Ready", CleanupOutcome.Skipped, false, true),
            ("Ready", CleanupOutcome.Cancelled, false, true), ("Kept", CleanupOutcome.Skipped, false, false),
            ("Skipped", CleanupOutcome.Skipped, false, false), ("Ready", CleanupOutcome.Partial, true, false),
            ("Ready", CleanupOutcome.Uncertain, true, false), ("Ready", CleanupOutcome.Failed, true, false),
            ("Ready", CleanupOutcome.Cancelled, true, false), ("Ready", CleanupOutcome.Recycled, true, false),
            ("Ready", CleanupOutcome.PermanentlyDeleted, true, false) };
        var targets = cases.Select((item, index) => basis.Targets[0] with { Decision = item.Item1,
            Location = basis.Targets[0].Location with { Path = Path.Join(@"G:\owned", "bin-" + index) } }).ToArray();
        var record = basis with { State = CleanupRunState.Stopped, Targets = targets, Receipts = cases.Select((item, index) =>
            new CleanupTargetReceipt(targets[index].Location.Path, targets[index].Location.ProviderId, targets[index].Location.RuleId,
                item.Item2, "Fixture outcome", item.Item3, DateTimeOffset.UtcNow)).ToArray() };
        Assert.Equal(targets.Where((_, index) => cases[index].Item4), CleanupWorkspace.RemainingTargets(record));
        var missingReceipts = record with { Receipts = [], Targets = [targets[0], targets[3], targets[4]] };
        Assert.Equal([targets[0]], CleanupWorkspace.RemainingTargets(missingReceipts));
        Assert.Empty(CleanupWorkspace.RemainingTargets(missingReceipts with { State = CleanupRunState.Completed }));
    }

    [Theory]
    [InlineData("Recycled", true, false, "Removed")]
    [InlineData("PermanentlyDeleted", true, false, "Removed")]
    [InlineData("Skipped", false, true, "Kept")]
    [InlineData("Skipped", false, false, "Skipped")]
    [InlineData("NotProcessed", false, false, "NotStarted")]
    [InlineData("Cancelled", false, false, "NotStarted")]
    [InlineData("Cancelled", true, false, "NeedsChecking")]
    [InlineData("Failed", true, false, "NeedsChecking")]
    [InlineData("Partial", true, false, "NeedsChecking")]
    [InlineData("Uncertain", true, false, "NeedsChecking")]
    public void DecisionsDistinguishKeptSkippedAndUnknownWork(string outcome, bool attempted, bool keptBeforeRun, string expected)
    {
        var location = new Opportunity(0, "Build outputs", "Build", @"G:\owned\bin", 10, 10, "", "", "", false);
        Assert.Equal(Enum.Parse<CleanupInspectionDecision>(expected), CleanupInspectionData.Classify(new(location, Enum.Parse<CleanupOutcome>(outcome), "fixture", attempted), keptBeforeRun));
    }

    [Fact]
    public async Task SummaryCountsNestedFolderDataOnceAndRunSkipsStaySeparateFromKeptRules()
    {
        using var fixture = new TestDirectory();
        string parent = Directory.CreateDirectory(Path.Join(fixture.Path, ".venv")).FullName;
        string child = Directory.CreateDirectory(Path.Join(parent, "__pycache__")).FullName;
        File.WriteAllText(Path.Join(parent, "pyvenv.cfg"), "home = fixture");
        File.WriteAllBytes(Path.Join(child, "module.pyc"), new byte[4096]);
        BuildActivityFixture.AgeProject(fixture.Path, 60);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, DeveloperOpportunityFixture.Find(scan), discover: DeveloperOpportunityFixture.Find);
        var plan = CleanupPlanner.ReviewRules(basis, new(30), BuildCleanupAction.Recycle, DeveloperOpportunityFixture.Find);
        Assert.Equal(2, plan.Preview.Locations.Count);
        var initial = CleanupInspectionData.Build(plan);
        Assert.Equal(2, initial.Count); Assert.All(initial, item => Assert.Equal(CleanupInspectionDecision.Ready, item.Decision));
        Assert.Equal(plan.Preview.EstimatedBytes, initial.Sum(item => item.AccountedBytes));
        var omitted = initial.Where(item => item.Location.Path == child).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var edited = CleanupInspectionData.Build(plan, omitted);
        Assert.Equal(CleanupInspectionDecision.Skipped, edited.Single(item => item.Location.Path == child).Decision);
        Assert.Contains("saved rule is unchanged", edited.Single(item => item.Location.Path == child).Reason);
        Assert.Equal(plan.Preview.EstimatedBytes, edited.Sum(item => item.AccountedBytes));
        var safelySkipped = CleanupInspectionData.ChangeSelection(initial, new HashSet<string>(), initial.Single(item => item.Location.Path == child).Location, false);
        var skippedRows = CleanupInspectionData.Build(plan, safelySkipped);
        Assert.All(skippedRows, item => Assert.False(item.Included));
        var childIncluded = CleanupInspectionData.ChangeSelection(skippedRows, safelySkipped, skippedRows.Single(item => item.Location.Path == child).Location, true);
        var childOnly = CleanupInspectionData.Build(plan, childIncluded);
        Assert.Equal(child, Assert.Single(childOnly, item => item.Included).Location.Path);
        var parentIncluded = CleanupInspectionData.ChangeSelection(childOnly, childIncluded, childOnly.Single(item => item.Location.Path == parent).Location, true);
        Assert.All(CleanupInspectionData.Build(plan, parentIncluded), item => Assert.True(item.Included));
        Assert.True(File.Exists(Path.Join(child, "module.pyc")));
    }

    [Fact]
    public async Task ResultSummaryUsesCapturedCohortAndKeepsPreexistingBlocksOutOfSkippedCount()
    {
        using var fixture = new TestDirectory();
        var old = CleanupRulePlanTests.Project(fixture.Path, "old", 60);
        var recent = CleanupRulePlanTests.Project(fixture.Path, "recent", 0);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var basis = CleanupPlanner.PrepareRuleCandidates(scan, DeveloperOpportunityFixture.Find(scan), discover: DeveloperOpportunityFixture.Find);
        var plan = CleanupPlanner.ReviewRules(basis, new(30), BuildCleanupAction.Recycle, DeveloperOpportunityFixture.Find);
        var selected = Assert.Single(plan.Preview.Locations); var blocked = Assert.Single(plan.BlockedLocations);
        var results = new[] { new CleanupLocationResult(selected, CleanupOutcome.Uncertain, "Worker response was lost.", true),
            new CleanupLocationResult(blocked.Opportunity, CleanupOutcome.Skipped, blocked.Reason) };
        var entries = CleanupInspectionData.Build(plan, results: results);
        Assert.Equal(2, entries.Count); Assert.Equal(CleanupInspectionDecision.NeedsChecking, entries.Single(item => item.Location.Path == old.Output).Decision);
        Assert.Equal(CleanupInspectionDecision.Kept, entries.Single(item => item.Location.Path == recent.Output).Decision);
        Assert.DoesNotContain(entries, item => item.Decision == CleanupInspectionDecision.Skipped);
        Assert.All(entries, item => Assert.False(item.CanChoose));
    }

    [Fact]
    public void HistoricalInspectionUsesFrozenMetadataWithoutAnAccessibleDriveOrLivePlan()
    {
        var observed = new DateTimeOffset(2020, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var parent = new Opportunity(10, "Project cache", "Cache", @"Z:\offline\project\cache", 1000, 4096, "Captured contents", "", "", false);
        var child = new Opportunity(11, "Build output", "Build", @"Z:\offline\project\cache\bin", 400, 512, "Captured build", "Rebuild after cleanup.", "", false) { IsBuildOutput = true, IsRegeneratable = true };
        var notStarted = child with { DirectoryIndex = 12, Path = @"Z:\offline\other\bin", LogicalBytes = 200, AllocatedBytes = 256 };
        var record = new CleanupRunRecord
        {
            State = CleanupRunState.Stopped, CreatedUtc = observed, ScannedAtUtc = observed, UsesLogicalSize = true,
            Targets = [new(parent, null, "Kept", "Contains a protected folder."),
                new(child, new(child.Path, new FileIdentity(11), 1, 400, 512, true), "Ready", "Captured for this run.")
                    { Assessment = new(CleanupReadiness.Ready, observed.UtcDateTime.AddDays(-20), "Old enough at review.") },
                new(notStarted, new(notStarted.Path, new FileIdentity(12), 1, 200, 256, true), "Ready", "Not started.")],
            Receipts = [new(child.Path, child.ProviderId, child.RuleId, CleanupOutcome.Recycled, "Moved to the Recycle Bin.", true, observed)]
        };
        var entries = CleanupInspectionData.BuildRecord(record);
        Assert.Equal(3, entries.Count); Assert.Equal(1200, entries.Sum(item => item.AccountedBytes));
        Assert.Equal(CleanupInspectionDecision.Kept, entries.Single(item => item.Location.Path == parent.Path).Decision);
        var completed = entries.Single(item => item.Location.Path == child.Path);
        Assert.Equal(CleanupInspectionDecision.Removed, completed.Decision); Assert.Equal(400, completed.SourceBytes);
        Assert.Equal("7–29 days", completed.Inactivity);
        Assert.Equal(CleanupInspectionDecision.NotStarted, entries.Single(item => item.Location.Path == notStarted.Path).Decision);
        Assert.All(entries, item => { Assert.False(item.CanChoose); Assert.False(item.Included); });
    }
}
