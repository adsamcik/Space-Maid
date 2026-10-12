using System.IO;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OpportunityReviewValidatorTests
{
    [Fact]
    public async Task SelectedScopeGetsCurrentSizeWithoutReadingFileContentOrExpandingSiblingFolders()
    {
        using var fixture = new TestDirectory();
        var project = CreateProject(fixture.Path);
        string known = Directory.CreateDirectory(Path.Join(project.Root, "known")).FullName;
        string knownFile = Path.Join(known, "old.dat"); File.WriteAllBytes(knownFile, new byte[333]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var selected = Assert.Single(OpportunityDetector.Find(scan), opportunity => opportunity.Path == project.Output);
        File.WriteAllBytes(project.File, new byte[20000]);
        File.WriteAllBytes(knownFile, new byte[7777]);
        string added = Directory.CreateDirectory(Path.Join(project.Root, "new-sibling")).FullName;
        string addedFile = Path.Join(added, "not-in-selected-scope.dat"); File.WriteAllBytes(addedFile, new byte[65536]);
        var events = new List<string>();
        OpportunityReviewValidation result;
        // A data read cannot open this file while validation runs. Directory
        // metadata can still report its new length and native identity.
        using (var locked = File.Open(project.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await OpportunityReviewValidator.ValidateAsync(scan, [selected], diagnostic: (name, _) => events.Add(name), discover: DeveloperOpportunityFixture.Find);
        var fresh = Assert.Single(result.ValidatedLocations);
        Assert.Equal(20000, fresh.LogicalBytes); Assert.Equal(selected.Title, fresh.Title);
        Assert.True(result.CanMergeInventory); Assert.Empty(result.ExcludedPaths);
        Assert.Contains(result.RecheckedScopes, scope => scope.Path == project.Root && !scope.Recursive);
        Assert.Contains(result.RecheckedScopes, scope => scope.Path == project.Output && scope.Recursive);
        Assert.Null(result.Snapshot.FindEntry(addedFile));
        Assert.Contains(result.UncheckedScopes, scope => scope.Path == added && scope.Recursive);
        var addedEntry = result.Snapshot.Entry(result.Snapshot.FindEntry(added)!.Value);
        Assert.True(addedEntry.IsIncomplete); Assert.True(result.Snapshot.IsUnmeasuredDirectory(addedEntry.FileIndex));
        Assert.True(result.Snapshot.RootEntry.IsIncomplete); Assert.False(result.Snapshot.IsUnmeasuredDirectory(result.Snapshot.RootIndex));
        Assert.False(result.Snapshot.Entry(result.Snapshot.FindEntry(project.Output)!.Value).IsIncomplete);
        Assert.Equal(scan.WarningCount, result.Snapshot.WarningCount);
        Assert.Equal(333, result.Snapshot.Entry(result.Snapshot.FindEntry(knownFile)!.Value).LogicalBytes);
        Assert.Equal(4096, scan.Entry(scan.FindEntry(project.File)!.Value).LogicalBytes);
        Assert.Equal(scan.ScannedAtUtc, result.Snapshot.ScannedAtUtc);
        Assert.Contains("opportunity-validation-complete", events);
        var preview = CleanupPlanner.Create(result.Snapshot, result.ValidatedLocations);
        Assert.Equal(20000, new FileInfo(project.File).Length);
        var rechecked = await ScanRechecker.RefreshAsync(result.Snapshot, [new(added)]);
        Assert.Equal(65536, rechecked.Entry(rechecked.FindEntry(addedFile)!.Value).LogicalBytes);
        var measured = rechecked.Entry(rechecked.FindEntry(added)!.Value);
        Assert.False(measured.IsIncomplete); Assert.False(rechecked.IsUnmeasuredDirectory(measured.FileIndex));
        Assert.False(rechecked.RootEntry.IsIncomplete); Assert.Equal(scan.WarningCount, rechecked.WarningCount);
    }

    [Fact]
    public async Task RemovedProjectEvidenceExcludesThePreviouslySelectedBuildOutput()
    {
        using var fixture = new TestDirectory(); var project = CreateProject(fixture.Path);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var selected = Assert.Single(OpportunityDetector.Find(scan));
        File.Delete(project.Marker);
        var result = await OpportunityReviewValidator.ValidateAsync(scan, [selected], discover: DeveloperOpportunityFixture.Find);
        Assert.Empty(result.ValidatedLocations); Assert.Contains(project.Output, result.ExcludedPaths);
        Assert.DoesNotContain(result.Findings, opportunity => opportunity.Path == project.Output);
        Assert.NotNull(scan.FindEntry(project.Marker)); Assert.Null(result.Snapshot.FindEntry(project.Marker));
        Assert.Equal(4096, new FileInfo(project.File).Length);
    }

    [Fact]
    public async Task DeletedAndReplacedSelectedFoldersAreExcludedAndNewFindingsAreNotSelected()
    {
        using var fixture = new TestDirectory(); var project = CreateProject(fixture.Path);
        string obj = Directory.CreateDirectory(Path.Join(project.Root, "obj")).FullName;
        File.WriteAllBytes(Path.Join(obj, "old.dat"), new byte[8192]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var selected = OpportunityDetector.Find(scan);
        Directory.Delete(project.Output, recursive: true);
        string moved = Path.Join(project.Root, "retained-old-obj"); Directory.Move(obj, moved);
        Directory.CreateDirectory(obj); File.WriteAllBytes(Path.Join(obj, "replacement.dat"), new byte[16384]);
        var result = await OpportunityReviewValidator.ValidateAsync(scan, selected, discover: DeveloperOpportunityFixture.Find);
        Assert.Empty(result.ValidatedLocations);
        Assert.Contains(project.Output, result.ExcludedPaths); Assert.Contains(obj, result.ExcludedPaths);
        Assert.Contains(result.Findings, opportunity => opportunity.Path == obj && opportunity.LogicalBytes == 16384);
        Assert.Null(result.Snapshot.FindEntry(project.Output));
        Assert.Contains(result.UncheckedScopes, scope => scope.Path == moved);
        Assert.Equal(8192, new FileInfo(Path.Join(moved, "old.dat")).Length);
        Assert.Equal(16384, new FileInfo(Path.Join(obj, "replacement.dat")).Length);
    }

    [Fact]
    public async Task MissingSelectedParentUsesOnlyTheNearestKnownLiveParentShallowly()
    {
        using var fixture = new TestDirectory(); var project = CreateProject(fixture.Path);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var selected = Assert.Single(OpportunityDetector.Find(scan));
        Directory.Delete(project.Root, recursive: true);
        string sibling = Directory.CreateDirectory(Path.Join(fixture.Path, "new-repository")).FullName;
        string nested = Directory.CreateDirectory(Path.Join(sibling, "nested")).FullName;
        File.WriteAllBytes(Path.Join(nested, "data.bin"), new byte[8192]);
        var result = await OpportunityReviewValidator.ValidateAsync(scan, [selected], discover: DeveloperOpportunityFixture.Find);
        var scope = Assert.Single(result.RecheckedScopes);
        Assert.Equal(Path.TrimEndingDirectorySeparator(fixture.Path), scope.Path); Assert.False(scope.Recursive);
        Assert.Empty(result.ValidatedLocations); Assert.Contains(project.Output, result.ExcludedPaths);
        Assert.Null(result.Snapshot.FindEntry(nested));
        Assert.Contains(result.UncheckedScopes, value => value.Path == sibling && value.Recursive);
    }

    [Fact]
    public async Task BasicScanGetsFreshLogicalSelectionAndKeepsOriginalInventoryUntouched()
    {
        using var fixture = new TestDirectory(); var project = CreateProject(fixture.Path);
        string sibling = Directory.CreateDirectory(Path.Join(project.Root, "unselected")).FullName;
        string siblingFile = Path.Join(sibling, "data.bin"); File.WriteAllBytes(siblingFile, new byte[777]);
        var native = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var scan = AsLogical(native);
        var selected = Assert.Single(OpportunityDetector.Find(scan)); long originalBytes = scan.LogicalBytes;
        File.WriteAllBytes(project.File, new byte[12345]); File.WriteAllBytes(siblingFile, new byte[8888]);
        var result = await OpportunityReviewValidator.ValidateAsync(scan, [selected], discover: DeveloperOpportunityFixture.Find);
        Assert.False(result.CanMergeInventory); Assert.False(result.Snapshot.HasAllocationData);
        Assert.False(result.Snapshot.IncludesAllDataStreams); Assert.False(result.Snapshot.HasCompleteVolumeLinks);
        var metadata = result.Snapshot.GetMetadata(result.Snapshot.FindEntry(project.Output)!.Value);
        Assert.Null(metadata.NativeIdLow); Assert.Null(metadata.NativeIdHigh);
        Assert.Equal(12345, Assert.Single(result.ValidatedLocations).LogicalBytes);
        Assert.Null(result.Snapshot.FindEntry(siblingFile)); Assert.NotNull(scan.FindEntry(siblingFile));
        Assert.Equal(originalBytes, scan.LogicalBytes); Assert.Equal(4096, scan.Entry(scan.FindEntry(project.File)!.Value).LogicalBytes);
        Assert.Equal(777, scan.Entry(scan.FindEntry(siblingFile)!.Value).LogicalBytes);
        Assert.Null(scan.LastRecheckUtc); Assert.Empty(result.UncheckedScopes);
        Assert.Contains(result.Snapshot.Warnings, warning => warning.Contains("Physical allocation and hard-link identity are unknown", StringComparison.Ordinal));
        var preview = CleanupPlanner.Create(result.Snapshot, result.ValidatedLocations);
        Assert.True(preview.UsesLogicalSize); Assert.Equal(12345, preview.EstimatedBytes);
    }

    [Fact]
    public async Task CancellationAfterAParentCheckDoesNotPublishOrMutatePartialResults()
    {
        using var fixture = new TestDirectory(); var project = CreateProject(fixture.Path);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var selected = Assert.Single(OpportunityDetector.Find(scan));
        File.WriteAllBytes(project.File, new byte[20000]);
        using var cancellation = new CancellationTokenSource(); int completed = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpportunityReviewValidator.ValidateAsync(scan, [selected], cancellation.Token,
            (name, _) => { if (name == "opportunity-validation-read-complete") { completed++; cancellation.Cancel(); } }, discover: DeveloperOpportunityFixture.Find));
        Assert.Equal(1, completed); Assert.Null(scan.LastRecheckUtc);
        Assert.Equal(4096, scan.Entry(scan.FindEntry(project.File)!.Value).LogicalBytes);
        Assert.Equal(20000, new FileInfo(project.File).Length);
    }

    [Fact]
    public void LargeSelectionPlanningCollapsesOnlyRecursiveDescendantsAndHonorsCancellation()
    {
        string root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "SpaceMaid-planning"));
        var requests = new List<ScanRecheckRequest> { new(root, false) };
        for (int i = 0; i < 4000; i++)
        {
            string project = Path.Join(root, "project-" + i);
            requests.Add(new(project, false)); requests.Add(new(Path.Join(project, "bin")));
            requests.Add(new(Path.Join(project, "bin", "nested"), false));
            requests.Add(new(Path.Join(project, "bin", "."), false));
        }
        string selectedProject = Path.Join(root, "project-0"); requests.Add(new(selectedProject));
        var planned = OpportunityReviewValidator.PlanScopes(requests, CancellationToken.None);
        Assert.Equal(8000, planned.Length);
        Assert.Contains(planned, value => value.Path == root && !value.Recursive);
        Assert.Contains(planned, value => value.Path == selectedProject && value.Recursive);
        Assert.DoesNotContain(planned, value => value.Path.StartsWith(selectedProject + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Contains(planned, value => value.Path == Path.Join(root, "project-3999") && !value.Recursive);
        Assert.Contains(planned, value => value.Path == Path.Join(root, "project-3999", "bin") && value.Recursive);
        using var cancellation = new CancellationTokenSource(); int consumed = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => OpportunityReviewValidator.PlanScopes(CancelDuringPlanning(), cancellation.Token));
        Assert.Equal(32, consumed);

        IEnumerable<ScanRecheckRequest> CancelDuringPlanning()
        {
            foreach (var request in requests)
            {
                if (++consumed == 32) cancellation.Cancel();
                yield return request;
            }
        }
    }

    private static (string Root, string Output, string Marker, string File) CreateProject(string root)
    {
        string project = Directory.CreateDirectory(Path.Join(root, "project")).FullName;
        string marker = Path.Join(project, "app.csproj"); File.WriteAllText(marker, "<Project />");
        string output = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
        string file = Path.Join(output, "result.dll"); File.WriteAllBytes(file, new byte[4096]);
        return (project, output, marker, file);
    }
    private static ScanSnapshot AsLogical(ScanSnapshot scan) => new(scan.RootPath, "Basic folder scan", scan.Records,
        scan.Links, scan.NameArena, scan.RootIndex, scan.FirstLinks, scan.ChildHeads, scan.NextLinks, scan.DirectoryLogical,
        scan.DirectoryAllocated, false, false, false, scan.Warnings, scan.Metrics, scan.EntryFlags,
        scan.WarningCount, scan.DirectoryCount, scan.FallbackReason);
}
