using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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
    [Trait("Stage", "System")]
    public async Task OwnedHelperReusesItsProcessAfterCancellationOrNativeAccessFailureAndExitsOnDisconnect()
    {
        var connected = await HelperLayoutSource.CreateAsync(false);
        connected.Dispose(); await connected.Exit.WaitAsync(TimeSpan.FromSeconds(10));
        using var source = await HelperLayoutSource.CreateAsync(false);
        int processId = source.ProcessId;
        for (int request = 0; request < 2; request++)
        {
            using var stop = new CancellationTokenSource();
            int blocks = 0;
            var error = Record.Exception(() => source.Acquire(Path.GetPathRoot(Environment.CurrentDirectory)!, block =>
            {
                Assert.False(block.Data.IsEmpty); blocks++; stop.Cancel();
            }, stop.Token));
            // NTFS access depends on the volume's permissions, not merely the
            // administrator role. Bound a readable query to its first batch.
            if (blocks > 0)
                Assert.IsAssignableFrom<OperationCanceledException>(error);
            else
            {
                var denied = Assert.IsType<Win32Exception>(error);
                Assert.Equal(5, denied.NativeErrorCode);
            }
            Assert.True(source.IsAvailable); Assert.False(source.Exit.IsCompleted);
            Assert.Equal(processId, source.ProcessId);
        }
        Assert.Throws<InvalidDataException>(() => source.Acquire(@"G:\not-a-volume-root", _ => { }, CancellationToken.None));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => source.Acquire(Path.GetPathRoot(Environment.CurrentDirectory)!, _ => { }, cancelled.Token));
        }
        Assert.True(source.IsAvailable);
        source.Dispose();
        await source.Exit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(source.IsAvailable);
    }

    [Fact]
    public async Task SessionSupportsSuccessFailureCancellationAndLateCancelWithoutRestarting()
    {
        using var pair = await PipePair.ConnectAsync();
        using var finishLast = new ManualResetEventSlim();
        int calls = 0;
        var source = new SessionSource((volume, consume, token) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 2) throw new Win32Exception(5, "Owned access-denied fixture.");
            consume(new(new byte[] { (byte)call }, 2));
            if (call == 3)
            {
                if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cancel was not received.");
                token.ThrowIfCancellationRequested();
            }
            if (call == 4)
            {
                if (!finishLast.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Last request was not released.");
                token.ThrowIfCancellationRequested();
            }
        });
        var run = Task.Run(() => ScanPipeSession.RunAsync(pair.Client, source));
        try
        {
            Request(pair.Server, 1);
            var first = await Response(pair.Server, 1, ScanFrameKind.Batch);
            Assert.Equal(2, BinaryPrimitives.ReadDoubleLittleEndian(first.AsSpan(8)));
            Assert.Equal(1, first[16]);
            await Response(pair.Server, 1, ScanFrameKind.Complete);
            Request(pair.Server, 2);
            var failed = await Response(pair.Server, 2, ScanFrameKind.Failure);
            Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(failed.AsSpan(8)));
            Request(pair.Server, 3);
            await Response(pair.Server, 3, ScanFrameKind.Batch);
            ScanPipeProtocol.Write(pair.Server, ScanFrameKind.Cancel, BitConverter.GetBytes(3L));
            await Response(pair.Server, 3, ScanFrameKind.Cancelled);
            Request(pair.Server, 4);
            await Response(pair.Server, 4, ScanFrameKind.Batch);
            // This delayed command must not cancel request 4.
            ScanPipeProtocol.Write(pair.Server, ScanFrameKind.Cancel, BitConverter.GetBytes(3L));
            finishLast.Set();
            await Response(pair.Server, 4, ScanFrameKind.Complete);
            Assert.Equal(4, calls); Assert.False(run.IsCompleted);
        }
        finally { finishLast.Set(); pair.Server.Dispose(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task SessionRejectsCommandsOutsideItsReadOnlyScanContract()
    {
        using var pair = await PipePair.ConnectAsync();
        int calls = 0;
        var run = Task.Run(() => ScanPipeSession.RunAsync(pair.Client, new SessionSource((_, _, _) => calls++)));
        ScanPipeProtocol.Write(pair.Server, ScanFrameKind.Hello, []);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, calls);
    }

    [Fact]
    [Trait("Stage", "System")]
    public async Task DeadHelperIsUnavailableAndCanBeReplaced()
    {
        using var source = await HelperLayoutSource.CreateAsync(false);
        using (var process = Process.GetProcessById(source.ProcessId)) process.Kill();
        await source.Exit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(source.IsAvailable);
        Assert.Throws<IOException>(() => source.Acquire(Path.GetPathRoot(Environment.CurrentDirectory)!, _ => { }, default));
        using var replacement = await HelperLayoutSource.CreateAsync(false);
        Assert.True(replacement.IsAvailable);
        Assert.NotEqual(source.ProcessId, replacement.ProcessId);
        replacement.Dispose(); await replacement.Exit.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void Request(Stream pipe, long id)
    {
        byte[] payload = new byte[11]; BinaryPrimitives.WriteInt64LittleEndian(payload, id);
        System.Text.Encoding.ASCII.GetBytes(Path.GetPathRoot(Environment.CurrentDirectory)!).CopyTo(payload, 8);
        ScanPipeProtocol.Write(pipe, ScanFrameKind.Request, payload);
    }

    private static async Task<byte[]> Response(Stream pipe, long id, ScanFrameKind kind) => await Task.Run(() =>
    {
        var frame = ScanPipeProtocol.ReadHeader(pipe); Assert.Equal(kind, frame.Kind);
        byte[] data = new byte[frame.Length]; pipe.ReadExactly(data);
        Assert.Equal(id, BinaryPrimitives.ReadInt64LittleEndian(data)); return data;
    }).WaitAsync(TimeSpan.FromSeconds(10));

    private sealed class SessionSource(Action<string, Action<NtfsLayoutBlock>, CancellationToken> acquire) : INtfsLayoutSource
    {
        public void Acquire(string root, Action<NtfsLayoutBlock> consume, CancellationToken token) => acquire(root, consume, token);
    }

    private sealed class PipePair(NamedPipeServerStream server, NamedPipeClientStream client) : IDisposable
    {
        public NamedPipeServerStream Server => server;
        public NamedPipeClientStream Client => client;
        public static async Task<PipePair> ConnectAsync()
        {
            string name = "SpaceMaid-Test-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(10000)).WaitAsync(TimeSpan.FromSeconds(10));
                return new(server, client);
            }
            catch { server.Dispose(); client.Dispose(); throw; }
        }
        public void Dispose() { server.Dispose(); client.Dispose(); }
    }

    private sealed class FixtureSource(byte[][] batches) : INtfsLayoutSource
    {
        public void Acquire(string root, Action<NtfsLayoutBlock> consume, CancellationToken token)
        { foreach (byte[] batch in batches) { token.ThrowIfCancellationRequested(); consume(new(batch, 2)); } }
    }

    [HelperAcceptanceFact]
    [Trait("Stage", "System")]
    public async Task MeasureBoundedNativeTransportAgainstDirectAcquisition()
    {
        string volume = Path.GetPathRoot(Environment.CurrentDirectory)!;
        using var source = await HelperLayoutSource.CreateAsync(true);
        var throughHelper = Measure(source, source.ProcessId);
        Assert.True(source.IsAvailable);
        source.Dispose();
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
