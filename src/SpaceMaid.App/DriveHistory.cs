using System.IO;
using System.Text.Json;

namespace SpaceMaid.App;

internal sealed record DriveRecommendation(string Name, long Bytes, string? GroupKey = null);
internal sealed record DriveHistoryEntry(string VolumeId, string RootPath, string LastPath, DateTime LastUsedUtc,
    DateTime? ScannedAtUtc, string? ScanPath, bool HasAllocationData, bool PartialScan, DriveRecommendation[] Recommendations);

internal sealed class DriveHistoryStore(string filePath)
{
    private const int MaxVolumes = 24, MaxJsonBytes = 128 * 1024, MaxPathLength = 4096;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8 };
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<string, DriveHistoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    internal string? Error { get; private set; }
    internal string? LastVolumeId { get; private set; }

    internal async Task LoadAsync()
    {
        await _io.WaitAsync();
        try
        {
            var entries = new Dictionary<string, DriveHistoryEntry>(StringComparer.OrdinalIgnoreCase);
            string? lastVolume = null;
            if (File.Exists(filePath))
            {
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[MaxJsonBytes + 1];
                int count = 0, read;
                while (count < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(count))) != 0) count += read;
                if (count > MaxJsonBytes) throw new InvalidDataException("Drive history exceeds its size limit.");
                var document = JsonSerializer.Deserialize<HistoryDocument>(buffer.AsSpan(0, count), Json);
                if (document is not { Version: 1, Entries: not null })
                    throw new InvalidDataException("Drive history has an unsupported format.");
                foreach (var entry in document.Entries)
                {
                    if (entry is null || !ValidId(entry.VolumeId) || !TryRoot(entry.RootPath, out string root)
                        || !TryScopedPath(entry.LastPath, root, out string lastPath) || entry.LastUsedUtc == default
                        || entry.LastUsedUtc.Kind != DateTimeKind.Utc || entry.Recommendations is null
                        || entry.Recommendations.Any(value => value is null || !ValidName(value.Name) || !ValidGroupKey(value.GroupKey)))
                        throw new InvalidDataException("Drive history contains invalid metadata.");
                    string? scanPath = null;
                    if (entry.ScannedAtUtc.HasValue != (entry.ScanPath is not null)
                        || (entry.ScannedAtUtc is DateTime scanned && (scanned == default || scanned.Kind != DateTimeKind.Utc))
                        || (entry.ScanPath is not null && !TryScopedPath(entry.ScanPath, root, out scanPath))
                        || (entry.ScannedAtUtc is null && (entry.HasAllocationData || entry.PartialScan || entry.Recommendations.Length != 0)))
                        throw new InvalidDataException("Drive history contains an invalid scan path.");
                    if (!entries.TryAdd(entry.VolumeId, entry with
                        { RootPath = root, LastPath = lastPath, ScanPath = scanPath, Recommendations = TopRecommendations(entry.Recommendations) }))
                        throw new InvalidDataException("Drive history contains duplicate volumes.");
                }
                lastVolume = document.LastVolumeId;
                if (lastVolume is not null && (!ValidId(lastVolume) || !entries.ContainsKey(lastVolume)))
                    throw new InvalidDataException("Drive history contains an invalid last volume.");
                entries = MostRecent(entries.Values);
                if (lastVolume is not null && !entries.ContainsKey(lastVolume)) lastVolume = null;
            }
            lock (_sync) { _entries = entries; LastVolumeId = lastVolume; Error = null; }
        }
        catch (Exception ex) when (IsHistoryError(ex))
        {
            lock (_sync) { _entries.Clear(); LastVolumeId = null; Error = "Drive history could not be loaded: " + ex.Message; }
        }
        finally { _io.Release(); }
    }

    internal DriveHistoryEntry? Find(string volumeId, string currentRoot)
    {
        if (!ValidId(volumeId) || !TryRoot(currentRoot, out string root)) return null;
        lock (_sync)
        {
            if (!_entries.TryGetValue(volumeId, out var entry)) return null;
            string? lastPath = Rebase(entry.LastPath, entry.RootPath, root);
            string? scanPath = entry.ScanPath is null ? null : Rebase(entry.ScanPath, entry.RootPath, root);
            if (lastPath is null || (entry.ScanPath is not null && scanPath is null)) return null;
            return entry with { RootPath = root, LastPath = lastPath, ScanPath = scanPath, Recommendations = [.. entry.Recommendations] };
        }
    }

    internal void RememberSelection(string volumeId, string root, string path)
    {
        if (!ValidId(volumeId) || !TryRoot(root, out string normalizedRoot)
            || !TryScopedPath(path, normalizedRoot, out string normalizedPath)) return;
        lock (_sync)
        {
            var prior = Find(volumeId, normalizedRoot);
            _entries[volumeId] = prior is null
                ? new(volumeId, normalizedRoot, normalizedPath, DateTime.UtcNow, null, null, false, false, [])
                : prior with { LastPath = normalizedPath, LastUsedUtc = DateTime.UtcNow };
            _entries = MostRecent(_entries.Values);
            LastVolumeId = volumeId;
        }
    }

    internal void RecordScan(string volumeId, string root, string scanPath, DateTime scannedAtUtc,
        bool hasAllocationData, bool partialScan, IEnumerable<DriveRecommendation> recommendations)
    {
        if (!ValidId(volumeId) || !TryRoot(root, out string normalizedRoot)
            || !TryScopedPath(scanPath, normalizedRoot, out string normalizedPath) || scannedAtUtc == default) return;
        var top = TopRecommendations(recommendations);
        lock (_sync)
        {
            var prior = Find(volumeId, normalizedRoot);
            var timestamp = scannedAtUtc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(scannedAtUtc, DateTimeKind.Utc)
                : scannedAtUtc.ToUniversalTime();
            _entries[volumeId] = new(volumeId, normalizedRoot, prior?.LastPath ?? normalizedPath, DateTime.UtcNow,
                timestamp, normalizedPath, hasAllocationData, partialScan, top);
            _entries = MostRecent(_entries.Values);
            LastVolumeId = volumeId;
        }
    }

    internal async Task SaveAsync()
    {
        await _io.WaitAsync();
        string? temporary = null;
        try
        {
            DriveHistoryEntry[] entries;
            string? lastVolume;
            lock (_sync)
            {
                entries = _entries.Values.OrderByDescending(value => value.LastUsedUtc).Take(MaxVolumes).ToArray();
                lastVolume = LastVolumeId;
            }
            byte[] bytes;
            // Extremely long Unicode paths may take more JSON bytes than characters. Evict oldest hints first.
            while (true)
            {
                if (!entries.Any(value => value.VolumeId.Equals(lastVolume, StringComparison.OrdinalIgnoreCase))) lastVolume = null;
                bytes = JsonSerializer.SerializeToUtf8Bytes(new HistoryDocument(1, lastVolume, entries), Json);
                if (bytes.Length <= MaxJsonBytes) break;
                entries = entries[..^1];
            }
            string destination = Path.GetFullPath(filePath), directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Join(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
            lock (_sync) Error = null;
        }
        catch (Exception ex) when (IsHistoryError(ex))
        { lock (_sync) Error = "Drive history could not be saved: " + ex.Message; }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch (Exception ex) when (IsHistoryError(ex)) { } }
            _io.Release();
        }
    }

    private static Dictionary<string, DriveHistoryEntry> MostRecent(IEnumerable<DriveHistoryEntry> entries)
        => entries.OrderByDescending(value => value.LastUsedUtc).Take(MaxVolumes)
            .ToDictionary(value => value.VolumeId, StringComparer.OrdinalIgnoreCase);

    private static DriveRecommendation[] TopRecommendations(IEnumerable<DriveRecommendation> recommendations)
        => recommendations.Where(value => value is not null && ValidName(value.Name) && ValidGroupKey(value.GroupKey) && value.Bytes > 0)
            .OrderByDescending(value => value.Bytes).Take(3).ToArray();

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && !value.Any(char.IsControl);
    private static bool ValidGroupKey(string? value) => value is null
        || value.StartsWith("cleanup:", StringComparison.Ordinal) && value.Length <= 80 && !value.Any(char.IsControl);

    private static bool TryRoot(string? input, out string root)
    {
        root = "";
        if (!TryFullPath(input, out string path)) return false;
        string? actualRoot = Path.GetPathRoot(path);
        if (actualRoot is null || !path.Equals(Path.TrimEndingDirectorySeparator(actualRoot), StringComparison.OrdinalIgnoreCase)) return false;
        root = actualRoot;
        return true;
    }

    private static bool TryScopedPath(string? input, string root, out string path)
    {
        if (!TryFullPath(input, out path)) return false;
        return path.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFullPath(string? input, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxPathLength || !Path.IsPathFullyQualified(input) || input.Any(char.IsControl)) return false;
        try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input)); return path.Length <= MaxPathLength; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string? Rebase(string path, string oldRoot, string root)
    {
        if (!TryScopedPath(path, oldRoot, out string normalized)) return null;
        string rebased = Path.Join(root, Path.GetRelativePath(oldRoot, normalized));
        return TryScopedPath(rebased, root, out string result) ? result : null;
    }

    private static bool IsHistoryError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException
        or InvalidDataException or ArgumentException or NotSupportedException;

    private sealed record HistoryDocument(int Version, string? LastVolumeId, DriveHistoryEntry[] Entries);
}
