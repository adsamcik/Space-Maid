using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class DeletionTests
{
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"G:\$Recycle.Bin\example")]
    [InlineData(@"G:\System Volume Information\example")]
    [InlineData(@"G:\$Extend")]
    [InlineData(@"G:\folder\..\other")]
    [InlineData(@"G:\folder.\other")]
    [InlineData(@"G:\folder:stream")]
    [InlineData(@"\\server\share\folder")]
    [InlineData(@"\\?\G:\folder")]
    public void UnsafeOrProtectedTargetsAreRejected(string path) =>
        Assert.Throws<InvalidOperationException>(() => FolderDeletionTarget.ValidatePath(path));

    [Fact]
    public async Task ReplacedDirectoryAndFileSelectionsCannotBeDeleted()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "folder")).FullName;
        File.WriteAllText(Path.Join(folder, "keep.txt"), "keep");
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        int folderLink = Assert.Single(scan.SortedChildren(scan.RootIndex));
        var target = FolderDeletionTarget.FromScan(scan, folderLink);
        int fileLink = Assert.Single(scan.SortedChildren(scan.Entry(folderLink).FileIndex));
        Assert.Throws<InvalidOperationException>(() => FolderDeletionTarget.FromScan(scan, fileLink));
        Assert.Throws<ArgumentOutOfRangeException>(() => FolderDeletionTarget.FromScan(scan, -2));
        Directory.Move(folder, folder + "-original");
        Directory.CreateDirectory(folder);
        Assert.Throws<InvalidOperationException>(target.ValidateCurrent);
        Assert.Throws<InvalidOperationException>(() => FolderDeletionTarget.FromScan(scan, folderLink));
        Assert.Equal("keep", File.ReadAllText(Path.Join(folder + "-original", "keep.txt")));
    }

    [Fact]
    public void PermanentFallbackAndCancellationAreRefusedBeforeDelete()
    {
        Assert.Equal(FolderRecycler.CannotRecycleHResult, FolderRecycler.DeletePermission(0, default));
        Assert.Equal(0, FolderRecycler.DeletePermission(0x80, default));
        Assert.Equal(FolderRecycler.CancelledHResult, FolderRecycler.DeletePermission(0x80, new(true)));
    }

    [Fact]
    public void UnresolvedAndReparseDirectorySelectionsAreRejected()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int unresolved = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0));
        builder.AddLink(unresolved, new(999), "missing-parent");
        int reparse = builder.AddFile(new(new(3), FileAttributes.Directory | FileAttributes.ReparsePoint, 0, 0, 0));
        builder.AddLink(reparse, new(1), "junction");
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, 0, default);
        Assert.Throws<InvalidOperationException>(() => FolderDeletionTarget.FromScan(scan, scan.PrimaryLink(unresolved)));
        Assert.Throws<InvalidOperationException>(() => FolderDeletionTarget.FromScan(scan, scan.PrimaryLink(reparse)));
    }

    [Fact]
    public async Task WindowsRecyclesOnlyTheFixtureFolderAndPreservesItsContents()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "recycle-" + Guid.NewGuid().ToString("N"))).FullName;
        string content = "Space Maid owned fixture " + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Join(folder, "keep.txt"), content);
        var scan = await new VolumeScanner().ScanAsync(folder, preferFast: false);
        var target = FolderDeletionTarget.FromScan(scan, -1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FolderRecycler.RecycleAsync(target, 0, new(true)));
        Assert.True(Directory.Exists(folder));
        var result = await FolderRecycler.RecycleAsync(target, 0, default);
        Assert.False(result.Cancelled);
        string recycled = Assert.IsType<string>(result.RecycledPath);
        string root = Path.GetPathRoot(folder)!;
        Assert.StartsWith(root + "$Recycle.Bin\\", recycled, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("$R", Path.GetFileName(recycled), StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(content, File.ReadAllText(Path.Join(recycled, "keep.txt")));
        RestoreFixture(recycled, folder);
        Assert.Equal(content, File.ReadAllText(Path.Join(folder, "keep.txt")));
    }

    internal static void RestoreFixture(string recycled, string folder)
    {
        // Restore only the exact fixture destination returned by this operation.
        // Its matching $I record must name our original owned fixture before
        // removing that metadata. Never enumerate or empty the Recycle Bin.
        string expected = Path.GetFullPath(Path.Join(Path.GetTempPath(), "SpaceMaid-tests")) + "\\";
        Assert.StartsWith(expected, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetPathRoot(folder)! + "$Recycle.Bin\\", recycled, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("$R", Path.GetFileName(recycled), StringComparison.Ordinal);
        string metadata = Path.Join(Path.GetDirectoryName(recycled)!, "$I" + Path.GetFileName(recycled)[2..]);
        if (File.Exists(metadata))
        {
            var data = File.ReadAllBytes(metadata);
            Assert.Contains(folder, System.Text.Encoding.Unicode.GetString(data), StringComparison.OrdinalIgnoreCase);
            Directory.Move(recycled, folder);
            File.Delete(metadata);
        }
        else Directory.Move(recycled, folder);
    }
}
