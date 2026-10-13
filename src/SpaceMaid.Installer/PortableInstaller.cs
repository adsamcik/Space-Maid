using System.IO;
using System.IO.Compression;

namespace SpaceMaid.Installer;

internal static class PortableInstaller
{
    private const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 250_000;

    public static async Task<string> ExtractAsync(Stream payload, string expectedHash, string destination,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string target = ValidateDestination(destination);
        if (!payload.CanSeek) throw new InvalidDataException("Installation files could not be read. Download Setup again.");
        payload.Position = 0;
        await PayloadIntegrity.VerifyAsync(payload, expectedHash, cancellationToken);
        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        var entries = ValidateArchive(archive);
        string parent = Path.GetDirectoryName(target)!;
        string staging = Path.Join(parent, "." + Path.GetFileName(target) + ".SpaceMaid-" + Guid.NewGuid().ToString("N"));
        SafeDirectories.EnsureNoReparseAncestors(staging);
        Directory.CreateDirectory(staging);
        try
        {
            long total = entries.Sum(item => item.Entry.Length);
            long copied = 0;
            byte[] buffer = new byte[128 * 1024];
            foreach (var item in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.Join(staging, item.Path);
                SafeDirectories.EnsureNoReparseAncestors(path);
                if (item.IsDirectory) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using Stream source = item.Entry.Open();
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true))
                {
                    long entryCopied = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        entryCopied = checked(entryCopied + count);
                        if (entryCopied > item.Entry.Length) throw InvalidArchive();
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        copied += count;
                        progress?.Report(total == 0 ? 0 : (double)copied / total);
                    }
                    if (entryCopied != item.Entry.Length) throw InvalidArchive();
                }
                File.SetLastWriteTimeUtc(path, item.Entry.LastWriteTime.UtcDateTime);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Recheck just before committing; an existing folder is never overwritten.
            ValidateDestination(target);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: false);
            Directory.Move(staging, target);
            progress?.Report(1);
            return target;
        }
        finally { SafeDirectories.TryDeleteOwnedDirectory(staging); }
    }

    internal static string ValidateDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
            throw new IOException("Choose a full folder path.");
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        string? root = Path.GetPathRoot(target);
        if (string.Equals(target, Path.TrimEndingDirectorySeparator(root ?? ""), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a folder inside the drive.");
        SafeDirectories.EnsureNoReparseAncestors(target);
        if (File.Exists(target)) throw new IOException("Choose a folder, not a file.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("This folder is not empty. Choose an empty folder.");
        string? parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent)) throw new IOException("The parent folder does not exist. Choose another location.");
        return target;
    }

    private static List<ArchiveItem> ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > MaximumEntries) throw InvalidArchive();
        var entries = new List<ArchiveItem>(archive.Entries.Count);
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            bool isDirectory = normalized.EndsWith('/');
            string[] parts = normalized.TrimEnd('/').Split('/');
            if (parts.Length == 0 || parts.Any(InvalidPart)) throw InvalidArchive();
            int unixKind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixKind is 0xa000 or 0x6000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw InvalidArchive();
            string path = Path.Combine(parts);
            if (!paths.TryAdd(path, isDirectory)) throw InvalidArchive();
            if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - total || (isDirectory && entry.Length != 0)) throw InvalidArchive();
            total += entry.Length;
            entries.Add(new(entry, path, isDirectory));
        }
        foreach (var item in entries)
        {
            string? parent = Path.GetDirectoryName(item.Path);
            while (!string.IsNullOrEmpty(parent))
            {
                if (paths.TryGetValue(parent, out bool directory) && !directory) throw InvalidArchive();
                parent = Path.GetDirectoryName(parent);
            }
        }
        foreach (string required in new[] { ".portable", "Update.exe", "Space Maid.exe", Path.Join("current", "SpaceMaid.App.exe"), Path.Join("current", "sq.version") })
            if (!paths.TryGetValue(required, out bool directory) || directory) throw InvalidArchive();
        return entries;
    }

    private static bool InvalidPart(string part)
    {
        if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return true;
        string basename = part.Split('.')[0];
        return basename.Equals("CON", StringComparison.OrdinalIgnoreCase) || basename.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || basename.Equals("AUX", StringComparison.OrdinalIgnoreCase) || basename.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (basename.Length == 4 && (basename.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || basename.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && basename[3] is >= '1' and <= '9');
    }

    private static InvalidDataException InvalidArchive() => new("Installation files are invalid. Download Setup again.");
    private sealed record ArchiveItem(ZipArchiveEntry Entry, string Path, bool IsDirectory);
}

internal static class SafeDirectories
{
    public static void EnsureNoReparseAncestors(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Choose a regular folder rather than a link or synced placeholder.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    public static void TryDeleteOwnedDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry, recursive: false);
                    else TryDeleteOwnedDirectory(entry);
                }
                else File.Delete(entry);
            }
            Directory.Delete(directory, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
