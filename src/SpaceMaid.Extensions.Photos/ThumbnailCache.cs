using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Photos;

internal sealed record PhotoThumbnail(BitmapSource? Image, string Status);
internal sealed class ThumbnailCache(IExtensionHost host)
{
    internal const long MemoryLimit = 64L * 1024 * 1024;
    internal const long DiskLimit = 512L * 1024 * 1024;
    internal const int Dimensions = 160;
    private readonly object _gate = new();
    private readonly Dictionary<string, (BitmapSource Image, long Bytes, long Use)> _memory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<PhotoThumbnail>> _pending = new(StringComparer.Ordinal);
    private long _memoryBytes, _clock;
    private long? _diskBytes;
    private bool _diskWritesDisabled;
    private readonly string _directory = Path.Join(host.CacheDirectory, "thumbnails-v1");

    internal long MemoryBytes { get { lock (_gate) return _memoryBytes; } }
    public Task<PhotoThumbnail> GetAsync(PhotoRow row, CancellationToken token)
    {
        if (!row.ContentAvailable) return Task.FromResult(new PhotoThumbnail(null, "Unavailable · Offline/reparse file"));
        if (!row.AnalysisCurrent) return Task.FromResult(new PhotoThumbnail(null, "Analyze to enable previews"));
        var key = Key(row);
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var pending)) return pending.WaitAsync(token);
            if (_pending.Count >= 128) return Task.FromResult(new PhotoThumbnail(null, "Preview queue full · Retry on selection"));
            var task = LoadAsync(row, key, token); _pending[key] = task;
            _ = task.ContinueWith(_ => { lock (_gate) _pending.Remove(key); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
    private async Task<PhotoThumbnail> LoadAsync(PhotoRow row, string key, CancellationToken token)
    {
        PhotoThumbnail? result = null;
        try
        {
            await host.Jobs.RunAsync("photos-thumbnail", JobResource.Thumbnail, async context =>
            {
                await context.CheckpointAsync(); context.Cancellation.ThrowIfCancellationRequested();
                using var metadata = NativeMediaFile.Open(row.Path, false);
                if (!metadata.Revision.ContentAvailable) throw new IOException("Cloud-only, offline and reparse files remain metadata-only.");
                if (metadata.Revision.Identity != row.ObjectId || metadata.Revision.Key != row.Revision)
                    throw new IOException("File changed · Refresh and Analyze to enable preview");
                using var source = NativeMediaFile.Open(row.Path, true);
                if (source.Revision.Key != row.Revision) throw new IOException("File changed · Refresh this collection");
                // WIC extensions can update independently of Windows or WPF.
                // Header inspection is in the approved content lane; cached
                // previews avoid pixel decoding but still validate this codec.
                var codec = BitmapDecoder.Create(source.Stream!, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).CodecInfo;
                key = DecoderKey(key, codec.ContainerFormat, codec.Version.ToString());
                source.VerifyUnchanged();
                lock (_gate)
                {
                    if (_memory.TryGetValue(key, out var cached))
                    {
                        _memory[key] = (cached.Image, cached.Bytes, ++_clock);
                        result = new(cached.Image, "Cached preview · Edits may differ"); return;
                    }
                }
                Directory.CreateDirectory(_directory);
                var cachePath = Path.Join(_directory, key + ".png");
                BitmapSource? bitmap = null; var diskHit = false;
                if (File.Exists(cachePath))
                {
                    try
                    {
                        using var cached = File.OpenRead(cachePath); bitmap = Decode(cached); diskHit = true;
                        File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
                    }
                    catch (Exception error) when (error is IOException or FormatException or NotSupportedException or System.Runtime.InteropServices.COMException)
                    { File.Delete(cachePath); }
                }
                if (bitmap is null)
                {
                    await context.CheckpointAsync();
                    source.Stream!.Position = 0;
                    bitmap = Orient(Decode(source.Stream!), row.Orientation); source.VerifyUnchanged();
                    context.Cancellation.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        InitializeDisk();
                        if (!_diskWritesDisabled)
                        {
                        var temporary = Path.Join(_directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output);
                            }
                            var priorBytes = File.Exists(cachePath) ? new FileInfo(cachePath).Length : 0;
                            var newBytes = new FileInfo(temporary).Length;
                            File.Move(temporary, cachePath, true); _diskBytes += newBytes - priorBytes;
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        { _diskWritesDisabled = true; host.Log("photos-thumbnail-cache-write-failed", new { Error = error.GetType().Name }); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        try { EvictDisk(); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        { _diskWritesDisabled = true; host.Log("photos-thumbnail-cache-eviction-failed", new { Error = error.GetType().Name }); }
                        }
                    }
                }
                metadata.VerifyUnchanged(); AddMemory(key, bitmap);
                result = new(bitmap, "Preview · Embedded RAW previews may not reflect current edits");
                host.Log("photos-thumbnail", new { Cache = diskHit ? "disk" : "decoded", Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, MemoryBytes });
            }, null, token);
            return result ?? new(null, "Preview unavailable");
        }
        catch (OperationCanceledException) { return new(null, "Preview cancelled"); }
        catch (InvalidOperationException)
        { return new(null, "Preview queue unavailable · Retry on selection"); }
        catch (Exception error) when (error is IOException or FormatException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            host.Log("photos-thumbnail-unavailable", new { Error = error.GetType().Name });
            return new(null, error is IOException && (error.Message.StartsWith("File changed", StringComparison.Ordinal) || error.Message.StartsWith("Cloud-only", StringComparison.Ordinal)) ? error.Message : "Preview unavailable · Codec missing or file unreadable");
        }
    }
    internal static string Key(PhotoRow row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{row.ObjectId}|{row.Revision}|{row.SidecarRevision}|orientation:{row.Orientation}|160|fit|WIC:{Environment.OSVersion.Version}:{typeof(BitmapDecoder).Assembly.GetName().Version}|recipe1")));
    internal static string DecoderKey(string sourceKey, Guid format, string version) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceKey}|{format:D}|{version}")));
    private static BitmapSource Orient(BitmapSource bitmap, int? orientation)
    {
        System.Windows.Media.Matrix? matrix = orientation switch
        {
            2 => new(-1, 0, 0, 1, 0, 0), 3 => new(-1, 0, 0, -1, 0, 0), 4 => new(1, 0, 0, -1, 0, 0),
            5 => new(0, 1, 1, 0, 0, 0), 6 => new(0, 1, -1, 0, 0, 0), 7 => new(0, -1, -1, 0, 0, 0), 8 => new(0, -1, 1, 0, 0, 0), _ => null
        };
        if (matrix is null) return bitmap;
        var result = new TransformedBitmap(bitmap, new System.Windows.Media.MatrixTransform(matrix.Value)); result.Freeze(); return result;
    }
    internal static BitmapSource Decode(Stream stream)
    {
        // Dimension inspection does not retain a full-resolution decoded copy.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 100000 || frame.PixelHeight > 100000)
            throw new NotSupportedException("Image dimensions are unsupported.");
        var scale = Math.Min(1.0, Dimensions / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
        var width = Math.Max(1, (int)(frame.PixelWidth * scale)); var height = Math.Max(1, (int)(frame.PixelHeight * scale));
        stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat; bitmap.DecodePixelWidth = width; bitmap.DecodePixelHeight = height;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    private void AddMemory(string key, BitmapSource bitmap)
    {
        var bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(4, (bitmap.Format.BitsPerPixel + 7) / 8);
        lock (_gate)
        {
            if (_memory.Remove(key, out var prior)) _memoryBytes -= prior.Bytes;
            while (_memoryBytes + bytes > MemoryLimit && _memory.Count > 0)
            {
                var oldest = _memory.MinBy(x => x.Value.Use); _memory.Remove(oldest.Key); _memoryBytes -= oldest.Value.Bytes;
            }
            if (bytes <= MemoryLimit) { _memory[key] = (bitmap, bytes, ++_clock); _memoryBytes += bytes; }
        }
    }
    private void EvictDisk()
    {
        // Bound the eviction working set even if a prior cache contains many tiny files.
        if (_diskBytes <= DiskLimit) return;
        var target = DiskLimit * 9 / 10;
        while (_diskBytes > target)
        {
            var oldest = new PriorityQueue<FileInfo, DateTime>(Comparer<DateTime>.Create((a, b) => b.CompareTo(a)));
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.png"))
            {
                oldest.Enqueue(file, file.LastWriteTimeUtc); if (oldest.Count > 128) oldest.Dequeue();
            }
            if (oldest.Count == 0) break;
            foreach (var item in oldest.UnorderedItems.OrderBy(x => x.Priority))
            {
                if (_diskBytes <= target) break;
                var size = item.Element.Length; item.Element.Delete(); _diskBytes -= size;
            }
        }
    }
    private void InitializeDisk()
    {
        if (_diskBytes is not null) return;
        _diskBytes = 0;
        try
        {
            foreach (var temporary in new DirectoryInfo(_directory).EnumerateFiles("*.tmp")) temporary.Delete();
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.png")) _diskBytes += file.Length;
            EvictDisk();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _diskWritesDisabled = true; host.Log("photos-thumbnail-cache-unavailable", new { Error = error.GetType().Name }); }
    }
}
