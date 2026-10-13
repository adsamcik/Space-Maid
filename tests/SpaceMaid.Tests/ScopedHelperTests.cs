using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ScopedHelperTests
{
    [Fact]
    public void NativeFilterAndTransportRetainFullFileReferencesWithinTheBoundedSelection()
    {
        FileIdentity[] selected = [new(0x0007000000000042), new(0x0012000000000088)];
        byte[] input = NativeNtfsLayoutSource.CreateFilesInput(selected);
        Assert.Equal(48, input.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(input));
        Assert.Equal(0x35u, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(12)));
        for (int i = 0; i < selected.Length; i++)
        {
            Assert.Equal(selected[i].Low, BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(16 + 16 * i)));
            Assert.Equal(selected[i].Low, BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(24 + 16 * i)));
        }
        byte[] payload = ScanPipeProtocol.CreateFilesRequest(9, @"C:\", selected);
        Assert.Equal(31, payload.Length);
        Assert.Equal(9, BinaryPrimitives.ReadInt64LittleEndian(payload));
        Assert.Equal(selected, ScanPipeProtocol.ReadFilesRequest(payload));
        var maximum = Enumerable.Range(1, ScanPipeProtocol.MaximumFileIds).Select(id => new FileIdentity((ulong)id)).ToArray();
        Assert.Equal(maximum, ScanPipeProtocol.ReadFilesRequest(ScanPipeProtocol.CreateFilesRequest(10, @"C:\", maximum)));
    }

    public static IEnumerable<object[]> InvalidSelections()
    {
        yield return [Array.Empty<FileIdentity>()];
        yield return [new FileIdentity[] { new(0) }];
        yield return [new FileIdentity[] { new(7, 1) }];
        yield return [new FileIdentity[] { new(7), new(7) }];
        yield return [Enumerable.Range(1, ScanPipeProtocol.MaximumFileIds + 1).Select(id => new FileIdentity((ulong)id)).ToArray()];
    }

    [Theory]
    [MemberData(nameof(InvalidSelections))]
    public void InvalidScopedSelectionsCannotOpenTheVolumeOrBecomeUnfilteredRequests(FileIdentity[] selected)
    {
        Assert.Throws<InvalidDataException>(() => NativeNtfsLayoutSource.CreateFilesInput(selected));
        Assert.Throws<InvalidDataException>(() => ScanPipeProtocol.CreateFilesRequest(1, @"C:\", selected));
        var source = new NativeNtfsLayoutSource();
        Assert.Throws<InvalidDataException>(() => source.AcquireFiles("not-a-volume", selected, _ => throw new InvalidOperationException(), default));
    }

    [Fact]
    public void ExistingWholeVolumeSourcesReportScopedQueriesAsUnsupported()
    {
        INtfsLayoutSource source = new WholeVolumeOnly();
        Assert.Throws<NotSupportedException>(() => source.AcquireFiles(@"C:\", [new(7)], _ => { }, default));
    }

    [Fact]
    public async Task ScopedSessionPreservesSelectionAndCanBeReusedAfterCancellationAndFailure()
    {
        using var pair = await PipePair.ConnectAsync();
        var observed = new List<FileIdentity[]>();
        var source = new ScopedSource((_, files, consume, token) =>
        {
            observed.Add(files.ToArray());
            int call = observed.Count;
            if (call == 2) throw new Win32Exception(5, "Owned filtered access-denied fixture.");
            consume(new(new byte[] { (byte)call }, 2));
            if (call == 1)
            {
                if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("Scoped cancellation was not received.");
                token.ThrowIfCancellationRequested();
            }
        });
        var run = Task.Run(() => ScanPipeSession.RunAsync(pair.Client, source));
        try
        {
            FileIdentity[] first = [new(0x0007000000000042), new(0x0012000000000088)];
            Request(pair.Server, 1, first);
            await Response(pair.Server, 1, ScanFrameKind.Batch);
            ScanPipeProtocol.Write(pair.Server, ScanFrameKind.Cancel, BitConverter.GetBytes(1L));
            await Response(pair.Server, 1, ScanFrameKind.Cancelled);
            Request(pair.Server, 2, [new(100)]);
            var failure = await Response(pair.Server, 2, ScanFrameKind.Failure);
            Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(failure.AsSpan(8)));
            Request(pair.Server, 3, [new(200), new(201)]);
            await Response(pair.Server, 3, ScanFrameKind.Batch);
            // A delayed cancellation for the first request must remain harmless.
            ScanPipeProtocol.Write(pair.Server, ScanFrameKind.Cancel, BitConverter.GetBytes(1L));
            await Response(pair.Server, 3, ScanFrameKind.Complete);
            Assert.Equal(first, observed[0]);
            Assert.Equal(new FileIdentity[] { new(100) }, observed[1]);
            Assert.Equal(new FileIdentity[] { new(200), new(201) }, observed[2]);
            Assert.Equal(0, source.WholeVolumeCalls);
            Assert.False(run.IsCompleted);
        }
        finally { pair.Server.Dispose(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("count")]
    [InlineData("negative")]
    [InlineData("zero-id")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    public async Task MalformedScopedCommandsAreRejectedBeforeAcquisition(string defect)
    {
        using var pair = await PipePair.ConnectAsync();
        int calls = 0;
        var source = new ScopedSource((_, _, _, _) => calls++);
        var run = Task.Run(() => ScanPipeSession.RunAsync(pair.Client, source));
        byte[] payload = ScanPipeProtocol.CreateFilesRequest(1, Path.GetPathRoot(Environment.CurrentDirectory)!, [new(7), new(8)]);
        switch (defect)
        {
            case "empty": payload = payload[..15]; BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(11), 0); break;
            case "count": BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(11), 1); break;
            case "negative": BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(11), -1); break;
            case "zero-id": BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(15), 0); break;
            case "duplicate": BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(23), 7); break;
            case "oversized": payload = new byte[ScanPipeProtocol.MaximumFilesRequestPayload + 8]; break;
        }
        // Rejection may close the pipe before an oversized payload finishes.
        try { ScanPipeProtocol.Write(pair.Server, ScanFrameKind.FilesRequest, payload); }
        catch (IOException) { }
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, calls); Assert.Equal(0, source.WholeVolumeCalls);
    }

    private static void Request(Stream pipe, long id, IReadOnlyList<FileIdentity> selected) =>
        ScanPipeProtocol.Write(pipe, ScanFrameKind.FilesRequest,
            ScanPipeProtocol.CreateFilesRequest(id, Path.GetPathRoot(Environment.CurrentDirectory)!, selected));

    private static async Task<byte[]> Response(Stream pipe, long id, ScanFrameKind kind) => await Task.Run(() =>
    {
        var frame = ScanPipeProtocol.ReadHeader(pipe); Assert.Equal(kind, frame.Kind);
        byte[] data = new byte[frame.Length]; pipe.ReadExactly(data);
        Assert.Equal(id, BinaryPrimitives.ReadInt64LittleEndian(data)); return data;
    }).WaitAsync(TimeSpan.FromSeconds(10));

    private sealed class WholeVolumeOnly : INtfsLayoutSource
    {
        public void Acquire(string volume, Action<NtfsLayoutBlock> consume, CancellationToken token) =>
            throw new InvalidOperationException("The fixture must not receive a whole-volume request.");
    }

    private sealed class ScopedSource(Action<string, IReadOnlyList<FileIdentity>, Action<NtfsLayoutBlock>, CancellationToken> acquire) : INtfsLayoutSource
    {
        public int WholeVolumeCalls { get; private set; }
        public void Acquire(string volume, Action<NtfsLayoutBlock> consume, CancellationToken token) => WholeVolumeCalls++;
        public void AcquireFiles(string volume, IReadOnlyList<FileIdentity> files, Action<NtfsLayoutBlock> consume, CancellationToken token) =>
            acquire(volume, files, consume, token);
    }

    private sealed class PipePair(NamedPipeServerStream server, NamedPipeClientStream client) : IDisposable
    {
        public NamedPipeServerStream Server => server;
        public NamedPipeClientStream Client => client;
        public static async Task<PipePair> ConnectAsync()
        {
            string name = "SpaceMaid-Scoped-Test-" + Guid.NewGuid().ToString("N");
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
}
