using System.IO;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CleanupPlanTests
{
    [Fact]
    public async Task PreparationCapturesAnExecutableIdentityWithoutMutatingFilesAndKeepsCollectionsReadOnly()
    {
        using var fixture = new TestDirectory(); var project = CreateProject(fixture.Path, "project", 8192);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var locations = OpportunityDetector.Find(scan).ToList(); DateTime written = File.GetLastWriteTimeUtc(project.File);
        CleanupPlan plan;
        using (var locked = File.Open(project.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            plan = CleanupPlanner.Prepare(scan, locations);
        var target = Assert.Single(plan.Targets); Assert.Equal(project.Output, target.Path); target.ValidateCurrent();
        Assert.Same(scan, plan.Snapshot); Assert.True(plan.CanExecute); Assert.Empty(plan.BlockedLocations);
        Assert.Equal(8192, plan.Preview.EstimatedBytes); Assert.Single(plan.Preview.Locations);
        locations.Clear(); Assert.Single(plan.Preview.Locations);
        Assert.True(Assert.IsAssignableFrom<IList<FolderDeletionTarget>>(plan.Targets).IsReadOnly);
        Assert.True(Assert.IsAssignableFrom<IList<CleanupBlockedLocation>>(plan.BlockedLocations).IsReadOnly);
        Assert.True(Assert.IsAssignableFrom<IList<Opportunity>>(plan.Preview.Locations).IsReadOnly);
        Assert.True(Assert.IsAssignableFrom<IList<string>>(plan.Preview.Cautions).IsReadOnly);
        var cautions = new List<string> { "keep" }; var copied = new CleanupPreview(plan.Preview.Locations, 1, 0, 1, false, cautions);
        cautions.Clear(); Assert.Equal("keep", Assert.Single(copied.Cautions));
        Assert.Contains(plan.Preview.Cautions, caution => caution.Contains("until the Recycle Bin is emptied", StringComparison.Ordinal));
        Assert.Equal(8192, new FileInfo(project.File).Length); Assert.Equal(written, File.GetLastWriteTimeUtc(project.File));
    }

    [Fact]
    public async Task MissingReplacedAndForgedLocationsAreBlockedIndependentlyOfValidTargets()
    {
        using var fixture = new TestDirectory();
        var valid = CreateProject(fixture.Path, "valid", 4096);
        var missing = CreateProject(fixture.Path, "missing", 8192);
        var replaced = CreateProject(fixture.Path, "replaced", 16384);
        string generic = Directory.CreateDirectory(Path.Join(fixture.Path, "authored-files")).FullName;
        string sentinel = Path.Join(generic, "keep.txt"); File.WriteAllText(sentinel, "authored content");
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var locations = OpportunityDetector.Find(scan);
        var validLocation = Assert.Single(locations, value => value.Path == valid.Output);
        var missingLocation = Assert.Single(locations, value => value.Path == missing.Output);
        var replacedLocation = Assert.Single(locations, value => value.Path == replaced.Output);
        Directory.Delete(missing.Output, recursive: true);
        string old = replaced.Output + "-original"; Directory.Move(replaced.Output, old);
        Directory.CreateDirectory(replaced.Output); File.WriteAllBytes(Path.Join(replaced.Output, "new.dll"), new byte[32768]);
        var forged = validLocation with { DirectoryIndex = scan.Entry(scan.FindEntry(generic)!.Value).FileIndex, Path = generic };
        var malformed = validLocation with { Title = "Pretend cache" };
        var plan = CleanupPlanner.Prepare(scan, [validLocation, missingLocation, replacedLocation, forged, malformed]);
        Assert.True(plan.CanExecute); Assert.Equal(valid.Output, Assert.Single(plan.Targets).Path);
        Assert.Equal(4096, plan.Preview.EstimatedBytes); Assert.Single(plan.Preview.Locations);
        Assert.Equal(4, plan.BlockedLocations.Count);
        Assert.Contains(plan.BlockedLocations, value => ReferenceEquals(value.Opportunity, missingLocation) && !string.IsNullOrWhiteSpace(value.Reason));
        Assert.Contains(plan.BlockedLocations, value => ReferenceEquals(value.Opportunity, replacedLocation) && value.Reason.Contains("changed", StringComparison.Ordinal));
        Assert.Contains(plan.BlockedLocations, value => ReferenceEquals(value.Opportunity, forged));
        Assert.Contains(plan.BlockedLocations, value => ReferenceEquals(value.Opportunity, malformed));
        Assert.Equal("authored content", File.ReadAllText(sentinel));
        Assert.Equal(16384, new FileInfo(Path.Join(old, "result.dll")).Length);
        Assert.Equal(32768, new FileInfo(Path.Join(replaced.Output, "new.dll")).Length);
        Assert.Equal(4096, new FileInfo(valid.File).Length);
        var allBlocked = CleanupPlanner.Prepare(scan, [missingLocation, forged]);
        Assert.False(allBlocked.CanExecute); Assert.Empty(allBlocked.Targets); Assert.Empty(allBlocked.Preview.Locations);
    }

    [Fact]
    public async Task NestedRecognizedTargetsCollapseToTheOuterFolder()
    {
        using var fixture = new TestDirectory();
        string env = Directory.CreateDirectory(Path.Join(fixture.Path, ".venv")).FullName;
        string marker = Path.Join(env, "pyvenv.cfg"); File.WriteAllText(marker, "home = fixture");
        string cache = Directory.CreateDirectory(Path.Join(env, "__pycache__")).FullName;
        string file = Path.Join(cache, "module.pyc"); File.WriteAllBytes(file, new byte[4096]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var selected = OpportunityDetector.Find(scan); Assert.Equal(2, selected.Count);
        var plan = CleanupPlanner.Prepare(scan, selected);
        Assert.True(plan.CanExecute); Assert.Empty(plan.BlockedLocations);
        Assert.Equal(env, Assert.Single(plan.Targets).Path); Assert.Equal(env, Assert.Single(plan.Preview.Locations).Path);
        Assert.Equal(scan.Entry(scan.FindEntry(env)!.Value).AllocatedBytes, plan.Preview.EstimatedBytes);
        Assert.Equal(4096, new FileInfo(file).Length); Assert.Equal("home = fixture", File.ReadAllText(marker));
    }

    [Fact]
    public async Task LogicalInventoryCanCaptureLiveIdentityButProtectedAndReparseTargetsCannotExecute()
    {
        using var fixture = new TestDirectory(); var project = CreateProject(fixture.Path, "project", 12345);
        var native = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var logical = AsLogical(native);
        var plan = CleanupPlanner.Prepare(logical, OpportunityDetector.Find(logical));
        Assert.True(plan.CanExecute); Assert.True(plan.Preview.UsesLogicalSize); Assert.Equal(12345, plan.Preview.EstimatedBytes);
        var target = Assert.Single(plan.Targets); Assert.False(target.HasAllocationData); target.ValidateCurrent();
        var metadata = logical.GetMetadata(logical.FindEntry(project.Output)!.Value);
        Assert.Null(metadata.NativeIdLow); Assert.Null(metadata.NativeIdHigh);

        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int rootMarker = builder.AddFile(new(new(2), FileAttributes.Normal, 0, 0, 0)); builder.AddLink(rootMarker, new(1), "pyvenv.cfg");
        int linked = builder.AddFile(new(new(3), FileAttributes.Directory | FileAttributes.ReparsePoint, 0, 0, 0)); builder.AddLink(linked, new(1), "linked");
        int linkedMarker = builder.AddFile(new(new(4), FileAttributes.Normal, 0, 0, 0)); builder.AddLink(linkedMarker, new(3), "pyvenv.cfg");
        var guarded = builder.Build(@"C:\", "fixture", TimeSpan.Zero, 0, default);
        var found = OpportunityDetector.Find(guarded);
        var rootPlan = CleanupPlanner.Prepare(guarded, found.Where(value => value.DirectoryIndex == guarded.RootIndex));
        Assert.False(rootPlan.CanExecute); Assert.Contains("Drive roots", Assert.Single(rootPlan.BlockedLocations).Reason);
        var linkPlan = CleanupPlanner.Prepare(guarded, found.Where(value => value.DirectoryIndex != guarded.RootIndex));
        Assert.False(linkPlan.CanExecute); Assert.Contains("reparse", Assert.Single(linkPlan.BlockedLocations).Reason);
        Assert.Equal(12345, new FileInfo(project.File).Length);
    }

    [Fact]
    public async Task CancellationDuringSelectionDoesNotReturnAnExecutablePlanOrMutateData()
    {
        using var fixture = new TestDirectory(); var project = CreateProject(fixture.Path, "project", 4096);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var selected = Assert.Single(OpportunityDetector.Find(scan));
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => CleanupPlanner.Prepare(scan, CancelSelection(), cancellation.Token));
        Assert.Equal(4096, new FileInfo(project.File).Length); Assert.True(Directory.Exists(project.Output));

        IEnumerable<Opportunity> CancelSelection()
        { yield return selected; cancellation.Cancel(); yield return selected; }
    }

    private static (string Output, string File) CreateProject(string root, string name, int bytes)
    {
        string project = Directory.CreateDirectory(Path.Join(root, name)).FullName;
        File.WriteAllText(Path.Join(project, "app.csproj"), "<Project />");
        string output = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
        string file = Path.Join(output, "result.dll"); File.WriteAllBytes(file, new byte[bytes]);
        return (output, file);
    }
    private static ScanSnapshot AsLogical(ScanSnapshot scan) => new(scan.RootPath, "Basic folder scan", scan.Records,
        scan.Links, scan.NameArena, scan.RootIndex, scan.FirstLinks, scan.ChildHeads, scan.NextLinks, scan.DirectoryLogical,
        scan.DirectoryAllocated, false, false, false, scan.Warnings, scan.Metrics, scan.EntryFlags,
        scan.WarningCount, scan.DirectoryCount, scan.FallbackReason);
}
