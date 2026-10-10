using System.IO;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PreliminaryTests
{
    [Fact]
    public void PreviewIsBoundedRanksAllocationAndCountsEachObjectOnce()
    {
        var builder = new ScanBuilder(trackPreliminary: true);
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        for (int i = 1; i <= 1000; i++)
        {
            var record = new FileRecord(new((ulong)i + 1), FileAttributes.Normal, 1001 - i, i * 4096L, 0);
            int file = builder.AddFile(record);
            builder.AddLink(file, new(1), $"file-{i}.bin");
            Assert.Equal(file, builder.AddFile(record));
            builder.AddLink(file, new(1), $"alias-{i}.bin");
        }
        int directory = builder.AddFile(new(new(2000), FileAttributes.Directory, 123, 8192, 0));
        builder.AddLink(directory, new(1), "directory");
        var preview = builder.Preliminary(@"C:\");
        Assert.Equal(100, preview.LargestFiles.Count);
        Assert.Equal("file-1000.bin", preview.LargestFiles[0].Name);
        Assert.Equal("file-901.bin", preview.LargestFiles[^1].Name);
        Assert.Equal(500_500 + 123, preview.LogicalBytes);
        Assert.Equal(500_500L * 4096 + 8192, preview.AllocatedBytes);
        Assert.All(preview.LargestFiles, f => Assert.Equal(Path.Join(@"C:\", f.Name), f.Path));
        var complete = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        Assert.Equal(complete.AllocatedBytes, preview.AllocatedBytes);
        Assert.Equal(complete.LogicalBytes, preview.LogicalBytes);
    }

    [Fact]
    public void LateParentsResolveWithoutMutatingPublishedResultsOrAddingWarnings()
    {
        var builder = new ScanBuilder(trackPreliminary: true);
        int file = builder.AddFile(new(new(20), FileAttributes.Normal, 100, 4096, 0));
        builder.AddLink(file, new(10), "large.bin");
        var earlier = builder.Preliminary(@"C:\");
        Assert.Null(Assert.Single(earlier.LargestFiles).Path);
        int parent = builder.AddFile(new(new(10), FileAttributes.Directory, 0, 0, 0));
        builder.AddLink(parent, new(5), "project");
        builder.RootIndex = builder.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
        Assert.Equal(@"C:\project\large.bin", Assert.Single(builder.Preliminary(@"C:\").LargestFiles).Path);
        Assert.Null(Assert.Single(earlier.LargestFiles).Path);
        Assert.Equal(0, builder.WarningCount);

        // A cycle and a stale generation cannot produce an invented exact path.
        int a = builder.AddFile(new(new(30), FileAttributes.Directory, 0, 0, 0));
        int b = builder.AddFile(new(new(31), FileAttributes.Directory, 0, 0, 0));
        builder.AddLink(a, new(31), "a"); builder.AddLink(b, new(30), "b");
        int cyclic = builder.AddFile(new(new(40), FileAttributes.Normal, 1, 8192, 0));
        builder.AddLink(cyclic, new(30), "cyclic.bin");
        int stale = builder.AddFile(new(new(41), FileAttributes.Normal, 1, 8192, 0));
        builder.AddLink(stale, new(0x100000000000A), "stale.bin");
        Assert.All(builder.Preliminary(@"C:\").LargestFiles.Where(f => f.Name != "large.bin"), f => Assert.Null(f.Path));
        Assert.Equal(0, builder.WarningCount);
    }

    [Fact]
    public void LogicalOnlyPreviewRanksLogicalBytesAndDisclosesMissingAllocation()
    {
        var builder = new ScanBuilder(trackPreliminary: true) { AllocationData = false };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int small = builder.AddFile(new(new(2), FileAttributes.Normal, 100, 0, 0));
        builder.AddLink(small, new(1), "small");
        int large = builder.AddFile(new(new(3), FileAttributes.Normal, 1000, 0, 0));
        builder.AddLink(large, new(1), "large");
        var preview = builder.Preliminary(@"C:\");
        Assert.False(preview.HasAllocationData);
        Assert.Equal(1100, preview.LogicalBytes);
        Assert.Equal("large", preview.LargestFiles[0].Name);
    }

    [Fact]
    public async Task RealAcquisitionPublishesFinalPartialDataBeforeCancellation()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "project")).FullName;
        string keep = Path.Join(fixture.Path, "keep.bin");
        await File.WriteAllBytesAsync(keep, new byte[8192]);
        await File.WriteAllBytesAsync(Path.Join(folder, "unread.bin"), new byte[4096]);
        using var cancellation = new CancellationTokenSource();
        var reports = new List<ScanProgress>();
        var progress = new InlineProgress<ScanProgress>(p =>
        {
            reports.Add(p);
            // Cancel after the root's directory batch; the forced cancellation
            // report must still publish the file discovered in that batch.
            if (p.Files > 0 && p.Phase != "Scan cancelled") cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VolumeScanner().ScanAsync(
            fixture.Path, progress, cancellation.Token, preferFast: false));
        Assert.Empty(reports[0].Preliminary!.LargestFiles);
        var final = Assert.IsType<ScanPreliminary>(reports[^1].Preliminary);
        Assert.Equal("Scan cancelled", reports[^1].Phase);
        // Only the root batch has been read; the child file is excluded.
        Assert.Equal(keep, Assert.Single(final.LargestFiles).Path);
        Assert.Equal(8192, final.LogicalBytes);
        Assert.Equal(8192, new FileInfo(keep).Length);
    }
}
