using System.IO;
using System.Runtime.InteropServices;
using SpaceMaid.App;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class RecycleBinTests
{
    [Theory]
    [InlineData(@"G:\$Recycle.Bin", @"G:\")]
    [InlineData(@"c:\$recycle.bin\", @"C:\")]
    [InlineData(@"G:\Recycler", @"G:\")]
    [InlineData(@"G:\", null)]
    [InlineData(@"G:\folder\$Recycle.Bin", null)]
    [InlineData(@"G:\$Recycle.Bin\S-1-current", null)]
    [InlineData(@"G:\$Recycle.Bin-backup", null)]
    [InlineData(@"G:\folder\..\$Recycle.Bin", null)]
    [InlineData(@"\\server\share\$Recycle.Bin", null)]
    [InlineData(@"\\?\G:\$Recycle.Bin", null)]
    [InlineData("", null)]
    public void OnlyDriveRecycleBinFoldersOfferDriveCleanup(string path, string? expectedRoot) =>
        Assert.Equal(expectedRoot, RecycleBinService.RootForFolder(path));

    [Theory]
    [InlineData(typeof(RecycleBinService.QueryInfo32), 20, 4, 12)]
    [InlineData(typeof(RecycleBinService.QueryInfo64), 24, 8, 16)]
    public void NativeQueryLayoutsMatchWindowsSdk(Type type, int size, int bytesOffset, int itemsOffset)
    {
        Assert.Equal(size, Marshal.SizeOf(type));
        Assert.Equal(bytesOffset, Marshal.OffsetOf(type, "Bytes").ToInt32());
        Assert.Equal(itemsOffset, Marshal.OffsetOf(type, "Items").ToInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(@"G:\folder")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\?\G:\")]
    [InlineData("G:/")]
    public async Task AmbiguousOrAllDriveScopesNeverReachWindows(string path)
    {
        int calls = 0;
        var service = new RecycleBinService(_ => { calls++; return "fixture"; }, _ => { calls++; return (0, 0); }, (_, _) => { calls++; return 0; });
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EmptyAsync(new(path, "fixture", 0, 0), 0, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ConfirmedOneDriveIsRecheckedAndOnlyItsBinIsEmptied()
    {
        long bytes = 4096, items = 2; int calls = 0;
        var roots = new List<string>();
        var service = new RecycleBinService(root => { roots.Add(root); return "owned-volume"; }, _ => (bytes, items),
            (owner, root) => { Assert.Equal((nint)123, owner); Assert.Equal(@"G:\", root); calls++; bytes = 0; items = 0; return 0; });
        var bin = await service.QueryAsync(@"g:\");
        var result = await service.EmptyAsync(bin, 123, default);
        Assert.False(result.Cancelled); Assert.Equal(0, result.Remaining.Items); Assert.Equal(0, result.Remaining.Bytes);
        Assert.Equal(1, calls); Assert.All(roots, root => Assert.Equal(@"G:\", root));
    }

    [Fact]
    public async Task ChangedDriveContentsAndCancelledConsentNeverStartEmptying()
    {
        long bytes = 4096, items = 2; string volume = "original"; int calls = 0;
        var service = new RecycleBinService(_ => volume, _ => (bytes, items), (_, _) => { calls++; return 0; });
        var bin = await service.QueryAsync(@"G:\");
        items++;
        await Assert.ThrowsAsync<IOException>(() => service.EmptyAsync(bin, 0, default));
        items = 2; bytes++;
        await Assert.ThrowsAsync<IOException>(() => service.EmptyAsync(bin, 0, default));
        bytes = 4096; volume = "replacement";
        await Assert.ThrowsAsync<IOException>(() => service.EmptyAsync(bin, 0, default));
        volume = "original";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EmptyAsync(bin, 0, new(true)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PartialAndNativeCancellationResultsNeverClaimAnEmptyBin()
    {
        long items = 2;
        var partial = new RecycleBinService(_ => "fixture", _ => (items * 4096, items), (_, _) => { items = 1; return 0; });
        var result = await partial.EmptyAsync(await partial.QueryAsync(@"G:\"), 0, default);
        Assert.False(result.Cancelled); Assert.Equal(1, result.Remaining.Items);
        var cancellation = new RecycleBinService(_ => "fixture", _ => (4096, 1), (_, _) => unchecked((int)0x800704C7));
        var cancelled = await cancellation.EmptyAsync(await cancellation.QueryAsync(@"G:\"), 0, default);
        Assert.True(cancelled.Cancelled); Assert.Equal(1, cancelled.Remaining.Items);
    }

    [Fact]
    public async Task NativeSizeQueryIsReadOnlyOnTheSystemDrive()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;
        var bin = await new RecycleBinService().QueryAsync(root);
        Assert.Equal(root, bin.RootPath, ignoreCase: true);
        Assert.False(string.IsNullOrEmpty(bin.VolumeId));
        Assert.True(bin.Bytes >= 0 && bin.Items >= 0);
        // Never call native Empty here: the user's actual bin is not test data.
    }

    [Fact]
    public void RechecksAreLimitedToTheCurrentUserBinOrItsParentMetadata()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int bin = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(bin, new(1), "$Recycle.Bin");
        int user = builder.AddFile(new(new(3), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(user, new(2), "S-1-current");
        int other = builder.AddFile(new(new(4), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(other, new(2), "S-1-other");
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, 0, default);
        string binPath = @"G:\$Recycle.Bin";
        var own = Assert.Single(MainWindow.RecycleBinRefreshScopes(scan, binPath, binPath + @"\S-1-current"));
        Assert.Equal(binPath + @"\S-1-current", own.Path); Assert.True(own.Recursive);
        var missing = Assert.Single(MainWindow.RecycleBinRefreshScopes(scan, binPath, binPath + @"\S-1-new"));
        Assert.Equal(binPath, missing.Path); Assert.False(missing.Recursive);
        Assert.Equal(binPath, Assert.Single(MainWindow.RecycleBinRefreshScopes(scan, binPath, own.Path, false)).Path);
        var removedBin = Assert.Single(MainWindow.RecycleBinRefreshScopes(scan, binPath, own.Path, false, false));
        Assert.Equal(@"G:\", removedBin.Path); Assert.False(removedBin.Recursive);
        Assert.Empty(MainWindow.RecycleBinRefreshScopes(scan, @"G:\absent", @"G:\absent\S-1-current"));
    }
}
