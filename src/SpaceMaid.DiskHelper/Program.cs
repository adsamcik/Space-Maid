using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Extensions.Containers;

namespace SpaceMaid.DiskHelper;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || !args[0].StartsWith("SpaceMaidDisk-", StringComparison.Ordinal) || args[0].Length > 80
            || args[0].Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') || !uint.TryParse(args[1], out uint parent)) return 2;
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(deadline.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint peer) || peer != parent) return 4;
            await VirtualDiskCleanup.WriteAsync(pipe, Environment.ProcessId, deadline.Token);
            var request = await VirtualDiskCleanup.ReadAsync<VirtualDiskRequest>(pipe, deadline.Token);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            VirtualDiskResult result;
            try { result = VirtualDiskCleanup.Compact(request); }
            catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
            { result = new(false, error.Message + " Compaction may have been partially applied if Windows had already started it. No disk file was deleted."); }
            await VirtualDiskCleanup.WriteAsync(pipe, result, CancellationToken.None);
            return result.Succeeded ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException) { return 1; }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint process);
}
