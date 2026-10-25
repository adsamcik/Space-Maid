using System.IO;
using System.Text.Json;
using SpaceMaid.Core.Optimization;

namespace SpaceMaid.App;

internal sealed record OptimizationSettings(OptimizationPolicy Policy, DateTime? LastRunUtc = null, string? LastSummary = null, string? LastLog = null)
{
    internal static OptimizationSettings Load(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length > 128 * 1024) throw new InvalidDataException("Optimization settings are too large.");
            var settings = JsonSerializer.Deserialize<OptimizationSettings>(input) ?? throw new InvalidDataException("Invalid optimization settings.");
            if (settings.Policy is null) throw new InvalidDataException("Optimization policy is missing.");
            return settings with { Policy = settings.Policy.Validate() };
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(new OptimizationPolicy()); }
    }

    internal void Save(string path)
    {
        var policy = Policy.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this with { Policy = policy }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
