using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.IO;

namespace SpaceMaid.ScanHelper;

internal static partial class Program
{
    private static async Task<int> RepairPermissionAsync(string name, string parentId)
    {
        if (!name.StartsWith("SpaceMaidRepair-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name["SpaceMaidRepair-".Length..], "N", out _) ||
            !uint.TryParse(parentId, out uint parent)) return 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || identity.User is null) return 2;
        // The medium-integrity pipe server must never be able to impersonate
        // the elevated client while receiving the result.
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await pipe.ConnectAsync(deadline.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != parent ||
                !ServerRunsAs(server, identity.User)) return 3;
            var request = await RecyclePermissionRequest.ReadAsync<RecyclePermissionRequest>(pipe, deadline.Token);
            RecyclePermissionReply reply;
            try
            {
                if (request.Folder is null || request.Item is null) throw new InvalidDataException("Invalid permission repair target.");
                using var process = Process.GetCurrentProcess();
                if (!OpenProcessToken(process.Handle, 0x28, out var access)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                using (access)
                {
                    EnablePrivilege(access, "SeRestorePrivilege");
                    EnablePrivilege(access, "SeBackupPrivilege");
                    DiskAccess.Default.RepairRecyclePermission(request.Folder, request.Item, identity.User, deadline.Token);
                }
                reply = new(true, null);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException
                or UnauthorizedAccessException or Win32Exception or OperationCanceledException)
            { reply = new(false, error.Message); }
            await RecyclePermissionRequest.WriteAsync(pipe, reply, deadline.Token);
            return 0;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or JsonException
            or Win32Exception or InvalidOperationException or ArgumentException)
        { return 1; }
    }

    private static bool ServerRunsAs(uint pid, SecurityIdentifier user)
    {
        using var process = Process.GetProcessById(checked((int)pid));
        using var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid || !OpenProcessToken(handle.DangerousGetHandle(), 0x8, out var token)) return false;
        using (token)
        using (var serverIdentity = new WindowsIdentity(token.DangerousGetHandle()))
            return !process.HasExited && serverIdentity.User is { } caller && user.Equals(caller);
    }

    private static void EnablePrivilege(SafeAccessTokenHandle token, string name)
    {
        if (!LookupPrivilegeValue(null, name, out long luid)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var privilege = new TokenPrivilege { Count = 1, Low = (uint)luid, High = (int)(luid >> 32), Attributes = 2 };
        if (!AdjustTokenPrivileges(token, false, ref privilege, 0, 0, 0) || Marshal.GetLastPInvokeError() == 1300)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows did not grant the permission repair privileges.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege { public uint Count, Low; public int High; public uint Attributes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivilege privilege, int length, nint previous, nint returned);
}
