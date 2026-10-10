using System.IO;
using System.Runtime.InteropServices;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class RecheckTests
{
    [Fact]
    public async Task ShallowParentRemovesDeletedBranchAddsNewBranchAndKeepsUnrelatedCachedChildren()
    {
        using var fixture = new TestDirectory();
        string kept = Directory.CreateDirectory(Path.Join(fixture.Path, "kept")).FullName;
        string removed = Directory.CreateDirectory(Path.Join(fixture.Path, "removed")).FullName;
        File.WriteAllBytes(Path.Join(kept, "old.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Join(removed, "gone.bin"), new byte[8192]);
        var original = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        long keptSize = original.Entry(original.FindEntry(kept)!.Value).LogicalBytes;
        File.Delete(Path.Join(removed, "gone.bin")); Directory.Delete(removed);
        File.WriteAllBytes(Path.Join(kept, "old.bin"), new byte[20000]);
        string added = Directory.CreateDirectory(Path.Join(fixture.Path, "added")).FullName;
        File.WriteAllBytes(Path.Join(added, "new.bin"), new byte[12000]);
        var events = new List<string>();
        var updated = await ScanRechecker.RefreshAsync(original, [new(fixture.Path, false)], diagnostic: (name, _) => events.Add(name));
        Assert.Null(updated.FindEntry(removed));
        Assert.Equal(keptSize, updated.Entry(updated.FindEntry(kept)!.Value).LogicalBytes);
        Assert.Equal(12000, updated.Entry(updated.FindEntry(added)!.Value).LogicalBytes);
        Assert.Equal(keptSize + 12000, updated.LogicalBytes);
        Assert.Equal(12288, original.LogicalBytes); // The published snapshot was not mutated.
        Assert.NotNull(updated.LastRecheckUtc);
        Assert.Equal(original.ScannedAtUtc, updated.ScannedAtUtc);
        Assert.False(updated.IncludesAllDataStreams);
        Assert.False(updated.HasCompleteVolumeLinks);
        Assert.Contains("recheck-merge-complete", events);
        Assert.DoesNotContain(kept, updated.RecheckedPaths);
    }

    [Fact]
    public async Task RecheckUpdatesSharedObjectOnceAndPromotesItsRemainingNameAfterRemoval()
    {
        using var fixture = new TestDirectory();
        string source = Directory.CreateDirectory(Path.Join(fixture.Path, "source")).FullName;
        string outside = Directory.CreateDirectory(Path.Join(fixture.Path, "outside")).FullName;
        string file = Path.Join(source, "original.bin"), alias = Path.Join(outside, "alias.bin");
        File.WriteAllBytes(file, new byte[4096]);
        Assert.True(CreateHardLink(alias, file, 0));
        var original = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        File.WriteAllBytes(file, new byte[16384]);
        var changed = await ScanRechecker.RefreshAsync(original, [new(source)]);
        Assert.Equal(16384, changed.LogicalBytes);
        Assert.Equal(16384, changed.Entry(changed.FindEntry(alias)!.Value).LogicalBytes);
        Assert.Equal(changed.AllocatedBytes, changed.SortedChildren(changed.RootIndex).Sum(changed.AttributedBytes));
        File.Delete(file);
        var removed = await ScanRechecker.RefreshAsync(changed, [new(source)]);
        Assert.Null(removed.FindEntry(file));
        Assert.True(removed.Entry(removed.FindEntry(alias)!.Value).IsPrimaryName);
        Assert.Equal(16384, removed.LogicalBytes);
    }

    [Fact]
    public async Task RecheckAfterMoveAccountsForSourceAndDestinationWithoutDuplicatingData()
    {
        using var fixture = new TestDirectory();
        string source = Directory.CreateDirectory(Path.Join(fixture.Path, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Join(fixture.Path, "destination")).FullName;
        string folder = Directory.CreateDirectory(Path.Join(source, "folder")).FullName;
        File.WriteAllBytes(Path.Join(folder, "data.bin"), new byte[8192]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        string moved = Path.Join(destination, "moved");
        Directory.Move(folder, moved);
        var updated = await ScanRechecker.RefreshAsync(scan, [new(source, false), new(destination, false), new(moved)]);
        Assert.Null(updated.FindEntry(folder));
        Assert.NotNull(updated.FindEntry(Path.Join(moved, "data.bin")));
        Assert.Equal(scan.AllocatedBytes, updated.AllocatedBytes);
        Assert.Equal(scan.LogicalBytes, updated.LogicalBytes);
        Assert.Equal(0, updated.WarningCount);
    }

    [Fact]
    public async Task ShallowRenamePreservesCachedDescendantsUnderTheirNewPath()
    {
        using var fixture = new TestDirectory();
        string before = Directory.CreateDirectory(Path.Join(fixture.Path, "before")).FullName;
        File.WriteAllBytes(Path.Join(before, "data.bin"), new byte[8192]);
        var original = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        string after = Path.Join(fixture.Path, "after");
        Directory.Move(before, after);
        var updated = await ScanRechecker.RefreshAsync(original, [new(fixture.Path, false)]);
        Assert.Null(updated.FindEntry(before));
        Assert.NotNull(updated.FindEntry(Path.Join(after, "data.bin")));
        Assert.Equal(original.LogicalBytes, updated.LogicalBytes);
        Assert.Equal(original.AllocatedBytes, updated.AllocatedBytes);
        Assert.Equal(0, updated.WarningCount);
    }

    [Fact]
    public void MergePreservesCaseSensitiveNamesAndUsesTheNewParentIdentity()
    {
        string root = @"G:\owned-snapshot-fixture";
        var original = new ScanBuilder();
        original.RootIndex = original.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int folder = original.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0));
        original.AddLink(folder, new(1), "folder");
        original.AddLink(original.AddFile(new(new(3), 0, 10, 10, 0)), new(2), "obsolete");
        var scan = original.Build(root, "Fixture", TimeSpan.Zero, 0, default);
        var parent = new ScanBuilder();
        parent.RootIndex = parent.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        parent.AddLink(parent.AddFile(new(new(4), FileAttributes.Directory, 0, 0, 0)), new(1), "Folder");
        var fresh = new ScanBuilder();
        fresh.RootIndex = fresh.AddFile(new(new(4), FileAttributes.Directory, 0, 0, 0));
        fresh.AddLink(fresh.AddFile(new(new(5), 0, 20, 20, 0)), new(4), "photo.jpg");
        fresh.AddLink(fresh.AddFile(new(new(6), 0, 30, 30, 0)), new(4), "PHOTO.JPG");
        string path = Path.Join(root, "folder");
        var updated = ScanRechecker.Merge(scan, [new(root, false, parent.Build(root, "Fixture", TimeSpan.Zero, 0, default)),
            new(path, true, fresh.Build(path, "Fixture", TimeSpan.Zero, 0, default))], default);
        Assert.Equal(50, updated.LogicalBytes);
        Assert.Equal(20, updated.Entry(updated.FindEntry(Path.Join(path, "photo.jpg"))!.Value).LogicalBytes);
        Assert.Equal(30, updated.Entry(updated.FindEntry(Path.Join(path, "PHOTO.JPG"))!.Value).LogicalBytes);
        Assert.Null(updated.FindEntry(Path.Join(path, "Photo.jpg"))); // Ambiguous casing is not guessed.
        Assert.Null(updated.FindEntry(Path.Join(path, "obsolete")));
        Assert.Equal(0, updated.WarningCount);
        Assert.Equal("Folder", updated.Entry(updated.FindEntry(path)!.Value).Name);
        Assert.Single(updated.Children(updated.RootIndex)); // Different request casing cannot duplicate a directory.
    }

    [Fact]
    public void MergeRejectsAnAlternateStreamAccountingDowngrade()
    {
        var original = new ScanBuilder { AllStreams = true };
        original.RootIndex = original.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        original.AddLink(original.AddFile(new(new(2), 0, 12288, 12288, 0)), new(1), "photo.jpg");
        var scan = original.Build(@"G:\owned-snapshot-fixture", "Fixture", TimeSpan.Zero, 0, default);
        var fresh = new ScanBuilder();
        fresh.RootIndex = fresh.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        fresh.AddLink(fresh.AddFile(new(new(2), 0, 4096, 4096, 0)), new(1), "photo.jpg");
        Assert.Throws<IOException>(() => ScanRechecker.Merge(scan,
            [new(scan.RootPath, true, fresh.Build(scan.RootPath, "Fixture", TimeSpan.Zero, 0, default))], default));
        Assert.Equal(12288, scan.LogicalBytes);
        Assert.Null(scan.LastRecheckUtc);
    }

    [Fact]
    public async Task TargetedStreamMetadataPreservesAndRefreshesNamedDataStreams()
    {
        using var fixture = new TestDirectory();
        string file = Path.Join(fixture.Path, "data.bin");
        File.WriteAllBytes(file, new byte[4096]);
        File.WriteAllBytes(file + ":extra", new byte[8192]);
        var original = await new VolumeScanner().ReadDirectoryAsync(fixture.Path, true, default, allStreams: true);
        Assert.True(original.IncludesAllDataStreams);
        Assert.Equal(12288, original.LogicalBytes);
        Assert.Equal(12288, original.AllocatedBytes);
        File.WriteAllBytes(file + ":extra", new byte[16384]);
        var updated = await ScanRechecker.RefreshAsync(original, [new(fixture.Path, false)]);
        Assert.Equal(20480, updated.LogicalBytes);
        Assert.Equal(20480, updated.AllocatedBytes);
        Assert.True(updated.IncludesAllDataStreams);
        Assert.Equal(0, updated.WarningCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootDirectoryRechecksRetainHiddenReservedMetadataButRemoveDeletedUserFolders(bool recursive)
    {
        var original = new ScanBuilder { AllStreams = true, CompleteVolumeLinks = true };
        original.RootIndex = original.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
        original.AddLink(original.AddFile(new(new(0), 0, 100000, 100000, 0)), new(5), "$MFT");
        original.AddLink(original.AddFile(new(new(11), FileAttributes.Directory, 0, 0, 0)), new(5), "$Extend");
        original.AddLink(original.AddFile(new(new(100), 0, 1000, 1000, 0)), new(11), "$UsnJrnl");
        original.AddLink(original.AddFile(new(new(200), FileAttributes.Directory, 0, 0, 0)), new(5), "$user-folder");
        original.AddLink(original.AddFile(new(new(201), 0, 500, 500, 0)), new(200), "deleted.bin");
        var scan = original.Build(@"G:\", "Fast NTFS fixture", TimeSpan.Zero, 0, default);
        var fresh = new ScanBuilder { AllStreams = true };
        fresh.RootIndex = fresh.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
        var updated = ScanRechecker.Merge(scan, [new(scan.RootPath, recursive, fresh.Build(scan.RootPath, "Fixture", TimeSpan.Zero, 0, default))], default);
        Assert.Equal(101000, updated.LogicalBytes);
        Assert.NotNull(updated.FindEntry(@"G:\$MFT"));
        Assert.NotNull(updated.FindEntry(@"G:\$Extend\$UsnJrnl"));
        Assert.Null(updated.FindEntry(@"G:\$user-folder"));
        Assert.True(updated.RetainsEarlierNtfsMetadata);
        Assert.Equal(scan.ScannedAtUtc, updated.ScannedAtUtc);
        Assert.Equal(0, updated.WarningCount);
    }

    [Fact]
    public void RecursiveReplacementTerminatesWhenCachedSecondaryDirectoryNamesContainACycle()
    {
        var original = new ScanBuilder();
        original.RootIndex = original.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int a = original.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0));
        int b = original.AddFile(new(new(3), FileAttributes.Directory, 0, 0, 0));
        original.AddLink(a, new(1), "a");
        original.AddLink(b, new(2), "b");
        original.AddLink(a, new(3), "loop");
        var scan = original.Build(@"G:\owned-snapshot-fixture", "Fixture", TimeSpan.Zero, 0, default);
        var fresh = new ScanBuilder();
        fresh.RootIndex = fresh.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var updated = ScanRechecker.Merge(scan, [new(scan.RootPath, true, fresh.Build(scan.RootPath, "Fixture", TimeSpan.Zero, 0, default))], timeout.Token);
        Assert.Empty(updated.Children(updated.RootIndex));
        Assert.Equal(1, updated.FileCount);
        Assert.NotNull(updated.LastRecheckUtc);
    }

    [Fact]
    public async Task CancellationAndInaccessibleOrMissingScopeDoNotPublishAReplacement()
    {
        using var fixture = new TestDirectory();
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScanRechecker.RefreshAsync(scan, [new(fixture.Path)], cancelled.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => ScanRechecker.RefreshAsync(scan, [new(Path.Join(fixture.Path, "missing"))]));
        Assert.Null(scan.LastRecheckUtc);
        Assert.Empty(scan.RecheckedPaths);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, nint security);
}
