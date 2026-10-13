using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class FileOperationsTests
{
    [Fact]
    public async Task ExistingOperationsFollowChangingNtfsAvailabilityAndRetainStreamFallback()
    {
        using var fixture = new TestDirectory();
        string file = Path.Join(fixture.Path, "payload.bin");
        File.WriteAllBytes(file, new byte[4096]);
        File.WriteAllBytes(file + ":extra", new byte[8192]);
        INtfsLayoutSource? available = null;
        var operations = new FileOperations(() => available);
        // A null helper still permits the native backend in an elevated host.
        bool nativeAvailable = Elevation.IsAdministrator;
        var standard = await operations.MeasureFolderAsync(fixture.Path);
        Assert.Equal(nativeAvailable ? "Fast NTFS scoped metadata" : "Targeted Windows stream metadata", standard.Backend);
        Assert.Equal(12288, standard.LogicalBytes);

        var fastSource = new ScopedScannerTests.MetadataSource(standard);
        available = fastSource;
        var fast = await operations.MeasureFolderAsync(fixture.Path);
        Assert.Equal("Fast NTFS scoped metadata", fast.Backend);
        Assert.Equal(standard.LogicalBytes, fast.LogicalBytes);
        Assert.Equal(standard.AllocatedBytes, fast.AllocatedBytes);
        Assert.True(fast.IncludesAllDataStreams);
        Assert.NotEmpty(fastSource.Requests);
        Assert.Equal(0, fastSource.WholeVolumeCalls);

        available = new ScopedScannerTests.MetadataSource(standard) { Defect = "unsupported" };
        var events = new List<string>();
        var fallback = await operations.MeasureFolderAsync(fixture.Path, diagnostic: (name, _) => events.Add(name));
        Assert.Equal("Targeted Windows stream metadata", fallback.Backend);
        Assert.Equal(standard.LogicalBytes, fallback.LogicalBytes);
        Assert.Equal(standard.AllocatedBytes, fallback.AllocatedBytes);
        Assert.Contains("scoped-fast-fallback", events);

        available = null;
        var scan = await operations.ScanAsync(fixture.Path);
        Assert.Equal(nativeAvailable ? "Fast NTFS folder scan" : "Standard Windows scan", scan.Backend);
        Assert.Equal(nativeAvailable, scan.IncludesAllDataStreams);
        Assert.Equal(nativeAvailable ? 12288 : 4096, scan.LogicalBytes);
        Assert.Equal(12288, (await operations.MeasureFolderAsync(fixture.Path)).LogicalBytes);
    }

    [Fact]
    public async Task RecyclingUsesWindowsShellWithLiveGuardsAndPreservesOwnedNestedContents()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "recycle-" + Guid.NewGuid().ToString("N"))).FullName;
        string nested = Directory.CreateDirectory(Path.Join(folder, "one", "two")).FullName;
        byte[] content = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(Path.Join(nested, "keep.bin"), content);
        var scan = await new VolumeScanner().ScanAsync(folder, preferFast: false);
        var target = FolderDeletionTarget.FromScan(scan, -1);
        var operations = new FileOperations(() => throw new InvalidOperationException("Mutations must not request the metadata backend."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.RecycleAsync(target, 0, new(true)));
        Assert.Equal(content, File.ReadAllBytes(Path.Join(nested, "keep.bin")));

        // A scan cannot authorize recycling a replacement occupying the same path.
        string moved = folder + "-original";
        Directory.Move(folder, moved);
        Directory.CreateDirectory(folder);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => operations.RecycleAsync(target, 0, default));
            Assert.Equal(content, File.ReadAllBytes(Path.Join(moved, "one", "two", "keep.bin")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        }
        finally { Directory.Delete(folder); Directory.Move(moved, folder); }

        var events = new List<string>();
        var result = await operations.RecycleAsync(target, 0, default, (name, _) => events.Add(name));
        string recycled = Assert.IsType<string>(result.RecycledPath);
        try
        {
            Assert.False(result.Cancelled);
            Assert.False(Directory.Exists(folder));
            Assert.Equal(content, File.ReadAllBytes(Path.Join(recycled, "one", "two", "keep.bin")));
            Assert.Contains("recycle-verified", events);
        }
        finally { DeletionTests.RestoreFixture(recycled, folder); }
        Assert.Equal(content, File.ReadAllBytes(Path.Join(nested, "keep.bin")));
    }

    [Fact]
    public async Task RecycleBinFacadePreservesConsentScopeIdentityAndCancellationGuards()
    {
        long bytes = 4096, items = 2;
        string volume = "owned-volume";
        int emptyCalls = 0;
        var queriedRoots = new List<string>();
        var bins = new RecycleBinService(root => { queriedRoots.Add(root); return volume; }, _ => (bytes, items),
            (owner, root) =>
            {
                Assert.Equal((nint)123, owner); Assert.Equal(@"G:\", root);
                emptyCalls++; bytes = 0; items = 0; return 0;
            });
        var operations = new FileOperations(() => throw new InvalidOperationException("Bin operations must not request NTFS metadata."), bins);
        await Assert.ThrowsAsync<ArgumentException>(() => operations.QueryRecycleBinAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => operations.EmptyRecycleBinAsync(new("", volume, bytes, items), 123, default));
        Assert.Empty(queriedRoots);
        var confirmed = await operations.QueryRecycleBinAsync(@"g:\");

        items++;
        await Assert.ThrowsAsync<IOException>(() => operations.EmptyRecycleBinAsync(confirmed, 123, default));
        items--;
        volume = "replacement-volume";
        await Assert.ThrowsAsync<IOException>(() => operations.EmptyRecycleBinAsync(confirmed, 123, default));
        volume = "owned-volume";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.EmptyRecycleBinAsync(confirmed, 123, new(true)));
        Assert.Equal(0, emptyCalls);

        var result = await operations.EmptyRecycleBinAsync(confirmed, 123, default);
        Assert.False(result.Cancelled); Assert.Equal(0, result.Remaining.Items); Assert.Equal(0, result.Remaining.Bytes);
        Assert.Equal(1, emptyCalls);
        Assert.All(queriedRoots, root => Assert.Equal(@"G:\", root));
    }
}
