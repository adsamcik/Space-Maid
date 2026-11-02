using System.IO;
using System.ComponentModel;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.IO;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

[Trait("Stage", "System")]
public sealed class PermanentBuildDeletionTests
{
    [Fact]
    public async Task DeletesTheExactLongPathTreeAndKeepsProjectSourceAndOtherFolders()
    {
        using var fixture = new TestDirectory();
        string project = Directory.CreateDirectory(Path.Join(fixture.Path, "project")).FullName;
        string source = Path.Join(project, "Program.cs"); File.WriteAllText(source, "authored source");
        string output = Directory.CreateDirectory(Path.Join(project, "build")).FullName;
        string nested = output;
        for (int index = 0; index < 5; index++) nested = Directory.CreateDirectory(Path.Join(nested, new string((char)('a' + index), 55))).FullName;
        string generated = Path.Join(nested, "output.dll"); File.WriteAllText(generated, "rebuildable fixture");
        string sibling = Directory.CreateDirectory(Path.Join(project, "other")).FullName;
        string siblingFile = Path.Join(sibling, "keep.dat"); File.WriteAllText(siblingFile, "other contents");
        Assert.True(generated.Length > 260);
        var (scan, target) = await Capture(fixture.Path, output);

        int mutations = 0;
        target.DeletePermanently(new DiskAccess(() => null), scan, mutationStarted: () => mutations++);

        Assert.False(Directory.Exists(output)); Assert.False(File.Exists(generated));
        Assert.Equal(1, mutations);
        Assert.Equal("authored source", File.ReadAllText(source));
        Assert.Equal("other contents", File.ReadAllText(siblingFile));
    }

    [Fact]
    public async Task RootReplacementCannotSubstituteAPathForTheCapturedIdentity()
    {
        using var fixture = new TestDirectory();
        var output = CreateOutput(fixture.Path); var (scan, target) = await Capture(fixture.Path, output.Root);
        string held = output.Root + "-held"; Directory.Move(output.Root, held);
        Directory.CreateDirectory(output.Root); File.WriteAllText(output.File, "replacement contents");

        Assert.Throws<IOException>(() => target.DeletePermanently(new DiskAccess(() => null), scan));

        Assert.Equal("replacement contents", File.ReadAllText(output.File));
        Assert.Equal("original contents", File.ReadAllText(Path.Join(held, "result.dll")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrNewFilesKeepTheWholeFolderBeforeAnyMutation(bool addFile)
    {
        using var fixture = new TestDirectory();
        var output = CreateOutput(fixture.Path);
        string unchanged = Path.Join(output.Root, "other.dll");
        File.WriteAllText(unchanged, "unchanged contents");
        var (scan, target) = await Capture(fixture.Path, output.Root);
        if (addFile)
        {
            File.WriteAllText(Path.Join(output.Root, "new.dll"), "new contents");
            // Directory timestamps are not sufficient evidence of membership.
            int link = scan.FindEntry(output.Root)!.Value;
            Directory.SetLastWriteTimeUtc(output.Root, DateTime.FromFileTimeUtc(scan.GetMetadata(link).LastWriteFileTime));
        }
        else
        {
            File.WriteAllText(output.File, "changed contents");
            File.SetLastWriteTimeUtc(output.File, DateTime.UtcNow.AddMinutes(1));
        }

        Assert.Throws<IOException>(() => target.DeletePermanently(new DiskAccess(() => null), scan));

        Assert.Equal("unchanged contents", File.ReadAllText(unchanged));
        Assert.Equal(addFile ? "original contents" : "changed contents", File.ReadAllText(output.File));
        if (addFile) Assert.Equal("new contents", File.ReadAllText(Path.Join(output.Root, "new.dll")));
    }

    [Fact]
    public async Task ALockedFileKeepsEveryCapturedEntry()
    {
        using var fixture = new TestDirectory(); var output = CreateOutput(fixture.Path);
        string other = Path.Join(output.Root, "other.dll"); File.WriteAllText(other, "other contents");
        var (scan, target) = await Capture(fixture.Path, output.Root);
        bool started = false;
        using (File.Open(output.File, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.Throws<Win32Exception>(() => target.DeletePermanently(new DiskAccess(() => null), scan,
                mutationStarted: () => started = true));
        Assert.False(started);
        Assert.Equal("original contents", File.ReadAllText(output.File));
        Assert.Equal("other contents", File.ReadAllText(other));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedChildrenAndAncestorsAreNeverFollowed(bool linkedAncestor)
    {
        using var fixture = new TestDirectory(); using var outside = new TestDirectory();
        string parent = Directory.CreateDirectory(Path.Join(fixture.Path, "project")).FullName;
        var output = CreateOutput(parent); var (scan, target) = await Capture(fixture.Path, output.Root);
        string outsideFile = Path.Join(outside.Path, "keep.txt"); File.WriteAllText(outsideFile, "outside contents");
        string junction;
        if (linkedAncestor)
        {
            // The selected root still has its original identity behind this
            // redirect, so merely checking the root identity is insufficient.
            string held = parent + "-held"; Directory.Move(parent, held);
            junction = parent; await OpportunityReviewValidatorTests.CreateJunctionAsync(junction, held);
        }
        else
        {
            junction = Path.Join(output.Root, "linked");
            await OpportunityReviewValidatorTests.CreateJunctionAsync(junction, outside.Path);
        }
        try
        {
            Assert.Throws<IOException>(() => target.DeletePermanently(new DiskAccess(() => null), scan));
            Assert.Equal("outside contents", File.ReadAllText(outsideFile));
            Assert.Equal("original contents", File.ReadAllText(output.File));
        }
        finally { Directory.Delete(junction); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyFilesAndAlternateStreamsRemainUntouched(bool alternateStream)
    {
        using var fixture = new TestDirectory(); var output = CreateOutput(fixture.Path);
        if (alternateStream) File.WriteAllText(output.File + ":additional", "additional contents");
        else File.SetAttributes(output.File, FileAttributes.ReadOnly);
        try
        {
            var (scan, target) = await Capture(fixture.Path, output.Root);
            Assert.Throws<IOException>(() => target.DeletePermanently(new DiskAccess(() => null), scan));
            Assert.Equal("original contents", File.ReadAllText(output.File));
            if (alternateStream) Assert.Equal("additional contents", File.ReadAllText(output.File + ":additional"));
        }
        finally { File.SetAttributes(output.File, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryAlternateStreamsKeepTheWholeTreeBeforeMutation(bool onRoot)
    {
        using var fixture = new TestDirectory(); var output = CreateOutput(fixture.Path);
        string nested = Directory.CreateDirectory(Path.Join(output.Root, "nested")).FullName;
        string other = Path.Join(nested, "other.dll"); File.WriteAllText(other, "other contents");
        string stream = (onRoot ? output.Root : nested) + ":additional";
        File.WriteAllText(stream, "directory stream contents");
        var (scan, target) = await Capture(fixture.Path, output.Root);
        bool started = false;
        var error = Assert.Throws<IOException>(() => target.DeletePermanently(new DiskAccess(() => null), scan,
            mutationStarted: () => started = true));
        Assert.Contains("data streams", error.Message);
        Assert.False(started); Assert.Equal("original contents", File.ReadAllText(output.File));
        Assert.Equal("other contents", File.ReadAllText(other));
        Assert.Equal("directory stream contents", File.ReadAllText(stream));
    }

    [Fact]
    public async Task CancellationBeforeAcquisitionLeavesTheTreeUntouched()
    {
        using var fixture = new TestDirectory(); var output = CreateOutput(fixture.Path);
        var (scan, target) = await Capture(fixture.Path, output.Root);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => target.DeletePermanently(new DiskAccess(() => null), scan, cancellation.Token));
        Assert.Equal("original contents", File.ReadAllText(output.File));
    }

    [Fact]
    public async Task CancellationAfterCompleteAcquisitionStopsBeforeTheFirstDisposition()
    {
        using var fixture = new TestDirectory(); var output = CreateOutput(fixture.Path);
        var (scan, target) = await Capture(fixture.Path, output.Root);
        using var cancellation = new CancellationTokenSource(); int starts = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => target.DeletePermanently(new DiskAccess(() => null), scan,
            cancellation.Token, () => { starts++; cancellation.Cancel(); }));
        Assert.Equal(1, starts); Assert.Equal("original contents", File.ReadAllText(output.File));
    }

    [Fact]
    public async Task ALogicalOnlySnapshotCannotAuthorizePermanentTreeRemoval()
    {
        using var fixture = new TestDirectory(); var output = CreateOutput(fixture.Path);
        var (scan, target) = await Capture(fixture.Path, output.Root);
        var logical = new ScanSnapshot(scan.RootPath, scan.Backend, scan.Records, scan.Links, scan.NameArena,
            scan.RootIndex, scan.FirstLinks, scan.ChildHeads, scan.NextLinks, scan.DirectoryLogical, scan.DirectoryAllocated,
            false, false, false, scan.Warnings, scan.Metrics, scan.EntryFlags, scan.WarningCount, scan.DirectoryCount, scan.FallbackReason);
        Assert.Throws<InvalidOperationException>(() => target.DeletePermanently(new DiskAccess(() => null), logical));
        Assert.Equal("original contents", File.ReadAllText(output.File));
    }

    private static (string Root, string File) CreateOutput(string parent)
    {
        string root = Directory.CreateDirectory(Path.Join(parent, "build")).FullName;
        string file = Path.Join(root, "result.dll"); File.WriteAllText(file, "original contents");
        return (root, file);
    }

    private static async Task<(ScanSnapshot Scan, FolderDeletionTarget Target)> Capture(string scope, string output)
    {
        var scan = await new VolumeScanner().ScanAsync(scope, preferFast: false);
        Assert.True(scan.HasAllocationData);
        var target = FolderDeletionTarget.FromScan(scan, scan.FindEntry(output)!.Value);
        var current = await new DiskAccess(() => null).MeasureFolderAsync(output);
        Assert.True(current.HasAllocationData);
        return (current, target);
    }
}
