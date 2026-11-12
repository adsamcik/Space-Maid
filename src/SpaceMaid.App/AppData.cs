using System.IO;
using Velopack.Locators;

namespace SpaceMaid.App;

internal static class AppData
{
#if SPACE_MAID_PORTABLE_EXE
    internal static bool IsPortableExe => true;
#else
    internal static bool IsPortableExe => false;
#endif

    internal static string Root
    {
        get
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (IsPortableExe) return Path.Combine(localData, "SpaceMaid.Portable", "UserData");
            try { return ResolveRoot(VelopackLocator.Current.IsPortable, VelopackLocator.Current.RootAppDir, localData); }
            // WPF designers and component tests construct views without the process entrypoint.
            catch (InvalidOperationException) { return ResolveRoot(false, null, localData); }
        }
    }

    internal static string ResolveRoot(bool portable, string? applicationRoot, string localData)
        => portable && !string.IsNullOrEmpty(applicationRoot)
            ? Path.Combine(applicationRoot, "UserData") : Path.Combine(localData, "SpaceMaid");

    internal static string File(string name) => Path.Combine(Root, name);
}
