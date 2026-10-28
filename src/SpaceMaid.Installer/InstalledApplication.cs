using System.Diagnostics;
using System.IO;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Velopack;

namespace SpaceMaid.Installer;

internal sealed record InstalledApplication(string Folder, SemanticVersion Version)
{
    internal bool NeedsUpdate(string version) => Version < SemanticVersion.Parse(version);

    internal ProcessStartInfo UpdateCommand(string package)
    {
        var command = new ProcessStartInfo(Path.Join(Folder, "Update.exe")) { UseShellExecute = true };
        foreach (string argument in new[] { "apply", "--rootDir", Folder, "--package", package, "--norestart" })
            command.ArgumentList.Add(argument);
        return command;
    }
}

internal static class InstallationDiscovery
{
    internal static string DefaultFolder => Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceMaid");

    internal static InstalledApplication? Find() => Find(RegisteredFolders().Append(DefaultFolder));

    // Probe only this product's registration and its default location, never the user's drives.
    internal static InstalledApplication? Find(IEnumerable<string?> folders)
    {
        foreach (string? folder in folders)
            if (Read(folder) is { } installation) return installation;
        return null;
    }

    internal static InstalledApplication? Read(string? folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return null;
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (target.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(target)!), StringComparison.OrdinalIgnoreCase)) return null;
            SafeDirectories.EnsureNoReparseAncestors(target);
            if (File.Exists(Path.Join(target, ".portable"))) return null;
            foreach (string file in new[] { "Update.exe", Path.Join("current", "SpaceMaid.App.exe"), Path.Join("current", "sq.version") })
            {
                string path = Path.Join(target, file);
                SafeDirectories.EnsureNoReparseAncestors(path);
                if (!File.Exists(path)) return null;
            }
            using var reader = XmlReader.Create(Path.Join(target, "current", "sq.version"), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
            });
            XElement? metadata = XDocument.Load(reader).Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata");
            string? Value(string name) => metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == name)?.Value;
            if (Value("id") != "SpaceMaid" || Value("mainExe") != "SpaceMaid.App.exe"
                || !SemanticVersion.TryParse(Value("version"), out var version)) return null;
            return new(target, version);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException or XmlException or InvalidOperationException) { return null; }
    }

    private static IEnumerable<string> RegisteredFolders()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? folder = null;
            try
            {
                using RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using RegistryKey? registration = user.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SpaceMaid");
                folder = registration?.GetValue("InstallLocation") as string;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
            if (!string.IsNullOrWhiteSpace(folder)) yield return folder;
        }
    }
}
