using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using SpaceMaid.App;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class NtfsJournalTests
{
    [Fact]
    public void JournalCheckpointsRejectGenerationVolumeRetentionAndCursorGaps()
    {
        var checkpoint = new NtfsJournalCheckpoint(7, 100, 400);
        var state = new NtfsChangeJournal.State(new(7, 100, 900), 100, 200);
        NtfsChangeJournal.ValidateRange(checkpoint, state);
        Assert.Throws<IOException>(() => NtfsChangeJournal.ValidateRange(checkpoint with { VolumeSerial = 8 }, state));
        Assert.Throws<IOException>(() => NtfsChangeJournal.ValidateRange(checkpoint with { JournalId = 101 }, state));
        Assert.Throws<IOException>(() => NtfsChangeJournal.ValidateRange(checkpoint with { NextUsn = 99 }, state));
        Assert.Throws<IOException>(() => NtfsChangeJournal.ValidateRange(checkpoint with { NextUsn = 199 }, state));
        Assert.Throws<IOException>(() => NtfsChangeJournal.ValidateRange(checkpoint with { NextUsn = 901 }, state));
        byte[] native = new byte[56];
        BinaryPrimitives.WriteUInt64LittleEndian(native, 100);
        BinaryPrimitives.WriteInt64LittleEndian(native.AsSpan(8), 100);
        BinaryPrimitives.WriteInt64LittleEndian(native.AsSpan(16), 900);
        BinaryPrimitives.WriteInt64LittleEndian(native.AsSpan(24), 200);
        Assert.Equal(state, NtfsChangeJournal.ParseState(native, 7));
        Assert.Throws<InvalidDataException>(() => NtfsChangeJournal.ParseState(native.AsSpan(0, 55), 7));
        BinaryPrimitives.WriteInt64LittleEndian(native.AsSpan(24), 1000);
        Assert.Throws<InvalidDataException>(() => NtfsChangeJournal.ParseState(native, 7));
    }

    [Fact]
    public void NativeRecordsRetainFullIdsParentsReasonsAndTheFixedFence()
    {
        byte[] first = NativeRecord(2, 400, 0x0007000000000042, 0x0012000000000088, 0x00001000);
        byte[] second = NativeRecord(3, 480, 0x0007000000000042, 0x0012000000000099, 0x00002000);
        byte[] later = NativeRecord(2, 600, 900, 5, 0x100);
        byte[] data = Buffer(680, first, second, later);
        var changes = new List<NtfsJournalChange>();
        Assert.Equal(680, NtfsChangeJournal.ParseRecords(data, 400, 600, changes));
        Assert.Equal(new NtfsJournalChange(new(0x0007000000000042), new(0x0012000000000088), 0x1000, FileAttributes.Directory), changes[0]);
        Assert.Equal(new FileIdentity(0x0012000000000099), changes[1].ParentIdentity);
        Assert.Equal(2, changes.Count);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("version")]
    [InlineData("minor")]
    [InlineData("length")]
    [InlineData("name")]
    [InlineData("identity")]
    [InlineData("wide-id")]
    [InlineData("order")]
    [InlineData("no-progress")]
    public void MalformedOrUnsupportedNativeRecordsNeverProduceAUsableDelta(string defect)
    {
        byte[] record = NativeRecord(3, 400, 42, 5, 0x100);
        long continuation = 500;
        switch (defect)
        {
            case "short": record = record[..20]; break;
            case "version": BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 4); break;
            case "minor": BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 1); break;
            case "length": BinaryPrimitives.WriteUInt32LittleEndian(record, 79); break;
            case "name": BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(72), 5); break;
            case "identity": BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), 0); break;
            case "wide-id": BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), 1); break;
            case "order": BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(40), 399); break;
            case "no-progress": continuation = 400; break;
        }
        Assert.NotNull(Record.Exception(() => NtfsChangeJournal.ParseRecords(Buffer(continuation, record), 400, 500, [])));
    }

    [Fact]
    public void TransportRoundTripsOnlyBoundedValidatedJournalMetadata()
    {
        var checkpoint = new NtfsJournalCheckpoint(7, 100, 400);
        var changes = new NtfsJournalChanges(checkpoint, [new(new(0x0007000000000042), new(5), 0x2000, FileAttributes.Directory)]);
        var decoded = NtfsChangeJournal.Decode(NtfsChangeJournal.Encode(changes));
        Assert.Equal(checkpoint, decoded.Checkpoint); Assert.Equal(changes.Changes, decoded.Changes);
        Assert.Null(ScanPipeProtocol.ReadJournalRequest(ScanPipeProtocol.CreateJournalRequest(10, @"C:\", null)));
        Assert.Equal(checkpoint, ScanPipeProtocol.ReadJournalRequest(ScanPipeProtocol.CreateJournalRequest(11, @"C:\", checkpoint)));
        Assert.Throws<InvalidDataException>(() => ScanPipeProtocol.CreateJournalRequest(11, @"C:\other", checkpoint));
        Assert.Throws<InvalidDataException>(() => ScanPipeProtocol.ReadJournalRequest(new byte[34]));
        byte[] data = NtfsChangeJournal.Encode(changes);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => NtfsChangeJournal.Decode(data));
        data = NtfsChangeJournal.Encode(changes); BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(28), 0);
        Assert.Throws<InvalidDataException>(() => NtfsChangeJournal.Decode(data));
    }

    [Fact]
    public async Task JournalSessionCanCaptureReadCancelAndRecoverWithoutWholeVolumeAcquisition()
    {
        using var pair = await PipePair.ConnectAsync();
        using var cancelling = new ManualResetEventSlim();
        var checkpoint = new NtfsJournalCheckpoint(7, 100, 400);
        int calls = 0;
        var source = new JournalSource((since, token) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1) { Assert.Null(since); return new(checkpoint, []); }
            Assert.Equal(checkpoint, since);
            if (call == 2) throw new Win32Exception(1178, "Owned expired journal fixture.");
            if (call == 3)
            {
                cancelling.Set();
                if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("Journal cancel was not received.");
                token.ThrowIfCancellationRequested();
            }
            return new(checkpoint with { NextUsn = 600 }, [new(new(42), new(5), 0x100, FileAttributes.Normal)]);
        });
        var run = Task.Run(() => ScanPipeSession.RunAsync(pair.Client, source));
        try
        {
            Request(pair.Server, 1, null);
            var captured = NtfsChangeJournal.Decode((await Response(pair.Server, 1, ScanFrameKind.Batch)).AsSpan(16));
            Assert.Equal(checkpoint, captured.Checkpoint); Assert.Empty(captured.Changes);
            await Response(pair.Server, 1, ScanFrameKind.Complete);
            Request(pair.Server, 2, checkpoint);
            Assert.Equal(1178, BinaryPrimitives.ReadInt32LittleEndian((await Response(pair.Server, 2, ScanFrameKind.Failure)).AsSpan(8)));
            Request(pair.Server, 3, checkpoint);
            Assert.True(cancelling.Wait(TimeSpan.FromSeconds(5)));
            ScanPipeProtocol.Write(pair.Server, ScanFrameKind.Cancel, BitConverter.GetBytes(3L));
            await Response(pair.Server, 3, ScanFrameKind.Cancelled);
            Request(pair.Server, 4, checkpoint);
            var changed = NtfsChangeJournal.Decode((await Response(pair.Server, 4, ScanFrameKind.Batch)).AsSpan(16));
            Assert.Single(changed.Changes);
            ScanPipeProtocol.Write(pair.Server, ScanFrameKind.Cancel, BitConverter.GetBytes(3L));
            await Response(pair.Server, 4, ScanFrameKind.Complete);
            Assert.Equal(4, calls); Assert.False(run.IsCompleted);
        }
        finally { pair.Server.Dispose(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData("length")]
    [InlineData("generation")]
    [InlineData("cursor")]
    public async Task MalformedJournalCommandsCannotReachTheNativeSource(string defect)
    {
        using var pair = await PipePair.ConnectAsync();
        int calls = 0;
        var source = new JournalSource((_, _) => { calls++; throw new InvalidOperationException("Unexpected journal acquisition."); });
        var run = Task.Run(() => ScanPipeSession.RunAsync(pair.Client, source));
        byte[] payload = ScanPipeProtocol.CreateJournalRequest(1, Path.GetPathRoot(Environment.CurrentDirectory)!, new(7, 100, 400));
        switch (defect)
        {
            case "length": payload = payload[..34]; break;
            case "generation": BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(19), 0); break;
            case "cursor": BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(27), -1); break;
        }
        try { ScanPipeProtocol.Write(pair.Server, ScanFrameKind.JournalRequest, payload); }
        catch (IOException) { } // Header rejection can close the pipe before the invalid payload is written.
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, calls);
    }

    [Fact]
    [Trait("Stage", "System")]
    public async Task OwnedHelperJournalPermissionOrCancellationDoesNotPoisonItsSession()
    {
        using var source = await HelperLayoutSource.CreateAsync(false);
        string volume = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var error = Record.Exception(() =>
        {
            var captured = source.ReadJournal(volume, null, default);
            Assert.Empty(captured.Changes);
            Assert.Equal(captured.Checkpoint.JournalId, source.ReadJournal(volume, captured.Checkpoint, default).Checkpoint.JournalId);
        });
        if (error is not null)
            Assert.Contains(Assert.IsType<Win32Exception>(error).NativeErrorCode, new[] { 1, 5, 50, 87, 1178, 1179 });
        Assert.True(source.IsAvailable);
        Assert.ThrowsAny<OperationCanceledException>(() => source.ReadJournal(volume, null, new(true)));
        Assert.True(source.IsAvailable);
        source.Dispose(); await source.Exit.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [ElevatedNtfsFact]
    [Trait("Stage", "System")]
    public async Task NativeJournalReportsOwnedCreatesAndMovesWithTheirStableIds()
    {
        using var fixture = new TestDirectory();
        var source = new NativeNtfsLayoutSource();
        string volume = Path.GetPathRoot(fixture.Path)!;
        var checkpoint = source.ReadJournal(volume, null, default).Checkpoint;
        string first = Path.Join(fixture.Path, "original.dat"), moved = Path.Join(fixture.Path, "moved.dat");
        File.WriteAllText(first, "owned journal fixture"); File.Move(first, moved);
        var scan = await new VolumeScanner().MeasureFolderAsync(fixture.Path);
        var identity = scan.Records[scan.Entry(scan.FindEntry(moved)!.Value).FileIndex].Identity;
        var delta = source.ReadJournal(volume, checkpoint, default);
        Assert.Equal(checkpoint.JournalId, delta.Checkpoint.JournalId);
        Assert.Contains(delta.Changes, change => change.Identity == identity && (change.Reason & 0x100) != 0);
        Assert.Contains(delta.Changes, change => change.Identity == identity && (change.Reason & 0x2000) != 0);
    }

    private static byte[] NativeRecord(ushort version, long usn, ulong identity, ulong parent, uint reason)
    {
        int header = version == 2 ? 60 : 76;
        byte[] record = new byte[(header + 2 + 7) & ~7];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), version);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), identity);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(version == 2 ? 16 : 24), parent);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(version == 2 ? 24 : 40), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(version == 2 ? 40 : 56), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(version == 2 ? 52 : 68), (uint)FileAttributes.Directory);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(version == 2 ? 56 : 72), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(version == 2 ? 58 : 74), (ushort)header);
        record[header] = (byte)'a'; return record;
    }

    private static byte[] Buffer(long next, params byte[][] records)
    {
        byte[] buffer = new byte[8 + records.Sum(record => record.Length)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, next);
        int offset = 8;
        foreach (byte[] record in records) { record.CopyTo(buffer, offset); offset += record.Length; }
        return buffer;
    }

    private static void Request(Stream pipe, long id, NtfsJournalCheckpoint? since) =>
        ScanPipeProtocol.Write(pipe, ScanFrameKind.JournalRequest,
            ScanPipeProtocol.CreateJournalRequest(id, Path.GetPathRoot(Environment.CurrentDirectory)!, since));

    private static async Task<byte[]> Response(Stream pipe, long id, ScanFrameKind kind) => await Task.Run(() =>
    {
        var frame = ScanPipeProtocol.ReadHeader(pipe); Assert.Equal(kind, frame.Kind);
        byte[] data = new byte[frame.Length]; pipe.ReadExactly(data);
        Assert.Equal(id, BinaryPrimitives.ReadInt64LittleEndian(data)); return data;
    }).WaitAsync(TimeSpan.FromSeconds(10));

    private sealed class JournalSource(Func<NtfsJournalCheckpoint?, CancellationToken, NtfsJournalChanges> read) : INtfsLayoutSource
    {
        public void Acquire(string volume, Action<NtfsLayoutBlock> consume, CancellationToken token) => throw new InvalidOperationException("A journal read must not scan the volume.");
        public NtfsJournalChanges ReadJournal(string volume, NtfsJournalCheckpoint? since, CancellationToken token) => read(since, token);
    }

    private sealed class PipePair(NamedPipeServerStream server, NamedPipeClientStream client) : IDisposable
    {
        public NamedPipeServerStream Server => server;
        public NamedPipeClientStream Client => client;
        public static async Task<PipePair> ConnectAsync()
        {
            string name = "SpaceMaid-Journal-Test-" + Guid.NewGuid().ToString("N");
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
