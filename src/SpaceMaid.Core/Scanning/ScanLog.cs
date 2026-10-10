using System.Text;
using System.Text.Json;

namespace SpaceMaid.Core.Scanning;

// One bounded local file per scan, readable while the scan is running.
public sealed class ScanLog : IDisposable
{
    public const long MaximumBytes = 10 * 1024 * 1024;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceMaid", "Logs");
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private long _bytes;
    private bool _finished;
    public string? FilePath { get; private set; }
    public string? Error { get; private set; }

    public ScanLog(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, $"scan-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.jsonl");
            _writer = new(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = file;
            foreach (string old in Directory.EnumerateFiles(directory, "scan-*.jsonl").OrderDescending(StringComparer.Ordinal).Skip(10))
            {
                try { File.Delete(old); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* An active log may still be open. */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Error = ex.Message; }
    }

    public void Record(string eventName, object data)
    {
        lock (_gate)
        {
            if (_writer is null || _finished) return;
            try
            {
                string line = JsonSerializer.Serialize(new { TimestampUtc = DateTime.UtcNow, Event = eventName, Data = data });
                int bytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                if (_bytes + bytes > MaximumBytes - 512)
                {
                    _writer.WriteLine(JsonSerializer.Serialize(new { TimestampUtc = DateTime.UtcNow, Event = "log-size-limit", Message = "10 MB limit reached. Further events are omitted; scanning continues." }));
                    _finished = true;
                    return;
                }
                _writer.WriteLine(line);
                _bytes += bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            { Error = ex.Message; _finished = true; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _finished = true;
            try { _writer?.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = ex.Message; }
            _writer = null;
        }
    }
}

public sealed record ScanDiagnostic(string Event, string Backend, string Message, long ElapsedMs,
    int FileObjects, int Names, int WarningCount, long QueryCount, long ParsedBatches, long ReturnedBytes,
    double NativeQueryMs, double ParseMs, long ProcessWorkingSetBytes, long ManagedHeapBytes,
    long ManagedAllocatedBytes, int? Win32Error = null)
{
    public double FileObjectsPerSecond => ElapsedMs > 0 ? 1000d * FileObjects / ElapsedMs : 0;
}
