using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal sealed partial class HelperLayoutSource : INtfsLayoutSource, IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly Process _process;
    private bool _used;
    private int _disposed;
    private Task _exit = Task.CompletedTask;
    internal Task Exit => _exit;
    internal int ProcessId => _process.Id;
    private HelperLayoutSource(NamedPipeServerStream pipe, Process process) { _pipe = pipe; _process = process; }

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
    {
        if (_used) throw new IOException("This scan helper has already completed its request.");
        _used = true;
        using var cancelled = token.Register(() => _pipe.Dispose());
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            NativeNtfsLayoutSource.ValidateVolume(volume);
            ScanPipeProtocol.Write(_pipe, ScanFrameKind.Request, Encoding.ASCII.GetBytes(volume));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var frame = ScanPipeProtocol.ReadHeader(_pipe);
                if (frame.Kind == ScanFrameKind.Complete && frame.Length == 0) return;
                if (frame.Kind == ScanFrameKind.Failure && frame.Length is >= 4 and <= 4100)
                {
                    _pipe.ReadExactly(buffer.AsSpan(0, frame.Length));
                    throw new Win32Exception(BinaryPrimitives.ReadInt32LittleEndian(buffer), Encoding.UTF8.GetString(buffer, 4, frame.Length - 4));
                }
                if (frame.Kind != ScanFrameKind.Batch || frame.Length < 8) throw new InvalidDataException("Unexpected helper response.");
                if (frame.Length > buffer.Length)
                { ArrayPool<byte>.Shared.Return(buffer); buffer = ArrayPool<byte>.Shared.Rent(frame.Length); }
                _pipe.ReadExactly(buffer.AsSpan(0, frame.Length));
                double query = BinaryPrimitives.ReadDoubleLittleEndian(buffer);
                if (!double.IsFinite(query) || query < 0) throw new InvalidDataException("Invalid helper timing.");
                consume(new(buffer.AsMemory(8, frame.Length - 8), query));
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is IOException or ObjectDisposedException)
        { throw new OperationCanceledException(token); }
        finally { ArrayPool<byte>.Shared.Return(buffer); Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pipe.Dispose(); _exit = ReapAsync(_process);
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
