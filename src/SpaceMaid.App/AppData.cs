using System.IO;
using Velopack.Locators;

namespace SpaceMaid.App;

internal static class AppData
{
    internal static string Root
    {
        get
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
