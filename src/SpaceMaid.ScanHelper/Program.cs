using System.IO.Pipes;
using System.Runtime.InteropServices;
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
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != parent) return 3;
            byte[] hello = BitConverter.GetBytes(Environment.ProcessId);
            ScanPipeProtocol.Write(pipe, ScanFrameKind.Hello, hello);
            await ScanPipeSession.RunAsync(pipe, new NativeNtfsLayoutSource());
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or TimeoutException)
        {
            return 1;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint process);
}
