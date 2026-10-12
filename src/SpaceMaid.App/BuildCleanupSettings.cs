using System.IO;
using System.Text.Json;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

internal static class BuildCleanupSettings
{
    internal static BuildCleanupPolicy Load(string path)
    {
        FileStream stream;
        try { stream = File.OpenRead(path); }
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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { keepRecentBuildDays = policy.KeepRecentDays }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
