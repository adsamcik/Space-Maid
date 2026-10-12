using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.LocalModel;

namespace SpaceMaid.ModelWorker;

internal static class WorkerIsolation
{
    internal static bool IsAppContainer(out bool lowPrivilege)
    {
        lowPrivilege = false;
        if (!OperatingSystem.IsWindows() || !OpenProcessToken(GetCurrentProcess(), 0x0008, out var token)) return false;
        using (token)
        {
            if (!GetTokenInformation(token, 29, out int appContainer, sizeof(int), out _) || appContainer != 1) return false;
            // TokenIsLessPrivilegedAppContainer is unavailable on some current
            // Windows builds. LPAC is defined by opting out of the common ALL
            // APPLICATION PACKAGES group. Verify that effective membership is
            // absent and the sole capability is the read-only Windows registry
            // bootstrap permission needed by the native DLL loader.
            nint capabilities = Marshal.AllocHGlobal(512);
            try
            {
                if (!GetTokenBuffer(token, 30, capabilities, 512, out _) || Marshal.ReadInt32(capabilities) != 1 ||
                    new SecurityIdentifier(Marshal.ReadIntPtr(capabilities, 8)).Value !=
                    "S-1-15-3-1024-1065365936-1281604716-3511738428-1654721687-432734479-3232135806-4053264122-3456934681" ||
                    !ConvertStringSidToSidW("S-1-15-2-1", out nint allPackages)) return false;
                try { lowPrivilege = CheckTokenMembershipEx(0, allPackages, 1, out bool member) && !member; }
                finally { LocalFree(allPackages); }
            }
            finally { Marshal.FreeHGlobal(capabilities); }
            return true;
        }
    }

    internal static void RedirectNativeOutputToError() => SetStdHandle(-11, GetStdHandle(-12));

    internal static async Task<SandboxProbeResult> ProbeAsync(SandboxProbeRequest request)
    {
        bool appContainer = IsAppContainer(out bool lowPrivilege);
        bool modelRead = CanOpen(request.ModelPath, FileAccess.Read);
        bool modelWriteBlocked = !CanOpen(request.ModelPath, FileAccess.Write);
        string runtimeFile = Path.Combine(request.RuntimeDirectory, "SpaceMaid.ModelWorker.runtimeconfig.json");
        bool runtimeWriteBlocked = CanOpen(runtimeFile, FileAccess.Read) && !CanOpen(runtimeFile, FileAccess.Write);
        bool outsideReadBlocked = !CanOpen(request.OutsideReadPath, FileAccess.Read);
        bool outsideWriteBlocked = !CanOpen(request.OutsideWritePath, FileAccess.Write);
        bool loopbackBlocked;
        string networkObservation;
        try
        {
            // An LPAC without capabilities may deny WSA startup itself.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, request.LoopbackPort), deadline.Token);
            loopbackBlocked = false;
            networkObservation = "connected";
        }
        catch (SocketException exception)
        {
            // Refused/missing listeners do not establish network isolation.
            loopbackBlocked = exception.SocketErrorCode == SocketError.AccessDenied;
            networkObservation = "socket_" + exception.SocketErrorCode + "_" + exception.NativeErrorCode;
        }
        catch (OperationCanceledException) { loopbackBlocked = false; networkObservation = "timeout"; }

        bool childBlocked;
        try
        {
            using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, Arguments = "/d /c exit 0" });
            childBlocked = child is null;
            if (child is not null) { await child.WaitForExitAsync(); }
        }
        catch (System.ComponentModel.Win32Exception) { childBlocked = true; }
        catch (UnauthorizedAccessException) { childBlocked = true; }

        bool? privateRegistryReadBlocked = null;
        if (request.PrivateRegistryKey is { } registryKey)
        {
            if (registryKey.Length > 1024 || registryKey.IndexOf('\0') >= 0)
                throw new InvalidDataException("Invalid registry probe fixture.");
            try
            {
                using var key = Registry.Users.OpenSubKey(registryKey, writable: false);
                privateRegistryReadBlocked = key is null;
            }
            catch (UnauthorizedAccessException) { privateRegistryReadBlocked = true; }
            catch (System.Security.SecurityException) { privateRegistryReadBlocked = true; }
        }

        return new(appContainer, lowPrivilege, modelRead, modelWriteBlocked, runtimeWriteBlocked,
            outsideReadBlocked, outsideWriteBlocked, loopbackBlocked, childBlocked,
            Environment.GetEnvironmentVariable("SPACEMAID_SANDBOX_TEST_SECRET") is null, networkObservation, privateRegistryReadBlocked);
    }

    private static bool CanOpen(string path, FileAccess access)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetStdHandle(int standardHandle, nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int information,
        int informationLength, out int returnLength);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenBuffer(SafeAccessTokenHandle token, int informationClass, nint information,
        int informationLength, out int returnLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSidW(string sid, out nint value);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckTokenMembershipEx(nint token, nint sid, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool member);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
}
