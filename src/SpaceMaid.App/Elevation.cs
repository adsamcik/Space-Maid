using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace SpaceMaid.App;

internal static class Elevation
{
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    public static bool TryRelaunch(out string? error)
    {
        error = null;
        try
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The app executable could not be found.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            // Also support launching the framework-dependent DLL through dotnet.
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(App).Assembly.Location);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return false; } // UAC was cancelled; keep this app running normally.
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { error = ex.Message; return false; }
    }
}
