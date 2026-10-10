using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.ScanHelper;

internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || !args[0].StartsWith("SpaceMaid-", StringComparison.Ordinal)
            || args[0].Length > 80 || args[0].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || !uint.TryParse(args[1], out uint parent)) return 2;
        using var cancellation = new CancellationTokenSource();
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
        Task? monitor = null;
        try
        {
            await pipe.ConnectAsync(15000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != parent) return 3;
            byte[] hello = BitConverter.GetBytes(Environment.ProcessId);
            ScanPipeProtocol.Write(pipe, ScanFrameKind.Hello, hello);
            var request = ScanPipeProtocol.ReadHeader(pipe);
            if (request.Kind != ScanFrameKind.Request || request.Length != 3) return 4;
            byte[] bytes = new byte[3]; pipe.ReadExactly(bytes);
            string volume = NativeNtfsLayoutSource.ValidateVolume(Encoding.ASCII.GetString(bytes));
            monitor = Task.Run(() =>
            {
                try
                {
                    var signal = ScanPipeProtocol.ReadHeader(pipe);
                    if (signal.Kind != ScanFrameKind.Cancel || signal.Length != 0) throw new InvalidDataException("Unexpected scan command.");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) { }
                finally { cancellation.Cancel(); }
            });
            new NativeNtfsLayoutSource().Acquire(volume, block =>
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    ScanPipeProtocol.WriteBlock(pipe, block);
                }, cancellation.Token);
            ScanPipeProtocol.Write(pipe, ScanFrameKind.Complete, []);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception or OperationCanceledException or UnauthorizedAccessException or TimeoutException)
        {
            if (pipe.IsConnected && !cancellation.IsCancellationRequested)
            {
                try
                {
                    byte[] message = Encoding.UTF8.GetBytes(ex.Message[..Math.Min(1024, ex.Message.Length)]);
                    byte[] error = new byte[4 + message.Length];
                    BinaryPrimitives.WriteInt32LittleEndian(error, (ex as Win32Exception)?.NativeErrorCode ?? 13);
                    message.CopyTo(error, 4); ScanPipeProtocol.Write(pipe, ScanFrameKind.Failure, error);
                }
                catch (IOException) { }
            }
            return 1;
        }
        finally { pipe.Dispose(); if (monitor is not null) await monitor; }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint process);
}
