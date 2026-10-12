using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CleanupBatchTests
{
    [Fact]
    public async Task ChangedRulesAndReplacedNativeFoldersAreSkippedBeforeRecycling()
    {
        using var fixture = new TestDirectory();
        var changedRule = CreateProject(fixture.Path, "changed-rule", 'a', 4096);
        var replaced = CreateProject(fixture.Path, "replaced", 'b', 8192);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var plan = CleanupPlanner.Prepare(scan, OpportunityDetector.Find(scan), discover: DeveloperOpportunityFixture.Find);
        Assert.True(plan.CanExecute); Assert.Equal(2, plan.Targets.Count);
        File.Delete(changedRule.Marker);
        string original = replaced.Output + "-original"; Directory.Move(replaced.Output, original);
        Directory.CreateDirectory(replaced.Output);
        string replacementFile = Path.Join(replaced.Output, "new.dll"); File.WriteAllText(replacementFile, "replacement contents");

        var result = await CleanupBatch.ExecuteAsync(plan, 0, new InlineProgress(_ => { }), default, (_, _) => { }, discover: DeveloperOpportunityFixture.Find);

        Assert.Equal(0, result.RecycledCount); Assert.False(result.Cancelled); Assert.Null(result.ValidationError);
        Assert.Equal(2, result.Locations.Count);
        Assert.All(result.Locations, item =>
        { Assert.Equal(CleanupOutcome.Skipped, item.Outcome); Assert.False(item.Attempted); Assert.Null(item.Recycle); });
        Assert.NotNull(result.Validation); Assert.Empty(result.Validation.ValidatedLocations);
        Assert.Equal(changedRule.Content, File.ReadAllText(changedRule.File));
        Assert.Equal(replaced.Content, File.ReadAllText(Path.Join(original, "result.dll")));
        Assert.Equal("replacement contents", File.ReadAllText(replacementFile));
        Assert.Equal(changedRule.SourceContent, File.ReadAllText(changedRule.Source));
        Assert.Equal(replaced.SourceContent, File.ReadAllText(replaced.Source));
    }

    [Fact]
    public async Task LogicalPlansRetainTheirPreconfirmationIdentityWhenAReplacementStillMatchesTheRule()
    {
        using var fixture = new TestDirectory(); var project = CreateProject(fixture.Path, "logical", 'c', 4096);
        var native = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var logical = AsLogical(native);
        var plan = CleanupPlanner.Prepare(logical, OpportunityDetector.Find(logical), discover: DeveloperOpportunityFixture.Find);
        Assert.True(plan.CanExecute); Assert.False(Assert.Single(plan.Targets).HasAllocationData);
        string original = project.Output + "-original"; Directory.Move(project.Output, original);
        Directory.CreateDirectory(project.Output);
        string replacementFile = Path.Join(project.Output, "replacement.dll"); File.WriteAllText(replacementFile, "new identity");

        var result = await CleanupBatch.ExecuteAsync(plan, 0, new InlineProgress(_ => { }), default, (_, _) => { }, discover: DeveloperOpportunityFixture.Find);

        Assert.NotNull(result.Validation);
        Assert.Equal(project.Output, Assert.Single(result.Validation.ValidatedLocations).Path);
        var skipped = Assert.Single(result.Locations); Assert.Equal(CleanupOutcome.Skipped, skipped.Outcome);
        Assert.False(skipped.Attempted); Assert.Null(skipped.Recycle);
        Assert.Contains("replaced since confirmation", skipped.Message, StringComparison.Ordinal);
        Assert.Equal(0, result.RecycledCount); Assert.False(result.Cancelled);
        Assert.Equal(project.Content, File.ReadAllText(Path.Join(original, "result.dll")));
        Assert.Equal("new identity", File.ReadAllText(replacementFile));
        Assert.Equal(project.SourceContent, File.ReadAllText(project.Source));
    }

    [Fact]
    public async Task WindowsBatchCancellationAfterFirstSuccessLeavesRemainingFoldersAndProjectSourceIntact()
    {
        using var fixture = new TestDirectory();
        var first = CreateProject(fixture.Path, "first", 'd', 8192);
        var second = CreateProject(fixture.Path, "second", 'e', 4096);
        var projects = new[] { first, second };
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var plan = CleanupPlanner.Prepare(scan, OpportunityDetector.Find(scan), discover: DeveloperOpportunityFixture.Find);
        Assert.Equal(2, plan.Targets.Count); Assert.Empty(plan.BlockedLocations);
        using var cancellation = new CancellationTokenSource();
        var recycledDestinations = new List<(string Recycled, string Original)>();
        var progress = new InlineProgress(update =>
        { if (update.Completed == 1 && recycledDestinations.Count == 1) cancellation.Cancel(); });
        try
        {
            var result = await CleanupBatch.ExecuteAsync(plan, 0, progress, cancellation.Token, (name, data) =>
            {
                if (name == "cleanup-location-result" && data is CleanupLocationResult item && item.Recycle?.RecycledPath is { } destination)
                    recycledDestinations.Add((destination, item.Location.Path));
            }, discover: DeveloperOpportunityFixture.Find);
            Assert.True(cancellation.IsCancellationRequested); Assert.True(result.Cancelled);
            Assert.Equal(1, result.RecycledCount); Assert.Null(result.ValidationError);
            var moved = Assert.Single(result.Locations, item => item.Outcome == CleanupOutcome.Recycled);
            var remaining = Assert.Single(result.Locations, item => item.Outcome == CleanupOutcome.NotProcessed);
            Assert.True(moved.Attempted); Assert.False(remaining.Attempted); Assert.Null(remaining.Recycle);
            string destination = Assert.IsType<string>(moved.Recycle?.RecycledPath);
            Assert.Equal(destination, Assert.Single(recycledDestinations).Recycled);
            Assert.False(Directory.Exists(moved.Location.Path)); Assert.True(Directory.Exists(remaining.Location.Path));
            var movedProject = Assert.Single(projects, project => project.Output == moved.Location.Path);
            Assert.Equal(movedProject.Content, File.ReadAllText(Path.Join(destination, "result.dll")));
            var remainingProject = Assert.Single(projects, project => project.Output == remaining.Location.Path);
            Assert.Equal(remainingProject.Content, File.ReadAllText(remainingProject.File));
            foreach (var project in projects)
            {
                Assert.Equal(project.SourceContent, File.ReadAllText(project.Source));
                Assert.Equal("<Project />", File.ReadAllText(project.Marker));
            }
        }
        finally
        {
            // Restore only destinations returned for this unique owned fixture.
            // The existing helper verifies its exact $I record before removal.
            foreach (var item in recycledDestinations) DeletionTests.RestoreFixture(item.Recycled, item.Original);
        }
        foreach (var project in projects) Assert.Equal(project.Content, File.ReadAllText(project.File));
    }

    private static ProjectFixture CreateProject(string root, string name, char content, int bytes)
    {
        string project = Directory.CreateDirectory(Path.Join(root, name)).FullName;
        string marker = Path.Join(project, "app.csproj"); File.WriteAllText(marker, "<Project />");
        string source = Path.Join(project, "Program.cs"), sourceContent = "// Authored source " + Guid.NewGuid().ToString("N");
        File.WriteAllText(source, sourceContent);
        string output = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
        string file = Path.Join(output, "result.dll"), contents = new(content, bytes); File.WriteAllText(file, contents);
        return new(marker, source, sourceContent, output, file, contents);
    }
    private static ScanSnapshot AsLogical(ScanSnapshot scan) => new(scan.RootPath, "Basic folder scan", scan.Records,
        scan.Links, scan.NameArena, scan.RootIndex, scan.FirstLinks, scan.ChildHeads, scan.NextLinks, scan.DirectoryLogical,
        scan.DirectoryAllocated, false, false, false, scan.Warnings, scan.Metrics, scan.EntryFlags,
        scan.WarningCount, scan.DirectoryCount, scan.FallbackReason);
    private sealed record ProjectFixture(string Marker, string Source, string SourceContent, string Output, string File, string Content);
    private sealed class InlineProgress(Action<CleanupProgress> callback) : IProgress<CleanupProgress>
    { public void Report(CleanupProgress value) => callback(value); }
}
