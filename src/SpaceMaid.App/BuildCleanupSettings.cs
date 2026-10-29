using SpaceMaid.Core.IO;
using System.IO;
using System.Text.Json;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

internal static class BuildCleanupSettings
{
    internal static BuildCleanupPolicy Load(string path)
    {
        FileStream stream;
        try { stream = DiskAccess.Default.OpenRead(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return BuildCleanupPolicy.Default; }
        using var input = stream;
        if (stream.Length > 4096) throw new InvalidDataException("Cleanup settings are too large.");
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("keepRecentBuildDays", out var value) || !value.TryGetInt32(out int days))
            throw new InvalidDataException("Cleanup settings are invalid.");
        return new(days);
    }

    internal static void Save(string path, BuildCleanupPolicy policy)
    {
        DiskAccess.Default.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            DiskAccess.Default.WriteAllText(temporary, JsonSerializer.Serialize(new { keepRecentBuildDays = policy.KeepRecentDays }));
            DiskAccess.Default.MoveFile(temporary, path, overwrite: true);
        }
        finally { if (DiskAccess.Default.FileExists(temporary)) DiskAccess.Default.DeleteFile(temporary); }
    }
}
