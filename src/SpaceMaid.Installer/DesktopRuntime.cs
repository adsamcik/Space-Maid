using System.IO;
using Microsoft.Win32;

namespace SpaceMaid.Installer;

internal static class DesktopRuntime
{
    public static bool IsAvailable()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT_X64") ?? Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
        roots.Add(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? location = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            if (location?.GetValue("InstallLocation") is string installed) roots.Add(installed);
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        foreach (string candidate in roots)
        {
            try
            {
                string shared = Path.Join(candidate, "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(shared) && Directory.EnumerateDirectories(shared).Any(directory =>
                    Version.TryParse(Path.GetFileName(directory), out Version? version) && version.Major == 10)) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }
}
