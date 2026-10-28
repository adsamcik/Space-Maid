using System.IO;
using System.Diagnostics;
using System.Text.Json;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class IncrementalNtfsTests
{
    [Fact]
    public async Task EmptyJournalStillRefreshesCurrentSizesAndNamedStreamsWithoutWalkingDirectories()
    {
        using var fixture = new TestDirectory();
        string nested = Directory.CreateDirectory(Path.Join(fixture.Path, "nested")).FullName;
        string file = Path.Join(nested, "payload.dat"); File.WriteAllBytes(file, new byte[4096]);
        var previous = await Capture(fixture.Path);
        File.WriteAllBytes(file, new byte[8192]); File.WriteAllBytes(file + ":extra", new byte[4096]);
        var source = new JournalMetadataSource(await Measure(fixture.Path));
        var events = new Events();
        var current = await new VolumeScanner(source).MeasureFolderAsync(fixture.Path, diagnostic: events.Record, previous: previous);
        Assert.Equal(4096, previous.LogicalBytes); Assert.Equal(12288, current.LogicalBytes);
        Assert.Equal(12288, current.Entry(current.FindEntry(file)!.Value).LogicalBytes);
        Assert.Equal(0, events.CompletedDirectories());
        Assert.Empty(events.DirectoryReads());
        Assert.Equal(previous.FileCount, source.Requests.SelectMany(ids => ids).Distinct().Count());
        Assert.Equal(0, source.WholeVolumeCalls);
    }

    [Fact]
    public async Task MembershipChangesReadOnlyDirtyParentsAndNewSubtrees()
    {
        using var fixture = new TestDirectory();
        string dirty = Directory.CreateDirectory(Path.Join(fixture.Path, "dirty")).FullName;
        string stable = Directory.CreateDirectory(Path.Join(fixture.Path, "stable", "nested")).FullName;
        string removed = Path.Join(dirty, "removed.dat"); File.WriteAllBytes(removed, new byte[4096]);
        File.WriteAllBytes(Path.Join(stable, "keep.dat"), new byte[8192]);
        var previous = await Capture(fixture.Path);
        File.Delete(removed);
        string introduced = Directory.CreateDirectory(Path.Join(dirty, "introduced")).FullName;
        string newBranch = Directory.CreateDirectory(Path.Join(introduced, "deep")).FullName;
        string added = Path.Join(newBranch, "new.dat"); File.WriteAllBytes(added, new byte[4096]);
        var fresh = await Measure(fixture.Path);
        var source = new JournalMetadataSource(fresh);
        source.Change(150, Id(previous, removed), Id(previous, dirty), 0x200);
        source.Change(151, Id(fresh, introduced), Id(fresh, dirty), 0x100, FileAttributes.Directory);
        source.Change(152, Id(fresh, added), Id(fresh, newBranch), 0x100);
        var events = new Events();
        var current = await new VolumeScanner(source).MeasureFolderAsync(fixture.Path, diagnostic: events.Record, previous: previous);
        Assert.Null(current.FindEntry(removed)); Assert.NotNull(current.FindEntry(added));
        Assert.Equal(fresh.LogicalBytes, current.LogicalBytes);
        Assert.Equal(fresh.FileCount, current.FileCount);
        Assert.Equal(new[] { dirty, introduced }.Order(), events.DirectoryReads().Order());
        Assert.DoesNotContain(events.DirectoryReads(), path => path.StartsWith(stable, StringComparison.Ordinal));
        Assert.Equal(3, events.CompletedDirectories());
        Assert.Equal(0, source.WholeVolumeCalls);
    }

    [Fact]
    public async Task ADirectoryMoveReusesTheCapturedTreeAtItsNewPath()
    {
        using var fixture = new TestDirectory();
        string before = Directory.CreateDirectory(Path.Join(fixture.Path, "before")).FullName;
        string nested = Directory.CreateDirectory(Path.Join(before, "nested")).FullName;
        File.WriteAllBytes(Path.Join(nested, "keep.dat"), new byte[4096]);
        var previous = await Capture(before);
        string after = Path.Join(fixture.Path, "after"); Directory.Move(before, after);
        var fresh = await Measure(after);
        var source = new JournalMetadataSource(fresh);
        // The moved root's parent is outside the captured subtree. Its children
        // retain both their IDs and their parent-directory IDs.
        source.Change(150, Id(previous, before), new(99999), 0x1000, FileAttributes.Directory);
        source.Change(151, Id(previous, before), new(99999), 0x2000, FileAttributes.Directory);
        var events = new Events();
        var current = await new VolumeScanner(source).MeasureFolderAsync(after, diagnostic: events.Record, previous: previous);
        Assert.Equal(after, current.RootPath);
        Assert.NotNull(current.FindEntry(Path.Join(after, "nested", "keep.dat")));
        Assert.Equal(previous.LogicalBytes, current.LogicalBytes);
        Assert.Equal(0, events.CompletedDirectories()); Assert.Empty(events.DirectoryReads());
        Assert.Equal(0, source.WholeVolumeCalls);
    }

    [Fact]
    public async Task RefreshingOneFolderDoesNotConsumeAnUntouchedSiblingsChangeHistory()
    {
        using var fixture = new TestDirectory();
        string first = Directory.CreateDirectory(Path.Join(fixture.Path, "first")).FullName;
        string sibling = Directory.CreateDirectory(Path.Join(fixture.Path, "sibling")).FullName;
        File.WriteAllBytes(Path.Join(first, "keep.dat"), new byte[4096]);
        var previous = await Capture(fixture.Path);
        string newFile = Path.Join(sibling, "new.dat"); File.WriteAllBytes(newFile, new byte[8192]);
        var fresh = await Measure(fixture.Path);
        var source = new JournalMetadataSource(fresh);
        source.Change(150, Id(fresh, newFile), Id(fresh, sibling), 0x100);
        var firstRefresh = await ScanRechecker.RefreshAsync(previous, [new(first)], layoutSource: source);
        Assert.Null(firstRefresh.FindEntry(newFile));
        Assert.Contains(firstRefresh.JournalScopes, scope => scope.Path == fixture.Path && scope.Checkpoint.NextUsn == 100);
        Assert.Contains(firstRefresh.JournalScopes, scope => scope.Path == first && scope.Checkpoint.NextUsn == 200);
        var siblingEvents = new Events();
        var siblingRefresh = await ScanRechecker.RefreshAsync(firstRefresh, [new(sibling)],
            diagnostic: siblingEvents.Record, layoutSource: source);
        Assert.NotNull(siblingRefresh.FindEntry(newFile));
        Assert.Equal(8192, siblingRefresh.Entry(siblingRefresh.FindEntry(sibling)!.Value).LogicalBytes);
        Assert.Equal(1, siblingEvents.CompletedDirectories());
        Assert.Equal(0, source.WholeVolumeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingHistoryAndAmbiguousHardLinksFallBackOnlyWithinTheRequestedFolder(bool hardLink)
    {
        using var fixture = new TestDirectory();
        string selected = Directory.CreateDirectory(Path.Join(fixture.Path, "selected")).FullName;
        string outside = Directory.CreateDirectory(Path.Join(fixture.Path, "outside")).FullName;
        File.WriteAllBytes(Path.Join(selected, "keep.dat"), new byte[4096]);
        File.WriteAllBytes(Path.Join(outside, "untouched.dat"), new byte[8192]);
        var previous = await Capture(fixture.Path);
        var fresh = await Measure(fixture.Path);
        var source = new JournalMetadataSource(fresh) { Expired = !hardLink };
        if (hardLink) source.Change(150, new(99999), new(99998), 0x10000);
        var events = new Events();
        var current = await new VolumeScanner(source).MeasureFolderAsync(selected, diagnostic: events.Record, previous: previous);
        Assert.Equal(selected, current.RootPath); Assert.Equal(4096, current.LogicalBytes);
        Assert.Contains(events.Items, item => item.Event == "incremental-ntfs-fallback");
        Assert.DoesNotContain(events.Items, item => item.Event == "incremental-ntfs-complete");
        Assert.All(source.Requests.SelectMany(ids => ids), id => Assert.NotEqual(Id(fresh, outside), id));
        Assert.Null(current.FindEntry(Path.Join(outside, "untouched.dat")));
        Assert.Equal(0, source.WholeVolumeCalls);
    }

    [Fact]
    public async Task NamespaceChangesDuringIdQueriesCannotPublishTheOldCachedMembership()
    {
        using var fixture = new TestDirectory();
        File.WriteAllBytes(Path.Join(fixture.Path, "keep.dat"), new byte[4096]);
        var previous = await Capture(fixture.Path);
        var source = new JournalMetadataSource(await Measure(fixture.Path));
        string added = Path.Join(fixture.Path, "added-during-query.dat");
        source.BeforeQuery = () =>
        {
            File.WriteAllBytes(added, new byte[8192]);
            var changed = Measure(fixture.Path).GetAwaiter().GetResult();
            source.Update(changed); source.CurrentUsn = 300;
            source.Change(250, Id(changed, added), Id(changed, fixture.Path), 0x100);
        };
        var events = new Events();
        var current = await new VolumeScanner(source).MeasureFolderAsync(fixture.Path, diagnostic: events.Record, previous: previous);
        Assert.NotNull(current.FindEntry(added)); Assert.Equal(12288, current.LogicalBytes);
        Assert.Contains(events.Items, item => item.Event == "incremental-ntfs-fallback");
        Assert.DoesNotContain(events.Items, item => item.Event == "incremental-ntfs-complete");
        Assert.Equal(0, source.WholeVolumeCalls);
    }

    [Fact]
    public async Task ANewJournalGenerationUsesTheRefreshedScopeOnTheNextRecheck()
    {
        using var fixture = new TestDirectory();
        string selected = Directory.CreateDirectory(Path.Join(fixture.Path, "selected")).FullName;
        File.WriteAllBytes(Path.Join(selected, "keep.dat"), new byte[4096]);
        var previous = await Capture(fixture.Path);
        previous.JournalScopes = [new(fixture.Path, true, new(7, 999, 10000))];
        var source = new JournalMetadataSource(await Measure(fixture.Path));
        var refreshed = await ScanRechecker.RefreshAsync(previous, [new(selected)], layoutSource: source);
        var events = new Events();
        var repeated = await ScanRechecker.RefreshAsync(refreshed, [new(selected)], diagnostic: events.Record, layoutSource: source);
        Assert.Equal(4096, repeated.Entry(repeated.FindEntry(selected)!.Value).LogicalBytes);
        Assert.Equal(0, events.CompletedDirectories());
    }

    [Fact]
    [Trait("Stage", "System")]
    public async Task NativeVolumeNamespaceCannotHideAnUnsupportedJunctionsCoverageWarning()
    {
        using var fixture = new TestDirectory();
        using var outside = new TestDirectory();
        File.WriteAllText(Path.Join(outside.Path, "keep.dat"), "owned outside fixture");
        string junction = Path.Join(fixture.Path, "linked");
        var start = new ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junction); start.ArgumentList.Add(outside.Path);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        try
        {
            var measured = await Measure(fixture.Path);
            // Raw MFT acquisition records the junction itself without walking
            // it. Unlike a recursive directory scan, it has no omission flag.
            var builder = new ScanBuilder { AllStreams = true, CompleteVolumeLinks = true };
            foreach (var record in measured.Records) builder.AddFile(record);
            builder.RootIndex = measured.RootIndex;
            foreach (var link in measured.Links) builder.AddLink(link.FileIndex, link.ParentIdentity,
                measured.NameArena.AsSpan(link.NameOffset, link.NameLength));
            var previous = builder.Build(fixture.Path, "Fast NTFS scan", TimeSpan.Zero, 0, default);
            VolumeScanner.AttachJournal(previous, true, new(7, 1000, 100));
            Assert.Equal(0, previous.WarningCount);
            var source = new JournalMetadataSource(measured);
            var events = new Events();
            var current = await new VolumeScanner(source).MeasureFolderAsync(fixture.Path, diagnostic: events.Record, previous: previous);
            Assert.True(current.WarningCount > 0); Assert.True(current.RootEntry.IsIncomplete);
            Assert.Null(current.FindEntry(Path.Join(junction, "keep.dat")));
            Assert.DoesNotContain(events.Items, item => item.Event == "incremental-ntfs-complete");
            Assert.Equal("owned outside fixture", File.ReadAllText(Path.Join(outside.Path, "keep.dat")));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public async Task RecyclingCleanupReviewAndBuildActivityShareFreshIncrementalMetadata()
    {
        using var fixture = new TestDirectory();
        string project = Directory.CreateDirectory(Path.Join(fixture.Path, "project")).FullName;
        File.WriteAllText(Path.Join(project, "project.csproj"), "<Project />");
        string output = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
        string file = Path.Join(output, "result.dll"); File.WriteAllBytes(file, new byte[4096]);
        BuildActivityFixture.AgeProject(project);
        var previous = await Capture(fixture.Path);
        var selected = Assert.Single(DeveloperOpportunityFixture.Find(previous), item => item.Path == output);
        Assert.Equal(CleanupReadiness.Ready, BuildCleanupProtection.Assess(previous, [selected], new())[selected.DirectoryIndex].State);
        File.WriteAllBytes(file, new byte[8192]); File.WriteAllBytes(file + ":extra", new byte[4096]);
        var source = new JournalMetadataSource(await Measure(fixture.Path));
        var operations = new FileOperations(() => source, inventory: () => previous);
        var events = new Events();
        var recycled = await operations.PrepareRecycleAsync(previous, previous.FindEntry(output)!.Value, default, events.Record);
        Assert.Equal(output, recycled.Path); Assert.Equal(12288, recycled.LogicalBytes); recycled.ValidateCurrent();
        Assert.Equal(0, events.CompletedDirectories());
        var reviewEvents = new Events();
        var review = await operations.ReviewCleanupAsync(previous, [selected], diagnostic: reviewEvents.Record,
            discover: DeveloperOpportunityFixture.Find);
        Assert.Equal(12288, Assert.Single(review.ValidatedLocations).LogicalBytes);
        Assert.Contains(reviewEvents.Items, item => item.Event == "incremental-ntfs-complete");
        Assert.All(reviewEvents.Items.Where(item => item.Event == "incremental-ntfs-complete"),
            item => Assert.Equal(0, item.Data.GetProperty("DirectoriesRead").GetInt32()));
        var activity = await operations.CheckBuildActivityAsync(selected, new(), previous: previous);
        Assert.Equal(CleanupReadiness.Protected, activity.State);
        Assert.Contains("Activity within", activity.Reason);
        var sizes = new ExtensionInventory(() => previous, () => true, () => new Lease(), operations: operations);
        var refreshedSize = await sizes.ReadFolderSizeAsync(output, refresh: true, default);
        Assert.NotNull(refreshedSize); Assert.True(refreshedSize.Complete);
        Assert.Equal(12288, refreshedSize.LogicalBytes); Assert.Equal("Incremental NTFS metadata", refreshedSize.Source);
        Assert.Equal(8192, new FileInfo(file).Length); Assert.Equal(0, source.WholeVolumeCalls);
    }

    private sealed class Lease : IDisposable { public void Dispose() { } }

    private static Task<ScanSnapshot> Measure(string path) => new VolumeScanner().MeasureFolderAsync(path);
    private static async Task<ScanSnapshot> Capture(string path)
    {
        var scan = await Measure(path);
        scan.JournalScopes = [new(path, true, new(7, 1000, 100))];
        return scan;
    }
    private static FileIdentity Id(ScanSnapshot snapshot, string path) =>
        snapshot.Records[snapshot.Entry(snapshot.FindEntry(path)!.Value).FileIndex].Identity;

    private sealed class Events
    {
        public List<(string Event, JsonElement Data)> Items { get; } = [];
        public void Record(string name, object data) => Items.Add((name, JsonSerializer.SerializeToElement(data)));
        public int CompletedDirectories() => Assert.Single(Items, item => item.Event == "incremental-ntfs-complete").Data.GetProperty("DirectoriesRead").GetInt32();
        public string[] DirectoryReads() => Items.Where(item => item.Event == "incremental-directory-read")
            .Select(item => item.Data.GetProperty("Path").GetString()!).ToArray();
    }

    private sealed class JournalMetadataSource(ScanSnapshot current) : INtfsLayoutSource
    {
        private ScopedScannerTests.MetadataSource _metadata = new(current);
        private readonly List<(long Usn, NtfsJournalChange Change)> _changes = [];
        public List<FileIdentity[]> Requests { get; } = [];
        public int WholeVolumeCalls { get; private set; }
        public long CurrentUsn { get; set; } = 200;
        public bool Expired { get; init; }
        public Action? BeforeQuery { get; set; }
        public void Update(ScanSnapshot snapshot) => _metadata = new(snapshot);
        public void Change(long usn, FileIdentity identity, FileIdentity parent, uint reason, FileAttributes attributes = FileAttributes.Normal)
            => _changes.Add((usn, new(identity, parent, reason, attributes)));
        public void Acquire(string volume, Action<NtfsLayoutBlock> consume, CancellationToken token)
        { WholeVolumeCalls++; throw new InvalidOperationException("Incremental rechecks must not enumerate the entire volume."); }
        public void AcquireFiles(string volume, IReadOnlyList<FileIdentity> files, Action<NtfsLayoutBlock> consume, CancellationToken token)
        {
            Requests.Add(files.ToArray());
            var before = BeforeQuery; BeforeQuery = null; before?.Invoke();
            _metadata.AcquireFiles(volume, files, consume, token);
        }
        public NtfsJournalChanges ReadJournal(string volume, NtfsJournalCheckpoint? since, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (since is { } supplied && (Expired || supplied.JournalId != 1000 || supplied.NextUsn > CurrentUsn))
                throw new IOException("Owned journal history gap.");
            return new(new(7, 1000, CurrentUsn), since is { } checkpoint
                ? _changes.Where(item => item.Usn >= checkpoint.NextUsn && item.Usn < CurrentUsn).Select(item => item.Change).ToArray() : []);
        }
    }
}
