using SpaceMaid.Core.IO;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace SpaceMaid.Extensions.Cloud;

internal sealed record CloudRoot(string Provider, string Path, string Source)
{
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
    public string Label => Provider + " · " + Name;
}
internal sealed record CloudDiscoveryResult(IReadOnlyList<CloudRoot> Roots, IReadOnlyList<string> Warnings);

internal static class CloudDiscovery
{
    internal static readonly string[] Providers = ["OneDrive", "Google Drive", "Dropbox", "Proton Drive"];
    private const string SyncRootsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    internal static CloudDiscoveryResult Find(CancellationToken token, DiskAccess? files = null)
    {
        files ??= DiskAccess.Default;
        var roots = new List<CloudRoot>();
        var warnings = new List<string>();
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? "";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            Try(() =>
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var manager = registry.OpenSubKey(SyncRootsKey);
                foreach (string id in manager?.GetSubKeyNames().Take(256) ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    // Use this user's registered root only; never inspect another account.
                    using var key = manager!.OpenSubKey(id + @"\UserSyncRoots");
                    if (key?.GetValue(sid) is string path && ProviderFor(id) is { } provider)
                        roots.Add(new(provider, path, "Windows sync registration"));
                }
            }, "Some Windows sync registrations could not be read.");
        Try(() =>
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (string name in accounts?.GetSubKeyNames().Take(64) ?? [])
            {
                token.ThrowIfCancellationRequested();
                using var account = accounts!.OpenSubKey(name);
                if (account?.GetValue("UserFolder") is string path) roots.Add(new("OneDrive", path, "OneDrive account"));
            }
        }, "OneDrive account folders could not be read.");
        foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path) roots.Add(new("OneDrive", path, "OneDrive environment"));
        foreach (var location in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
            Try(() =>
            {
                token.ThrowIfCancellationRequested();
                string file = Path.Join(Environment.GetFolderPath(location), "Dropbox", "info.json");
                if (files.FileExists(file)) roots.AddRange(ParseDropbox(ReadSmallFile(file, files)));
            }, "Dropbox folder configuration could not be read. Use Add folder.");
        Try(() =>
        {
            // Some Drive for desktop versions use a virtual volume rather than
            // Windows Cloud Files. This is a discovery hint, never a capability.
            foreach (var drive in files.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (drive.IsReady && drive.VolumeLabel.StartsWith("Google Drive", StringComparison.OrdinalIgnoreCase))
                        roots.Add(new("Google Drive", drive.Name, "Mounted Drive for desktop volume"));
                }
                catch (IOException) { }
            }
        }, "Mounted Google Drive volumes could not be checked. Use Add folder.");
        return new(Merge(roots), warnings);

        void Try(Action action, string warning)
        {
            token.ThrowIfCancellationRequested();
            try { action(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or JsonException or ArgumentException)
            { if (!warnings.Contains(warning)) warnings.Add(warning); }
        }
    }

    internal static string? ProviderFor(string registration)
    {
        string id = registration.Split('!')[0].Replace(" ", "", StringComparison.Ordinal);
        if (id.Contains("OneDrive", StringComparison.OrdinalIgnoreCase)) return "OneDrive";
        if (id.Contains("GoogleDrive", StringComparison.OrdinalIgnoreCase) || id.Equals("DriveFS", StringComparison.OrdinalIgnoreCase)) return "Google Drive";
        if (id.Contains("Dropbox", StringComparison.OrdinalIgnoreCase)) return "Dropbox";
        if (id.Contains("ProtonDrive", StringComparison.OrdinalIgnoreCase)) return "Proton Drive";
        return null;
    }

    internal static IReadOnlyList<CloudRoot> ParseDropbox(string json)
    {
        using var document = JsonDocument.Parse(json);
        var roots = new List<CloudRoot>();
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid Dropbox configuration.");
        foreach (var account in document.RootElement.EnumerateObject().Take(64))
            if (account.Value.ValueKind == JsonValueKind.Object && account.Value.TryGetProperty("path", out var path)
                && path.ValueKind == JsonValueKind.String && path.GetString() is { } value)
                roots.Add(new("Dropbox", value, "Dropbox " + account.Name));
        return Merge(roots);
    }

    internal static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.AsSpan(2).Contains(':'))
            throw new ArgumentException("Choose a local sync folder or mounted drive.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static IReadOnlyList<CloudRoot> Merge(IEnumerable<CloudRoot> roots)
    {
        var result = new List<CloudRoot>();
        foreach (var root in roots)
        {
            if (!Providers.Contains(root.Provider)) continue;
            try
            {
                var value = root with { Path = Normalize(root.Path) };
                if (!result.Any(existing => existing.Path.Equals(value.Path, StringComparison.OrdinalIgnoreCase))) result.Add(value);
            }
            catch (ArgumentException) { }
            if (result.Count == 64) break;
        }
        return result.OrderBy(root => root.Provider).ThenBy(root => root.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string ReadSmallFile(string path, DiskAccess? files = null)
    {
        using var stream = (files ?? DiskAccess.Default).Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > 262144) throw new IOException("Folder settings are too large.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
