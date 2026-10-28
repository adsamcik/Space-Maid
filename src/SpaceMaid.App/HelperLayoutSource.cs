using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal sealed partial class HelperLayoutSource : INtfsLayoutSource, IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly object _requestGate = new(), _writeGate = new();
    private readonly int _processId;
    private long _request;
    private int _disposed;
    private readonly Task _exit;
    internal Task Exit => _exit;
    internal int ProcessId => _processId;
    internal bool IsAvailable => Volatile.Read(ref _disposed) == 0 && !_exit.IsCompleted && _pipe.IsConnected;
    private HelperLayoutSource(NamedPipeServerStream pipe, Process process)
    { _pipe = pipe; _processId = process.Id; _exit = ReapAsync(process); }

    internal static async Task<HelperLayoutSource> CreateAsync(bool elevate, CancellationToken token = default)
    {
        string path = Path.Join(AppContext.BaseDirectory, "ScanHelper", "SpaceMaid.ScanHelper.exe");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The bundled scan helper is unavailable.");
        string name = "SpaceMaid-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 64 * 1024);
        Process? process = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var close = timeout.Token.Register(() => pipe.Dispose());
        try
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = elevate, WorkingDirectory = Path.GetDirectoryName(path)!, CreateNoWindow = true };
            if (elevate) start.Verb = "runas";
            start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString());
            process = await Task.Run(() => Process.Start(start), token) ?? throw new IOException("The scan helper did not start.");
            await pipe.WaitForConnectionAsync(timeout.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || peer != process.Id)
                throw new IOException("The scan helper peer could not be verified.");
            await Task.Run(() =>
            {
                var frame = ScanPipeProtocol.ReadHeader(pipe);
                if (frame.Kind != ScanFrameKind.Hello || frame.Length != 4) throw new InvalidDataException("Invalid helper handshake.");
                byte[] bytes = new byte[4]; pipe.ReadExactly(bytes);
                if (BinaryPrimitives.ReadInt32LittleEndian(bytes) != process.Id) throw new InvalidDataException("Incorrect helper process.");
            }, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return new(pipe, process);
        }
        catch (Exception ex) when (timeout.IsCancellationRequested)
        {
            pipe.Dispose(); if (process is not null) _ = ReapAsync(process);
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new IOException("The scan helper did not connect in time.", ex);
        }
        catch { pipe.Dispose(); if (process is not null) _ = ReapAsync(process); throw; }
    }

    public void Acquire(string volume, Action<NtfsLayoutBlock> consume, CancellationToken token)
        => AcquireRequest(volume, null, consume, token);

    public NtfsJournalChanges ReadJournal(string volume, NtfsJournalCheckpoint? since, CancellationToken token)
    {
        if (since is { } checkpoint) NtfsChangeJournal.ValidateCheckpoint(checkpoint);
        NtfsJournalChanges? result = null;
        AcquireRequest(volume, null, block =>
        {
            if (result is not null) throw new InvalidDataException("The helper returned multiple NTFS journal results.");
            result = NtfsChangeJournal.Decode(block.Data.Span);
        }, token, journal: true, since);
        if (result is null) throw new InvalidDataException("The helper did not return an NTFS journal result.");
        if (since is { } previous && (result.Checkpoint.VolumeSerial != previous.VolumeSerial
            || result.Checkpoint.JournalId != previous.JournalId || result.Checkpoint.NextUsn < previous.NextUsn))
            throw new InvalidDataException("The helper returned an unrelated NTFS journal result.");
        if (since is null && result.Changes.Count != 0)
            throw new InvalidDataException("The helper returned changes without a journal checkpoint request.");
        return result;
    }

    public void AcquireFiles(string volume, IReadOnlyList<FileIdentity> files, Action<NtfsLayoutBlock> consume, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        NativeNtfsLayoutSource.ValidateFiles(files);
        var captured = files.ToArray();
        NativeNtfsLayoutSource.ValidateFiles(captured);
        AcquireRequest(volume, captured, consume, token);
    }

    private void AcquireRequest(string volume, FileIdentity[]? files, Action<NtfsLayoutBlock> consume, CancellationToken token,
        bool journal = false, NtfsJournalCheckpoint? since = null)
    {
        // Validation/cancellation before sending cannot poison the idle session.
        token.ThrowIfCancellationRequested();
        NativeNtfsLayoutSource.ValidateVolume(volume);
        lock (_requestGate)
        {
            token.ThrowIfCancellationRequested();
            if (!IsAvailable) throw new IOException("The fast-scan helper is no longer connected.");
            long id = ++_request;
            ScanFrameKind kind = journal ? ScanFrameKind.JournalRequest : files is null ? ScanFrameKind.Request : ScanFrameKind.FilesRequest;
            byte[] request;
            if (journal) request = ScanPipeProtocol.CreateJournalRequest(id, volume, since);
            else if (files is null)
            {
                request = new byte[11]; BinaryPrimitives.WriteInt64LittleEndian(request, id);
                Encoding.ASCII.GetBytes(volume).CopyTo(request, 8);
            }
            else request = ScanPipeProtocol.CreateFilesRequest(id, volume, files);
            bool terminal = false;
            ExceptionDispatchInfo? consumeFailure = null;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            using var drainTimeout = new CancellationTokenSource();
            using var abort = drainTimeout.Token.Register(Dispose);
            int cancellationSent = 0;
            void CancelRequest()
            {
                if (Interlocked.Exchange(ref cancellationSent, 1) != 0) return;
                drainTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                // Never block the UI's Cancel handler on a pipe write. The ID
                // makes a delayed command harmless even after the next request.
                _ = Task.Run(() =>
                {
                    try { lock (_writeGate) if (IsAvailable) ScanPipeProtocol.Write(_pipe, ScanFrameKind.Cancel, BitConverter.GetBytes(id)); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Dispose(); }
                });
            }
            try
            {
                lock (_writeGate) ScanPipeProtocol.Write(_pipe, kind, request);
                using var cancelled = token.Register(CancelRequest);
                while (true)
                {
                    var frame = ScanPipeProtocol.ReadHeader(_pipe);
                    if (frame.Kind is not (ScanFrameKind.Batch or ScanFrameKind.Complete or ScanFrameKind.Cancelled or ScanFrameKind.Failure)
                        || frame.Kind == ScanFrameKind.Batch && frame.Length < 16
                        || journal && frame.Kind == ScanFrameKind.Batch && frame.Length > 44 + NtfsChangeJournal.MaximumChanges * 24
                        || frame.Kind is ScanFrameKind.Complete or ScanFrameKind.Cancelled && frame.Length != 8
                        || frame.Kind == ScanFrameKind.Failure && frame.Length is not (>= 12 and <= 4108))
                        throw new InvalidDataException("Unexpected helper response.");
                    if (frame.Length > buffer.Length)
                    { ArrayPool<byte>.Shared.Return(buffer); buffer = ArrayPool<byte>.Shared.Rent(frame.Length); }
                    _pipe.ReadExactly(buffer.AsSpan(0, frame.Length));
                    if (BinaryPrimitives.ReadInt64LittleEndian(buffer) != id)
                        throw new InvalidDataException("Incorrect helper request.");
                    if (frame.Kind != ScanFrameKind.Batch)
                    {
                        terminal = true;
                        token.ThrowIfCancellationRequested();
                        consumeFailure?.Throw();
                        if (frame.Kind == ScanFrameKind.Failure)
                            throw new Win32Exception(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8)), Encoding.UTF8.GetString(buffer, 12, frame.Length - 12));
                        if (frame.Kind == ScanFrameKind.Cancelled) throw new IOException("The helper cancelled the scan.");
                        return;
                    }
                    double query = BinaryPrimitives.ReadDoubleLittleEndian(buffer.AsSpan(8));
                    if (!double.IsFinite(query) || query < 0) throw new InvalidDataException("Invalid helper timing.");
                    if (token.IsCancellationRequested || consumeFailure is not null) continue;
                    try { consume(new(buffer.AsMemory(16, frame.Length - 16), query)); }
                    catch (Exception ex) { consumeFailure = ExceptionDispatchInfo.Capture(ex); CancelRequest(); }
                }
            }
            catch (Exception ex)
            {
                if (!terminal) Dispose();
                if (token.IsCancellationRequested && ex is IOException or ObjectDisposedException)
                    throw new OperationCanceledException(token);
                consumeFailure?.Throw();
                if (ex is ObjectDisposedException)
                    throw new IOException("The fast-scan helper disconnected before finishing the request.", ex);
                throw;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pipe.Dispose();
    }
    private static async Task ReapAsync(Process process)
    {
        try { await process.WaitForExitAsync(); }
        finally { process.Dispose(); }
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint process);
}
