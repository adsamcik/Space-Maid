using System.IO;
using System.Security;
using Microsoft.Win32;

namespace SpaceMaid.Extensions.Apps;

internal sealed record AppRegistration(string Name, string? Publisher, string? Version,
    string? Path, long? ReportedBytes, string Source);
internal sealed record DetectedApp(string Name, string Publisher, string Version, string? Path,
    long? ReportedBytes, string Source, IReadOnlyList<string> Names, bool Overlaps);
internal sealed record AppDiscoveryResult(IReadOnlyList<DetectedApp> Apps, IReadOnlyList<string> Warnings);

internal static class AppDiscovery
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly EnumerationOptions Shallow = new() { IgnoreInaccessible = false, AttributesToSkip = 0 };

    internal static AppDiscoveryResult Discover(string drive, IReadOnlyList<string> addedFolders, CancellationToken token)
    {
        var warnings = new List<string>();
        var registrations = ReadRegistrations(warnings, token);
        int unknown = registrations.Where(app => app.Path is null).Select(app => app.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (unknown > 0) Warn(warnings, $"{unknown:N0} Windows installation records have no known folder and cannot be assigned to a drive.");
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in new[] { "Program Files", "Program Files (x86)", "Apps", "Applications", "Programs", "Games" })
            roots.Add(System.IO.Path.Join(drive, name));
        string users = System.IO.Path.Join(drive, "Users");
        foreach (string user in Directories(users, warnings, token))
            roots.Add(System.IO.Path.Join(user, "AppData", "Local", "Programs"));

        // Steam's common folder is a bounded directory listing, not a recursive
        // search of game assets. Registered Steam installations cover custom paths.
        var games = new HashSet<string>(StringComparer.Ordinal) { System.IO.Path.Join(drive, "SteamLibrary", "steamapps", "common") };
        foreach (var app in registrations.Where(app => app.Path is not null && OnDrive(app.Path, drive)))
            games.Add(System.IO.Path.Join(app.Path!, "steamapps", "common"));
        foreach (string root in roots)
        {
            foreach (string folder in Directories(root, warnings, token))
            {
                games.Add(System.IO.Path.Join(folder, "steamapps", "common"));
                if (registrations.Any(app => app.Path == folder) || HasExecutable(folder, warnings, token))
                { AddFolder(folder, "App folder"); continue; }
                // Covers vendor/product and the common bin layout, at one extra level.
                foreach (string child in Directories(folder, warnings, token))
                    if (HasExecutable(child, warnings, token))
                    {
                        if (System.IO.Path.GetFileName(child).Equals("bin", StringComparison.OrdinalIgnoreCase)) AddFolder(folder, "App folder");
                        else AddFolder(child, "App folder");
                    }
            }
        }
        foreach (string common in games)
            foreach (string folder in Directories(common, warnings, token)) AddFolder(folder, "Game folder");
        foreach (string folder in addedFolders.Where(folder => OnDrive(folder, drive))) AddFolder(folder, "Added folder");
        token.ThrowIfCancellationRequested();
        return new(Group(registrations.Where(app => app.Path is not null && OnDrive(app.Path, drive)).ToArray()), warnings);

        void AddFolder(string folder, string source)
        {
            // A registration with this exact path supplies the better name.
            if (NormalizeFolder(folder) is not null && !registrations.Any(app => app.Path == folder))
                registrations.Add(new(System.IO.Path.GetFileName(folder), null, null, folder, null, source));
        }
    }

    internal static List<AppRegistration> ReadRegistrations(List<string> warnings, CancellationToken token)
    {
        var result = new List<AppRegistration>();
        RegistryView[] views = Environment.Is64BitOperatingSystem ? [RegistryView.Registry64, RegistryView.Registry32] : [RegistryView.Registry32];
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in views)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallKey);
                if (uninstall is null) continue;
                foreach (string keyName in uninstall.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var key = uninstall.OpenSubKey(keyName);
                        if (key is null || key.GetValue("SystemComponent") is int hidden && hidden == 1) continue;
                        string? name = key.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string? path = NormalizeFolder(key.GetValue("InstallLocation") as string)
                            ?? FolderFromIcon(key.GetValue("DisplayIcon") as string);
                        result.Add(new(name.Trim(), key.GetValue("Publisher") as string, key.GetValue("DisplayVersion") as string,
                            path, ReportedSize(key.GetValue("EstimatedSize")), "Windows installation record"));
                    }
                    catch (Exception error) when (IsAccessError(error)) { Warn(warnings, "Some Windows installation records could not be read."); }
                }
            }
            catch (Exception error) when (IsAccessError(error)) { Warn(warnings, "Some Windows installation records could not be read."); }
        }
        return result;
    }

    internal static long? ReportedSize(object? value) => value switch
    {
        int size when size != 0 => (long)unchecked((uint)size) * 1024,
        long size when size > 0 && size <= long.MaxValue / 1024 => size * 1024,
        _ => null
    };

    internal static string? NormalizeFolder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).Replace('/', '\\');
            if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            // An install record or shared icon must not assign an entire drive,
            // profile, Windows tree or generic program root to one application.
            string relative = System.IO.Path.GetRelativePath(System.IO.Path.GetPathRoot(path)!, path);
            if (relative == "." || relative.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(@"Windows\", StringComparison.OrdinalIgnoreCase)
                || new[] { "Program Files", "Program Files (x86)", "Apps", "Applications", "Programs", "Games", "Users", "ProgramData" }
                    .Contains(relative, StringComparer.OrdinalIgnoreCase)) return null;
            if (path.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
                || path.Equals(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
                || path.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)) return null;
            return path;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal static string? FolderFromIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        icon = icon.Trim();
        string file;
        if (icon.StartsWith('"'))
        {
            int end = icon.IndexOf('"', 1);
            if (end < 0) return null;
            file = icon[1..end];
        }
        else
        {
            int comma = icon.LastIndexOf(',');
            file = comma > 0 && int.TryParse(icon[(comma + 1)..], out _) ? icon[..comma] : icon;
        }
        file = Environment.ExpandEnvironmentVariables(file.Trim());
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !System.IO.Path.IsPathFullyQualified(file)) return null;
        try { return File.Exists(file) ? NormalizeFolder(System.IO.Path.GetDirectoryName(file)) : null; }
        catch (ArgumentException) { return null; }
    }

    internal static IReadOnlyList<DetectedApp> Group(IReadOnlyList<AppRegistration> registrations)
    {
        // Do not coalesce distinct case-sensitive folders. No combined byte total
        // is presented: nested installs and external hard links can overlap.
        var result = registrations.GroupBy(app => app.Path ?? "?" + app.Name, StringComparer.Ordinal).Select(group =>
        {
            var ordered = group.OrderBy(app => app.Source == "Windows installation record" ? 0 : 1).ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var first = ordered[0];
            string[] names = ordered.Select(app => app.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            long? reported = names.Length == 1 ? ordered.Max(app => app.ReportedBytes) : null;
            return new DetectedApp(names.Length == 1 ? first.Name : first.Name + $" + {names.Length - 1} more",
                first.Publisher ?? "", names.Length == 1 ? first.Version ?? "" : "", first.Path, reported, first.Source, names, false);
        }).ToArray();
        return result.Select(app => app with { Overlaps = app.Path is not null && result.Any(other => other.Path is not null && other != app
            && (IsWithin(app.Path, other.Path) || IsWithin(other.Path, app.Path))) }).ToArray();
    }

    internal static bool OnDrive(string path, string drive) => string.Equals(System.IO.Path.GetPathRoot(path), drive, StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string path, string parent) => path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.Ordinal);
    private static bool HasExecutable(string folder, List<string> warnings, CancellationToken token)
    {
        try
        {
            if (!IsRegular(folder)) return false;
            foreach (string file in Directory.EnumerateFiles(folder, "*.exe", Shallow))
            {
                token.ThrowIfCancellationRequested();
                if (IsRegular(file)) return true;
            }
        }
        catch (Exception error) when (IsAccessError(error)) { Warn(warnings, "Some app folders could not be inspected."); }
        return false;
    }
    private static IEnumerable<string> Directories(string root, List<string> warnings, CancellationToken token)
    {
        if (!Directory.Exists(root)) yield break;
        IEnumerator<string>? iterator = null;
        try
        {
            if (IsRegular(root)) iterator = Directory.EnumerateDirectories(root, "*", Shallow).GetEnumerator();
        }
        catch (Exception error) when (IsAccessError(error)) { Warn(warnings, "Some app folders could not be inspected."); }
        if (iterator is null) yield break;
        using (iterator)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string folder;
                try { if (!iterator.MoveNext()) break; folder = iterator.Current; if (!IsRegular(folder)) continue; }
                catch (Exception error) when (IsAccessError(error)) { Warn(warnings, "Some app folders could not be inspected."); break; }
                yield return folder;
            }
        }
    }
    private static bool IsRegular(string path)
    {
        // Check ancestors too: a normal child under a junction is still outside
        // the intended drive. Metadata-only access never opens executable bytes.
        for (string? current = path; current is not null; current = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(current)))
            if ((File.GetAttributes(current) & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)(0x40000 | 0x400000))) != 0) return false;
        return true;
    }
    private static bool IsAccessError(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException;
    private static void Warn(List<string> warnings, string value) { if (!warnings.Contains(value)) warnings.Add(value); }
}
