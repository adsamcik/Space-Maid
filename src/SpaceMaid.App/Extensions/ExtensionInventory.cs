using System.IO;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App.Extensions;

internal sealed class ExtensionInventory(Func<ScanSnapshot?> currentScan, Func<bool> isScanStale, Func<IDisposable> beginDiscovery) : IInventoryReader
{
    private const int BatchSize = 512;
    private readonly object _gate = new();
    private Guid _generation;
    private ScanSnapshot? _referencedScan;
    private Dictionary<int, string> _locations = [];

    public async IAsyncEnumerable<InventoryBatch> ReadAsync(string root, [EnumeratorCancellation] CancellationToken token, bool preferExistingInventory = false)
    {
        using var discovery = beginDiscovery();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        ValidateAncestors(root);
        var scan = currentScan();
        int? entry = preferExistingInventory ? scan?.FindEntry(root) : null;
        Guid generation = Guid.NewGuid();
        lock (_gate) { _generation = generation; _referencedScan = entry is not null ? scan : null; _locations.Clear(); }
        if (scan is not null && entry is { } index && scan.Entry(index).IsDirectory)
        {
            var coverage = new InventoryCoverage(root, scan.ScannedAtUtc, false, scan.HasAllocationData,
                scan.IncludesAllDataStreams, scan.HasCompleteVolumeLinks, scan.Warnings.Take(32).ToArray());
            bool complete = !isScanStale() && !scan.Entry(index).IsIncomplete && scan.WarningCount == 0;
            var warnings = coverage.Warnings.ToList();
            if (isScanStale()) warnings.Add("The available storage scan is out of date; refresh this collection to verify current metadata.");
            var pending = new Stack<IEnumerator<int>>();
            var visitedDirectories = new HashSet<int>();
            pending.Push(Enumerable.Repeat(index, 1).GetEnumerator());
            try
            {
                while (pending.Count > 0)
                {
                    var batch = await Task.Run(() =>
                    {
                        var items = new List<InventoryItem>(BatchSize);
                        while (pending.Count > 0 && items.Count < BatchSize)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!ReferenceEquals(currentScan(), scan)) throw new IOException("The storage scan changed during collection discovery. Refresh the collection again.");
                            var next = pending.Peek();
                            if (!next.MoveNext()) { next.Dispose(); pending.Pop(); continue; }
                            int link = next.Current; var value = scan.Entry(link); var metadata = scan.GetMetadata(link);
                            items.Add(new(new(generation, link), value.FileIndex, value.Name, value.IsDirectory,
                                value.LogicalBytes, value.AllocatedBytes, value.IsPrimaryName, metadata.Attributes,
                                metadata.LastWriteFileTime, metadata.NativeIdLow, metadata.NativeIdHigh));
                            if (value.IsDirectory)
                            {
                                if ((metadata.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                                {
                                    complete = false;
                                    if (warnings.Count < 32) warnings.Add("A reparse folder was left metadata-only and its contents were not followed.");
                                }
                                else if (visitedDirectories.Add(value.FileIndex)) pending.Push(scan.EnumerateChildren(value.FileIndex).GetEnumerator());
                                else { complete = false; if (warnings.Count < 32) warnings.Add("A repeated directory reference was not followed."); }
                            }
                        }
                        return items;
                    }, token).ConfigureAwait(false);
                    if (batch.Count > 0) yield return new(coverage, batch);
                }
            }
            finally { foreach (var iterator in pending) iterator.Dispose(); }
            yield return new(coverage with { Complete = complete, Warnings = warnings.ToArray() }, []);
            yield break;
        }

        // This fallback streams directory metadata directly: no content reads,
        // recursive reparse traversal, or second drive-sized snapshot.
        var observed = DateTime.UtcNow;
        var errors = new List<string>();
        bool finished = true;
        int sequence = 0;
        var folders = new Queue<string>(); folders.Enqueue(root);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        IEnumerator<LocalEntry>? active = null;
        try
        {
            while (folders.Count > 0 || active is not null)
            {
                var items = new List<InventoryItem>(BatchSize);
                var paths = new Dictionary<int, string>(BatchSize);
                await Task.Run(() =>
                {
                    while (items.Count < BatchSize && (folders.Count > 0 || active is not null))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (active is null)
                            {
                                string folder = folders.Dequeue(); ValidateAncestors(folder);
                                active = new FileSystemEnumerable<LocalEntry>(folder,
                                    (ref FileSystemEntry value) => new(value.ToFullPath(), value.FileName.ToString(), value.Attributes,
                                        value.IsDirectory ? 0 : value.Length, value.LastWriteTimeUtc.ToFileTime()), options).GetEnumerator();
                            }
                            if (!active.MoveNext()) { active.Dispose(); active = null; continue; }
                            var value = active.Current;
                            int handle = sequence++;
                            items.Add(new(new(generation, handle), handle, value.Name, (value.Attributes & FileAttributes.Directory) != 0,
                                value.Size, 0, true, (uint)value.Attributes, value.WriteTime, null, null));
                            paths[handle] = value.Path;
                            if ((value.Attributes & FileAttributes.Directory) != 0)
                            {
                                if ((value.Attributes & FileAttributes.ReparsePoint) == 0) folders.Enqueue(value.Path);
                                else RecordFailure("A reparse folder was left metadata-only and its contents were not followed.");
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            active?.Dispose(); active = null;
                            RecordFailure("A folder was unavailable or could not be read: " + ex.Message);
                        }
                    }
                }, token).ConfigureAwait(false);
                lock (_gate) { if (_generation != generation) throw new IOException("Another collection discovery replaced these inventory references."); _locations = paths; }
                if (items.Count > 0) yield return new(new(root, observed, false, false, false, false, errors.ToArray()), items);
            }
        }
        finally { active?.Dispose(); }
        yield return new(new(root, observed, finished, false, false, false, errors.ToArray()), []);
        void RecordFailure(string message) { finished = false; if (errors.Count < 32) errors.Add(message); }
    }

    public string? ResolveLocation(InventoryReference reference)
    {
        lock (_gate)
        {
            if (reference.Generation != _generation) return null;
            if (_referencedScan is { } scan)
                return ReferenceEquals(currentScan(), scan) ? scan.EntryPath(reference.EntryIndex) : null;
            return _locations.GetValueOrDefault(reference.EntryIndex);
        }
    }

    internal static void ValidateAncestors(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Choose an available local collection folder.");
        string? current = path;
        while (current is not null)
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.ReparsePoint | (FileAttributes)(0x1000 | 0x40000 | 0x400000))) != 0)
                throw new IOException("Reparse collection folders are metadata-only and are not followed. Choose a regular local collection folder.");
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
    }
    private readonly record struct LocalEntry(string Path, string Name, FileAttributes Attributes, long Size, long WriteTime);
}
