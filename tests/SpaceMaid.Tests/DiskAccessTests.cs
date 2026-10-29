using System.IO;
using System.Runtime.InteropServices;
using SpaceMaid.Core.IO;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class DiskAccessTests
{
    [Fact]
    public void LegacyMetadataUsesHeldHandleAndRejectsWrongBufferLayouts()
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "owned.bin");
        File.WriteAllBytes(path, new byte[8193]);
        using var held = File.OpenRead(path);
        var disk = new DiskAccess(() => throw new InvalidOperationException("Held-handle metadata must not request an NTFS layout."));
        Assert.True(disk.ReadHandleInformation<LegacyInformation>(held.SafeFileHandle, out var metadata));
        Assert.Equal(8193UL, ((ulong)metadata.SizeHigh << 32) | metadata.SizeLow);
        Assert.True(metadata.Links >= 1);
        Assert.NotEqual(0UL, ((ulong)metadata.IndexHigh << 32) | metadata.IndexLow);
        Assert.Throws<ArgumentException>(() => disk.ReadHandleInformation<int>(held.SafeFileHandle, out _));
        Assert.Equal(8193, held.Length);
    }

    [Fact]
    public async Task UnsupportedWholeVolumeQuerySelectsStandardBackendWithoutPublishingDiscardedData()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert.True(DiskAccess.Default.CanUseNtfs(root));
        var source = new UnsupportedSource();
        using var stop = new CancellationTokenSource();
        var events = new List<ScanDiagnostic>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DiskAccess(() => source).ScanAsync(root,
            cancellationToken: stop.Token, diagnostic: item =>
            {
                events.Add(item);
                // Stop at backend selection, before enumerating this real drive.
                if (item.Event == "backend-selected" && item.Backend == "Standard Windows scan") stop.Cancel();
            }));
        Assert.Equal(1, source.Calls);
        Assert.Contains(events, item => item.Event == "backend-fallback" && item.Message.Contains("unsupported"));
        Assert.Contains(events, item => item.Event == "backend-selected" && item.Backend == "Standard Windows scan");
    }

    [Fact]
    public async Task SourceChangesAndUnsupportedQueriesKeepCurrentStreamSizes()
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "data.bin");
        File.WriteAllBytes(path, new byte[4096]); File.WriteAllBytes(path + ":extra", new byte[8192]);
        INtfsLayoutSource? source = null;
        var disk = new DiskAccess(() => source);
        var ordinary = await disk.MeasureFolderAsync(fixture.Path);
        Assert.Equal("Targeted Windows stream metadata", ordinary.Backend);
        Assert.Equal(12288, ordinary.LogicalBytes);

        source = new ScopedScannerTests.MetadataSource(ordinary);
        var fast = await disk.MeasureFolderAsync(fixture.Path);
        Assert.Equal("Fast NTFS scoped metadata", fast.Backend);
        Assert.Equal(ordinary.LogicalBytes, fast.LogicalBytes);
        Assert.Equal(ordinary.AllocatedBytes, fast.AllocatedBytes);

        source = new ScopedScannerTests.MetadataSource(ordinary) { Defect = "unsupported" };
        var fallback = await disk.MeasureFolderAsync(fixture.Path);
        Assert.Equal("Targeted Windows stream metadata", fallback.Backend);
        Assert.Equal(ordinary.LogicalBytes, fallback.LogicalBytes);
        Assert.NotNull(fallback.FallbackReason);
        source = null;
        Assert.Equal(ordinary.LogicalBytes, (await disk.MeasureFolderAsync(fixture.Path)).LogicalBytes);
    }

    [Fact]
    public void ContentAndNamespaceOperationsNeverAcquireNtfsAndPreserveCreateAndSharingSemantics()
    {
        using var fixture = new TestDirectory();
        var disk = new DiskAccess(() => throw new InvalidOperationException("No metadata query is needed."));
        string folder = Path.Join(fixture.Path, "owned"); disk.CreateDirectory(folder);
        string first = Path.Join(folder, "first.txt"), second = Path.Join(folder, "second.txt");
        disk.WriteAllText(first, "preserve");
        Assert.Equal("preserve", disk.ReadAllText(first));
        using (disk.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => disk.OpenRead(first));
        Assert.Throws<IOException>(() => disk.Open(first, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        disk.MoveFile(first, second);
        Assert.False(disk.FileExists(first)); Assert.True(disk.FileExists(second));
        Assert.Equal("preserve", disk.ReadAllText(second));
        disk.DeleteFile(second); disk.DeleteDirectory(folder);
        Assert.False(disk.DirectoryExists(folder));
    }

    [Fact]
    public async Task SharedSourceIsSerializedAcrossInstancesAndCancelledWaitersCanRestart()
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "data.bin"), "keep");
        var original = await new DiskAccess(() => null).MeasureFolderAsync(fixture.Path);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int active = 0, maximum = 0;
        var source = new ScopedScannerTests.MetadataSource(original)
        {
            BeforeQuery = () =>
            {
                int count = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, count);
                entered.Set();
                try { if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the metadata query."); }
                finally { Interlocked.Decrement(ref active); }
            }
        };
        var first = new DiskAccess(() => source);
        var second = new DiskAccess(() => source);
        var running = first.MeasureFolderAsync(fixture.Path);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.MeasureFolderAsync(fixture.Path, cancelled.Token));
            Assert.Single(source.Requests);
        }
        finally { release.Set(); }
        Assert.Equal(original.LogicalBytes, (await running).LogicalBytes);
        var together = await Task.WhenAll(first.MeasureFolderAsync(fixture.Path), second.MeasureFolderAsync(fixture.Path));
        Assert.All(together, snapshot => Assert.Equal("Fast NTFS scoped metadata", snapshot.Backend));
        Assert.Equal(1, maximum);
        Assert.Equal(0, source.WholeVolumeCalls);
    }

    private sealed class UnsupportedSource : INtfsLayoutSource
    {
        internal int Calls;
        public void Acquire(string volume, Action<NtfsLayoutBlock> consume, CancellationToken token)
        { Calls++; throw new NotSupportedException("This query is unsupported."); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WrittenLow, WrittenHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
}
