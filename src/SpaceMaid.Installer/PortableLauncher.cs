using System.Diagnostics;
using System.IO;
using SpaceMaid.Core.IO;

namespace SpaceMaid.Installer;

internal static class PortableLauncher
{
    private const string AppExecutable = "SpaceMaid.App.exe";
    private static readonly string[] RequiredEntries =
        [AppExecutable, "SpaceMaid.App.dll", "SpaceMaid.App.runtimeconfig.json", "coreclr.dll"];

    internal static async Task<int> LaunchAsync(InstallerPayloads payloads, string[] arguments)
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localData)) throw new IOException("The Windows local app data folder is unavailable.");
        string cache = Path.Combine(localData, "SpaceMaid.Portable", "Cache");
        string executable = await PrepareAsync(cache, payloads.PortableSha256, payloads.OpenPortable);
        var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new IOException("Windows could not start the cached app.");
        if (arguments.Length == 0) return 0;

        await child.WaitForExitAsync();
        return child.ExitCode;
    }

    internal static async Task<string> PrepareAsync(string cache, string hash, Func<Stream> openPayload)
    {
        if (!PayloadIntegrity.IsSha256(hash)) throw new InvalidDataException("The embedded app could not be verified.");
        string target = Path.Combine(cache, hash.ToLowerInvariant());
        string executable = Path.Combine(target, AppExecutable);
        SafeDirectories.EnsureNoReparseAncestors(target);
        if (IsReady(target)) return executable;

        DiskAccess.Default.CreateDirectory(cache);
        using Stream payload = openPayload();
        try { await PortableInstaller.ExtractCachedAsync(payload, hash, target, RequiredEntries); }
        // A simultaneous launch may finish the atomic move before this one.
        catch (IOException) when (IsReady(target)) { }
        if (!IsReady(target)) throw new IOException("The cached app is incomplete.");
        return executable;
    }

    private static bool IsReady(string target)
    {
        foreach (string name in RequiredEntries)
        {
            string path = Path.Combine(target, name);
            SafeDirectories.EnsureNoReparseAncestors(path);
            if (!DiskAccess.Default.FileExists(path) || DiskAccess.Default.FileLength(path) == 0) return false;
        }
        return true;
    }
}
