using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceMaid.Extensions.Containers;

internal static class WslDiskClient
{
    internal static Task<VirtualDiskTarget> PreviewAsync(string path, CancellationToken token)
        => Task.Run(() => { token.ThrowIfCancellationRequested(); return VirtualDiskCleanup.Capture(path); }, token);

    internal static async Task<VirtualDiskResult> ExecuteAsync(VirtualDiskRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        VirtualDiskCleanup.RequireUnchanged(request.Target, await PreviewAsync(request.Target.Path, token));
        string helper = Path.Join(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "DiskHelper", "SpaceMaid.DiskHelper.exe");
        ValidateHelper(helper);
        string name = "SpaceMaidDisk-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var start = new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(helper)!, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var process = await Task.Run(() => Process.Start(start), token) ?? throw new IOException("The disk helper did not start.");
        await pipe.WaitForConnectionAsync(deadline.Token);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || peer != process.Id)
            throw new IOException("The disk helper's process could not be verified.");
        int hello = await VirtualDiskCleanup.ReadAsync<int>(pipe, deadline.Token);
        if (hello != process.Id) throw new IOException("The disk helper handshake was not valid.");
        await VirtualDiskCleanup.WriteAsync(pipe, request, deadline.Token);
        // The native operation is synchronous. Once sent, keep the result channel
        // open; cancellation/closing cannot honestly imply that it was rolled back.
        try
        {
            var result = await VirtualDiskCleanup.ReadAsync<VirtualDiskResult>(pipe, CancellationToken.None);
            await process.WaitForExitAsync();
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        { throw new IOException("The helper result was lost. Compaction may have run; inspect the disk before trying again.", error); }
    }

    private static void ValidateHelper(string path)
    {
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("The disk helper's path must not contain links.");
        if (!File.Exists(path) || !File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")))
            throw new IOException("The bundled disk helper is unavailable. Reinstall the complete app package.");
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint process);
}
