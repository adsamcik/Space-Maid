using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.IO;

namespace SpaceMaid.App;

internal static class RecyclePermissionClient
{
    internal static async Task<RecyclePermissionReply> RepairAsync(DiskAccess files, RecyclePermissionRequest request, CancellationToken token)
    {
        var helper = Path.Join(AppContext.BaseDirectory, "ScanHelper", "SpaceMaid.ScanHelper.exe");
        for (string? part = helper; part is not null; part = Path.GetDirectoryName(part))
            if ((files.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The permission repair helper has an unsafe installation path.");
        using var image = files.Open(helper, FileMode.Open, FileAccess.Read, FileShare.Read);
        string directory = Path.GetDirectoryName(helper)!;
        using var assembly = files.Open(Path.Join(directory, "SpaceMaid.ScanHelper.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var core = files.Open(Path.Join(directory, "SpaceMaid.Core.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!files.FileExists(Path.Join(directory, "SpaceMaid.ScanHelper.runtimeconfig.json")))
            throw new IOException("The bundled permission repair helper is incomplete.");
        using var runtime = files.Open(Path.Join(directory, "SpaceMaid.ScanHelper.runtimeconfig.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dependencies = files.Open(Path.Join(directory, "SpaceMaid.ScanHelper.deps.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (string payload in new[] { "SpaceMaid.ScanHelper.dll", "SpaceMaid.Core.dll", "SpaceMaid.ScanHelper.runtimeconfig.json", "SpaceMaid.ScanHelper.deps.json" })
            if ((files.GetAttributes(Path.Join(directory, payload)) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The bundled permission repair helper has an unsafe dependency path.");
        string pipeName = "SpaceMaidRepair-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = directory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--repair-permission"); start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process? launched;
        try { launched = await Task.Run(() => Process.Start(start), token).ConfigureAwait(false); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return new(false, null); }
        using var process = launched ?? throw new IOException("The permission repair helper did not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != process.Id)
                throw new IOException("The permission repair helper could not be verified.");
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("The permission repair helper did not respond. Elevation must use the current Windows account."); }

        // After handing off the request, wait for the exact outcome. Cancelling
        // the recycle must not hide an ownership change that already happened.
        using var responseDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await RecyclePermissionRequest.WriteAsync(pipe, request, responseDeadline.Token).ConfigureAwait(false);
            return await RecyclePermissionRequest.ReadAsync<RecyclePermissionReply>(pipe, responseDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException)
        { throw new IOException("Permission repair could not be confirmed. Check the item's ownership before trying again.", error); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
