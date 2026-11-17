using System.IO;
using System.Text.Json;
using SpaceMaid.Core.IO;

namespace SpaceMaid.App;

// A remembered standard-scanning choice never grants elevation consent.
// This dedicated preference is write-once: unread data is never replaced.
internal sealed record StartupScanningPreference(bool Dismissed, string? Error = null)
{
    internal bool ShouldOffer => !Dismissed && Error is null;

    internal static StartupScanningPreference Load(string path, DiskAccess files)
    {
        try
        {
            using var input = files.OpenRead(path);
            if (input.Length > 4096) throw new InvalidDataException("Startup choice exceeds its size limit.");
            var saved = JsonSerializer.Deserialize<Document>(input);
            if (saved is not { Version: 1, StandardScanningChosen: true })
                throw new InvalidDataException("Startup choice has an unsupported format.");
            return new(true);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(false); }
        catch (Exception error) when (Expected(error)) { return new(false, error.Message); }
    }

    internal static StartupScanningPreference RememberStandard(string path, DiskAccess files)
    {
        var previous = Load(path, files);
        if (previous.Dismissed || previous.Error is not null) return previous;
        try
        {
            files.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // A concurrent choice or unreadable replacement cannot be overwritten.
            using var output = files.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(output, new Document(1, true));
            output.Flush(flushToDisk: true);
            return new(true);
        }
        catch (Exception error) when (Expected(error))
        {
            var current = Load(path, files);
            return current.Dismissed ? current : new(false, current.Error ?? error.Message);
        }
    }

    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private sealed record Document(int Version, bool StandardScanningChosen);
}
