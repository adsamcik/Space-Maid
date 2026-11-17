using System.IO;
using System.Text.Json;
using SpaceMaid.Core.IO;

namespace SpaceMaid.Extensions.Apps;

// Owns only the user's explicitly added discovery shortcuts, never their contents.
internal sealed class AppFolderSettings(DiskAccess files, string path)
{
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumFolders = 64;
    internal IReadOnlyList<string> Folders { get; private set; } = [];
    internal string? Error { get; private set; }
    internal bool CanChange { get; private set; }

    internal bool Reload()
    {
        try { Folders = Read(); Error = null; CanChange = true; return true; }
        catch (Exception error) when (Expected(error))
        { CanChange = false; Error = "Saved app folders could not be read. Restore app-folders.json or retry when it is accessible. Existing shortcuts are retained; changes are paused. " + error.Message; return false; }
    }

    internal bool Change(string folder, bool add)
    {
        // A previous empty/default view must never overwrite unread durable data.
        if (!Reload()) return false;
        try
        {
            string normalized = AppDiscovery.NormalizeFolder(folder) ?? throw new ArgumentException("Choose a specific app folder.");
            string[] updated = Validate(add ? Folders.Append(normalized) : Folders.Where(value => value != normalized));
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, updated));
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Saved app folders exceed the size limit.");
            files.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = files.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(bytes);
                files.MoveFile(temporary, path, true);
            }
            finally
            {
                try { files.DeleteFile(temporary); }
                catch (Exception error) when (Expected(error)) { }
            }
            Folders = updated; Error = null; return true;
        }
        catch (Exception error) when (Expected(error))
        { Error = "Saved app folders were not changed. " + error.Message; return false; }
    }

    private string[] Read()
    {
        try
        {
            using var input = files.OpenRead(path);
            var bytes = new byte[MaximumBytes + 1]; int count = 0, read;
            while (count < bytes.Length && (read = input.Read(bytes.AsSpan(count))) != 0) count += read;
            if (count > MaximumBytes) throw new InvalidDataException("Saved app folders exceed the size limit.");
            var document = JsonSerializer.Deserialize<Document>(bytes.AsSpan(0, count));
            if (document is not { Version: 1, Folders: not null }) throw new InvalidDataException("Unsupported saved app folders format.");
            return Validate(document.Folders);
        }
        catch (FileNotFoundException) { return []; }
    }

    private static string[] Validate(IEnumerable<string> folders)
    {
        var result = new List<string>();
        foreach (string? folder in folders)
        {
            if (folder is null || folder.Length > 32767 || AppDiscovery.NormalizeFolder(folder) is not { } normalized)
                throw new InvalidDataException("A saved app folder is invalid.");
            if (!result.Contains(normalized, StringComparer.Ordinal)) result.Add(normalized);
            if (result.Count > MaximumFolders) throw new InvalidDataException("At most 64 added app folders are supported.");
        }
        return result.ToArray();
    }

    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private sealed record Document(int Version, string[] Folders);
}
