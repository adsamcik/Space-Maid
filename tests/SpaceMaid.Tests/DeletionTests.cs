using System.IO;
using System.Diagnostics;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class DeletionTests
{
    [Fact]
    public async Task RecyclePreparationChecksCurrentContentsAndRetainsIdentityGuards()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "folder")).FullName;
        string file = Path.Join(folder, "data.bin");
        File.WriteAllBytes(file, new byte[4096]);
        var operations = new FileOperations();
        var scan = await operations.MeasureFolderAsync(fixture.Path);
        int link = scan.FindEntry(folder)!.Value;
        File.WriteAllBytes(file, new byte[8192]);
        File.WriteAllBytes(file + ":extra", new byte[4096]);
        var target = await operations.PrepareRecycleAsync(scan, link, default);
        Assert.Equal(4096, scan.Entry(link).LogicalBytes);
        Assert.Equal(12288, target.LogicalBytes);
        Assert.Equal(folder, target.Path);
        target.ValidateCurrent();
        Directory.Move(folder, folder + "-original");
        Directory.CreateDirectory(folder);
        Assert.Throws<InvalidOperationException>(target.ValidateCurrent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.PrepareRecycleAsync(scan, link, default));
        Assert.Equal(8192, new FileInfo(Path.Join(folder + "-original", "data.bin")).Length);
    }

    [Fact]
    public async Task CancelledAndMissingRecycleChecksDoNotReturnATarget()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "folder")).FullName;
        var operations = new FileOperations();
        var scan = await operations.MeasureFolderAsync(fixture.Path);
        int link = scan.FindEntry(folder)!.Value;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.PrepareRecycleAsync(scan, link, new(true)));
        Assert.True(Directory.Exists(folder));
        Directory.Move(folder, folder + "-moved");
        Assert.NotNull(await Record.ExceptionAsync(() => operations.PrepareRecycleAsync(scan, link, default)));
        Assert.True(Directory.Exists(folder + "-moved"));
    }

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
        Assert.Null(FolderDeletionTarget.SelectionBlockReason(scan, folderLink));
        int fileLink = Assert.Single(scan.SortedChildren(scan.Entry(folderLink).FileIndex));
        Assert.Throws<InvalidOperationException>(() => FolderDeletionTarget.FromScan(scan, fileLink));
        Assert.NotNull(FolderDeletionTarget.SelectionBlockReason(scan, fileLink));
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
        int unresolvedLink = Array.FindIndex(scan.Links, l => l.FileIndex == unresolved);
        Assert.Throws<InvalidOperationException>(() => FolderDeletionTarget.FromScan(scan, unresolvedLink));
        Assert.Throws<InvalidOperationException>(() => FolderDeletionTarget.FromScan(scan, scan.PrimaryLink(reparse)));
        Assert.Contains("Drive roots", FolderDeletionTarget.SelectionBlockReason(scan, -1));
        Assert.Contains("unresolved", FolderDeletionTarget.SelectionBlockReason(scan, unresolvedLink));
        Assert.Contains("reparse", FolderDeletionTarget.SelectionBlockReason(scan, scan.PrimaryLink(reparse)));
    }

    [Fact]
    public async Task WindowsRecyclesOnlyTheFixtureFolderAndPreservesItsContents()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "recycle-" + Guid.NewGuid().ToString("N"))).FullName;
        string content = "Space Maid owned fixture " + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Join(folder, "keep.txt"), content);
        string nested = Directory.CreateDirectory(Path.Join(folder, "one", "two")).FullName;
        byte[] bytes = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(Path.Join(nested, "nested.bin"), bytes);
        string outside = Directory.CreateDirectory(Path.Join(fixture.Path, "outside-target")).FullName;
        string outsideFile = Path.Join(outside, "keep-outside.txt"); File.WriteAllText(outsideFile, content);
        string junction = Path.Join(folder, "linked-folder");
        string ownedRoot = Path.GetFullPath(fixture.Path) + Path.DirectorySeparatorChar;
        Assert.StartsWith(ownedRoot, Path.GetFullPath(outside), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(ownedRoot, Path.GetFullPath(junction), StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = $"/c mklink /J \"{junction}\" \"{outside}\"";
        using (var process = Process.Start(start)!)
        {
            string output = await process.StandardOutput.ReadToEndAsync(), error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, output + error);
        }
        Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
        var scan = await new VolumeScanner().ScanAsync(folder, preferFast: false);
        var target = FolderDeletionTarget.FromScan(scan, -1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FolderRecycler.RecycleAsync(target, 0, new(true)));
        Assert.True(Directory.Exists(folder));
        var events = new List<string>();
        var result = await FolderRecycler.RecycleAsync(target, 0, default, (name, _) => events.Add(name));
        Assert.False(result.Cancelled);
        string recycled = Assert.IsType<string>(result.RecycledPath);
        string root = Path.GetPathRoot(folder)!;
        Assert.StartsWith(root + "$Recycle.Bin\\", recycled, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("$R", Path.GetFileName(recycled), StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(content, File.ReadAllText(Path.Join(recycled, "keep.txt")));
        try
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Join(recycled, "one", "two", "nested.bin")));
            Assert.Equal(content, File.ReadAllText(outsideFile));
            Assert.True((File.GetAttributes(Path.Join(recycled, "linked-folder")) & FileAttributes.ReparsePoint) != 0);
            Assert.Contains("recycle-verified", events);
        }
        finally { RestoreFixture(recycled, folder); }
        Assert.Equal(content, File.ReadAllText(Path.Join(folder, "keep.txt")));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Join(folder, "one", "two", "nested.bin")));
        Assert.Equal(content, File.ReadAllText(outsideFile));
        Directory.Delete(junction); // Only the owned junction; its target stays intact.
    }

    [Fact]
    public async Task LockedFixtureFilesCannotReportRecycledSuccessAndCanBeRetriedAfterClosing()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "locked-folder")).FullName;
        string file = Path.Join(folder, "keep.txt"), content = "Owned locked fixture " + Guid.NewGuid().ToString("N");
        File.WriteAllText(file, content);
        var scan = await new VolumeScanner().ScanAsync(folder, preferFast: false);
        var target = FolderDeletionTarget.FromScan(scan, -1);
        var events = new List<string>(); RecycleResult? unexpectedResult = null;
        try
        {
            using (var locked = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failure = await Record.ExceptionAsync(async () =>
                    unexpectedResult = await FolderRecycler.RecycleAsync(target, 0, default, (name, _) => events.Add(name)));
                Assert.NotNull(failure);
                Assert.DoesNotContain("recycle-verified", events);
                Assert.True(Directory.Exists(folder)); Assert.True(File.Exists(file));
            }
        }
        finally
        {
            if (unexpectedResult?.RecycledPath is { } destination && !Directory.Exists(folder))
                RestoreFixture(destination, folder);
        }
        Assert.Equal(content, File.ReadAllText(file));
        var result = await FolderRecycler.RecycleAsync(target, 0, default);
        string recycled = Assert.IsType<string>(result.RecycledPath);
        try { Assert.False(result.Cancelled); Assert.Equal(content, File.ReadAllText(Path.Join(recycled, "keep.txt"))); }
        finally { RestoreFixture(recycled, folder); }
        Assert.Equal(content, File.ReadAllText(file));
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
            byte[] data = [];
            RetrySharingViolation(() =>
            {
                using var input = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                data = buffer.ToArray();
            });
            Assert.Contains(folder, System.Text.Encoding.Unicode.GetString(data), StringComparison.OrdinalIgnoreCase);
            Directory.Move(recycled, folder);
            RetrySharingViolation(() => File.Delete(metadata));
        }
        else Directory.Move(recycled, folder);
    }

    private static void RetrySharingViolation(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && attempt < 5)
            { Thread.Sleep(100); } // Windows can briefly retain our own $I record after completion.
        }
    }
}
