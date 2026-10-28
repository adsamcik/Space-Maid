using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PartialRecheckTests
{
    private const string Root = @"G:\owned-partial-recheck";

    [Fact]
    public void UnavailableBranchKeepsPreviousValuesWhileReadableSiblingsUpdate()
    {
        var scan = Snapshot(1, [(2, 1, "protected", true, 0), (3, 2, "cached.bin", false, 100), (4, 1, "readable.bin", false, 10)]);
        var fresh = Snapshot(1, [(2, 1, "protected", true, 0), (4, 1, "readable.bin", false, 20)], [2]);
        var updated = Refresh(scan, fresh);
        Assert.Equal(120, updated.LogicalBytes);
        Assert.Equal(100, Entry(updated, "protected", "cached.bin").LogicalBytes);
        Assert.True(Entry(updated, "protected").IsIncomplete);
        Assert.False(Entry(updated, "readable.bin").IsIncomplete);
        Assert.True(updated.RootEntry.IsIncomplete);
        Assert.Equal(new ScanRecheckRequest(Path.Join(Root, "protected")), Assert.Single(updated.IncompleteRecheckScopes));
        Assert.Contains(updated.Warnings, warning => warning.Contains("protected"));
        Assert.Equal(110, scan.LogicalBytes);
        Assert.Throws<IOException>(() => ScanRechecker.Merge(scan, [new(Root, true, fresh)], default));
    }

    [Fact]
    public void UnavailableFilePreservesStreamSizesAndMarksEveryContainingFolder()
    {
        var scan = Snapshot(1, [(2, 1, "a", true, 0), (3, 1, "b", true, 0),
            (4, 2, "file", false, 120), (4, 3, "alias", false, 120)]);
        var fresh = Snapshot(1, [(2, 1, "a", true, 0), (3, 1, "b", true, 0),
            (4, 2, "file", false, 20), (4, 3, "alias", false, 20)], [4]);
        var updated = Refresh(scan, fresh);
        Assert.Equal(120, updated.LogicalBytes);
        Assert.Equal(120, Entry(updated, "a", "file").LogicalBytes);
        Assert.True(Entry(updated, "a").IsIncomplete);
        Assert.True(Entry(updated, "b").IsIncomplete);
        Assert.Single(updated.UnreadableEntries);
    }

    [Fact]
    public void PartialParentKeepsUnknownNamesButReconcilesObservedNamesAndReadableChildren()
    {
        var scan = Snapshot(1, [(2, 1, "readable", true, 0), (3, 2, "deleted", false, 10),
            (4, 1, "replaced", true, 0), (5, 4, "old-child", false, 20), (6, 1, "unknown", false, 30)]);
        var fresh = Snapshot(1, [(2, 1, "readable", true, 0), (7, 1, "replaced", false, 40)], [1]);
        var updated = Refresh(scan, fresh);
        Assert.Null(updated.FindEntry(Path.Join(Root, "readable", "deleted")));
        Assert.Null(updated.FindEntry(Path.Join(Root, "replaced", "old-child")));
        Assert.False(Entry(updated, "replaced").IsDirectory);
        Assert.Equal(70, updated.LogicalBytes);
        Assert.Equal(3, updated.Children(updated.RootIndex).Count());
        Assert.False(Entry(updated, "readable").IsIncomplete);
    }

    [Fact]
    public void ReplacementDirectoryCannotInheritAnUnavailablePredecessorsChildren()
    {
        var scan = Snapshot(1, [(2, 1, "protected", true, 0), (3, 2, "old-child", false, 100)]);
        var fresh = Snapshot(1, [(4, 1, "protected", true, 0)], [4]);
        var updated = Refresh(scan, fresh);
        Assert.Null(updated.FindEntry(Path.Join(Root, "protected", "old-child")));
        Assert.Equal(0, updated.LogicalBytes);
        Assert.True(Entry(updated, "protected").IsIncomplete);
    }

    [Fact]
    public void FocusedRecoveryClearsOnlyTheFailuresItActuallyRechecks()
    {
        var scan = Snapshot(1, [(2, 1, "a", true, 0), (3, 1, "b", true, 0)], [2, 3]);
        var a = Snapshot(2, [(4, 2, "new", false, 10)], path: Path.Join(Root, "a"));
        var updated = ScanRechecker.Merge(scan, [new(a.RootPath, true, a)], default, allowIncomplete: true);
        Assert.False(Entry(updated, "a").IsIncomplete);
        Assert.True(Entry(updated, "b").IsIncomplete);
        Assert.Equal(new FileIdentity(3), Assert.Single(updated.UnreadableEntries));
        Assert.Empty(updated.IncompleteRecheckScopes);
        var b = Snapshot(3, [], path: Path.Join(Root, "b"));
        var recovered = ScanRechecker.Merge(updated, [new(b.RootPath, true, b)], default, allowIncomplete: true);
        Assert.Equal(0, recovered.WarningCount);
        Assert.False(recovered.RootEntry.IsIncomplete);
        Assert.Empty(recovered.Warnings);
    }

    [Fact]
    public void RemovingOneAliasDoesNotClearAnUnreadableFilesRemainingAlias()
    {
        var scan = Snapshot(1, [(2, 1, "source", true, 0), (3, 1, "outside", true, 0),
            (4, 2, "file", false, 120), (4, 3, "alias", false, 120)], [4]);
        var fresh = Snapshot(2, [], path: Path.Join(Root, "source"));
        var updated = ScanRechecker.Merge(scan, [new(fresh.RootPath, true, fresh)], default, allowIncomplete: true);
        Assert.Null(updated.FindEntry(Path.Join(Root, "source", "file")));
        Assert.True(Entry(updated, "outside", "alias").IsIncomplete);
        Assert.True(Entry(updated, "outside").IsIncomplete);
        Assert.Single(updated.UnreadableEntries);
    }

    [Fact]
    public void DestinationOnlyRecheckReconcilesMovedDirectoryChildrenWithoutDuplication()
    {
        var scan = Snapshot(1, [(2, 1, "source", true, 0), (3, 1, "destination", true, 0),
            (4, 2, "moved", true, 0), (5, 4, "deleted", false, 10), (6, 4, "kept", false, 10)]);
        var fresh = Snapshot(3, [(4, 3, "moved", true, 0), (6, 4, "kept", false, 20), (7, 4, "new", false, 30)],
            path: Path.Join(Root, "destination"));
        var updated = ScanRechecker.Merge(scan, [new(fresh.RootPath, true, fresh)], default, allowIncomplete: true);
        Assert.Null(updated.FindEntry(Path.Join(Root, "source", "moved")));
        Assert.Null(updated.FindEntry(Path.Join(Root, "destination", "moved", "deleted")));
        var moved = Entry(updated, "destination", "moved");
        Assert.Equal(2, updated.Children(moved.FileIndex).Count());
        Assert.Equal(50, updated.LogicalBytes);
        Assert.Equal(0, updated.WarningCount);
    }

    [Fact]
    public void PartialRefreshStillRejectsUnresolvedNamespaceAccounting()
    {
        var scan = Snapshot(1, []);
        var builder = new ScanBuilder { AllStreams = true };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        builder.AddLink(builder.AddFile(new(new(2), 0, 10, 10, 0)), new(99), "unresolved");
        var fresh = builder.Build(Root, "Fixture", TimeSpan.Zero, 0, default);
        Assert.Throws<IOException>(() => Refresh(scan, fresh));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Stage", "System")]
    public async Task WindowsAccessDenialDoesNotAbortOtherItemsAndRecoveryRemovesTheWarning(bool shallowNewDirectory)
    {
        using var fixture = new TestDirectory();
        string folder = Path.Join(fixture.Path, "protected");
        string protectedFile = Path.Join(folder, "file.bin"), readable = Path.Join(fixture.Path, "readable.bin");
        File.WriteAllBytes(readable, new byte[100]);
        if (!shallowNewDirectory) CreateProtected();
        var original = await new VolumeScanner().MeasureFolderAsync(fixture.Path);
        Assert.Equal(0, original.WarningCount);
        if (shallowNewDirectory) CreateProtected();
        var item = new DirectoryInfo(folder);
        var originalAcl = item.GetAccessControl();
        var denied = item.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory, AccessControlType.Deny));
        ScanSnapshot partial;
        try
        {
            item.SetAccessControl(denied);
            File.WriteAllBytes(readable, new byte[200]);
            partial = await ScanRechecker.RefreshAsync(original, [new(fixture.Path, !shallowNewDirectory)]);
            Assert.Equal(shallowNewDirectory ? 200 : 12488, partial.LogicalBytes);
            if (shallowNewDirectory) Assert.Null(partial.FindEntry(protectedFile));
            else Assert.Equal(12288, partial.Entry(partial.FindEntry(protectedFile)!.Value).LogicalBytes);
            Assert.True(partial.Entry(partial.FindEntry(folder)!.Value).IsIncomplete);
            Assert.False(partial.Entry(partial.FindEntry(readable)!.Value).IsIncomplete);
            Assert.Single(partial.IncompleteRecheckScopes);
            var fresh = await new VolumeScanner().MeasureFolderAsync(fixture.Path);
            Assert.True(fresh.WarningCount > 0);
            Assert.Throws<IOException>(() => ScanRechecker.Merge(original, [new(fixture.Path, true, fresh)], default));
            var focused = await ScanRechecker.RefreshAsync(partial, [new(folder)]);
            Assert.True(focused.Entry(focused.FindEntry(folder)!.Value).IsIncomplete);
            Assert.Equal(partial.LogicalBytes, focused.LogicalBytes);
        }
        finally
        {
            // SetAccessControl writes only modified sections. Mark the saved
            // descriptor's DACL as modified so restoration actually applies.
            originalAcl.SetSecurityDescriptorBinaryForm(originalAcl.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            item.SetAccessControl(originalAcl);
        }
        var recovered = await ScanRechecker.RefreshAsync(partial, [new(fixture.Path)]);
        Assert.Equal(12488, recovered.LogicalBytes);
        Assert.Equal(0, recovered.WarningCount);
        Assert.False(recovered.RootEntry.IsIncomplete);
        Assert.Empty(recovered.IncompleteRecheckScopes);

        void CreateProtected()
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(protectedFile, new byte[4096]);
            File.WriteAllBytes(protectedFile + ":extra", new byte[8192]);
        }
    }

    private static ScanEntry Entry(ScanSnapshot scan, params string[] parts) => scan.Entry(scan.FindEntry(Path.Join(Root, Path.Combine(parts)))!.Value);
    private static ScanSnapshot Refresh(ScanSnapshot scan, ScanSnapshot fresh) =>
        ScanRechecker.Merge(scan, [new(Root, true, fresh)], default, allowIncomplete: true);

    private static ScanSnapshot Snapshot(ulong root, (ulong Id, ulong Parent, string Name, bool Directory, long Bytes)[] entries,
        ulong[]? denied = null, string path = Root)
    {
        var builder = new ScanBuilder { AllStreams = true };
        builder.RootIndex = builder.AddFile(new(new(root), FileAttributes.Directory, 0, 0, 0));
        foreach (var entry in entries)
            builder.AddLink(builder.AddFile(new(new(entry.Id), entry.Directory ? FileAttributes.Directory : 0, entry.Bytes, entry.Bytes, 0)),
                new(entry.Parent), entry.Name);
        foreach (ulong id in denied ?? []) builder.Warn("Fixture access denied", new(id));
        return builder.Build(path, "Fixture", TimeSpan.Zero, 0, default);
    }
}
