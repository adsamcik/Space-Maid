using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SpaceMaid.App;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class HelperTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void InvalidFramesAreRejectedBeforeReadingPayload(int offset)
    {
        using var stream = new MemoryStream(); ScanPipeProtocol.Write(stream, ScanFrameKind.Batch, new byte[16]);
        byte[] frame = stream.ToArray(); frame[offset] = 255;
        if (offset == 8) BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => ScanPipeProtocol.ReadHeader(new MemoryStream(frame)));
    }

    [Fact]
    public async Task RawBatchesAreParsedOnceInTheHostWithTheExistingAccountingAndPreview()
    {
        byte[] root = LayoutFixture.File(5, 5, ".", 1, 0, 0);
        LayoutFixture.Put32(root, 28, (uint)FileAttributes.Directory);
        var source = new FixtureSource([root, LayoutFixture.File(100, 5, "photo.jpg", 1, 8192, 4096)]);
        var updates = new List<ScanProgress>();
        var scan = await new VolumeScanner().ScanAsync(Path.GetPathRoot(Environment.CurrentDirectory)!,
            new InlineProgress<ScanProgress>(updates.Add), layoutSource: source);
        Assert.Equal("Fast NTFS scan", scan.Backend);
        Assert.Equal(2, scan.FileCount); Assert.Equal(8192, scan.LogicalBytes); Assert.Equal(4096, scan.AllocatedBytes);
        Assert.True(scan.IncludesAllDataStreams); Assert.True(scan.HasCompleteVolumeLinks);
        Assert.Contains(updates, update => update.Preliminary is { LargestFiles.Count: 1 });
        Assert.Equal("photo.jpg", scan.Entry(scan.FindEntry(Path.Join(scan.RootPath, "photo.jpg"))!.Value).Name);
    }

    [Fact]
    public async Task OwnedHelperAuthenticatesAndExitsOnDisconnectOrReportsNativeAccessFailure()
    {
        var connected = await HelperLayoutSource.CreateAsync(false);
        connected.Dispose(); await connected.Exit.WaitAsync(TimeSpan.FromSeconds(10));
        using var source = await HelperLayoutSource.CreateAsync(false);
        using var stop = new CancellationTokenSource();
        if (Elevation.IsAdministrator)
        {
            Assert.ThrowsAny<OperationCanceledException>(() => source.Acquire(Path.GetPathRoot(Environment.CurrentDirectory)!, _ => stop.Cancel(), stop.Token));
        }
        else
        {
            var error = Assert.Throws<Win32Exception>(() => source.Acquire(Path.GetPathRoot(Environment.CurrentDirectory)!, _ => { }, stop.Token));
            Assert.Equal(5, error.NativeErrorCode);
        }
        await source.Exit.WaitAsync(TimeSpan.FromSeconds(10));
        using var invalid = await HelperLayoutSource.CreateAsync(false);
        Assert.Throws<InvalidDataException>(() => invalid.Acquire(@"G:\not-a-volume-root", _ => { }, CancellationToken.None));
        await invalid.Exit.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class FixtureSource(byte[][] batches) : INtfsLayoutSource
    {
        public void Acquire(string root, Action<NtfsLayoutBlock> consume, CancellationToken token)
        { foreach (byte[] batch in batches) { token.ThrowIfCancellationRequested(); consume(new(batch, 2)); } }
    }

    [HelperAcceptanceFact]
    public async Task MeasureBoundedNativeTransportAgainstDirectAcquisition()
    {
        string volume = Path.GetPathRoot(Environment.CurrentDirectory)!;
        using var source = await HelperLayoutSource.CreateAsync(true);
        var throughHelper = Measure(source, source.ProcessId);
        await source.Exit.WaitAsync(TimeSpan.FromSeconds(10));
        var direct = Measure(new NativeNtfsLayoutSource(), Environment.ProcessId);
        // The direct probe needs elevation separately; run it in an already
        // elevated test process for this explicit platform measurement.
        Assert.Equal(32, throughHelper.Batches); Assert.Equal(32, direct.Batches);
        string output = Path.GetFullPath("artifacts/extensions/helper-transport.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Volume = volume,
            ThroughHelper = new { throughHelper.Batches, throughHelper.Bytes, throughHelper.ElapsedMs, NativeQueryMs = throughHelper.QueryMs, throughHelper.PeakProcessBytes },
            Direct = new { direct.Batches, direct.Bytes, direct.ElapsedMs, NativeQueryMs = direct.QueryMs, direct.PeakProcessBytes },
            Scope = "First 32 native metadata batches only; sequential uncontrolled cache state; no full inventory or whole-volume speed claim." }, new JsonSerializerOptions { WriteIndented = true }));

        (int Batches, long Bytes, double ElapsedMs, double QueryMs, long PeakProcessBytes) Measure(INtfsLayoutSource reader, int processId)
        {
            using var stop = new CancellationTokenSource();
            int count = 0; long bytes = 0, peak = 0; double query = 0;
            var timer = Stopwatch.StartNew();
            Assert.ThrowsAny<OperationCanceledException>(() => reader.Acquire(volume, block =>
            {
                query += block.QueryMilliseconds;
                if (block.Data.IsEmpty) return;
                Assert.True(block.Data.Length >= 16); count++; bytes += block.Data.Length;
                if (count != 32) return;
                using var process = Process.GetProcessById(processId); peak = process.PeakWorkingSet64;
                stop.Cancel();
            }, stop.Token));
            return (count, bytes, timer.Elapsed.TotalMilliseconds, query, peak);
        }
    }
}

internal sealed class HelperAcceptanceFactAttribute : FactAttribute
{
    public HelperAcceptanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SPACEMAID_HELPER_ACCEPTANCE") != "1" || !Elevation.IsAdministrator)
            Skip = "Explicit administrator-only bounded native transport measurement.";
    }
}
