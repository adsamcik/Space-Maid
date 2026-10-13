using System.IO;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App.Extensions;

internal sealed class ExtensionInventory(Func<ScanSnapshot?> currentScan, Func<bool> isScanStale, Func<IDisposable> beginDiscovery,
    CancellationToken lifetime = default, FileOperations? operations = null,
    Action<string, object>? diagnostic = null) : IInventoryReader, IInventoryFolderSizes, IInventoryEvidence
{
    private const int BatchSize = 512;
    private readonly FileOperations _operations = operations ?? new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ReferenceBatch> _readers = [];
    private sealed record ReferenceBatch(ScanSnapshot? Scan, Dictionary<int, string?> Locations, CancellationToken Cancellation);

    public Task<IInventoryEvidenceSession> OpenEvidenceSessionAsync(string root, IReadOnlyList<string> excludedPaths,
        CancellationToken token) => Task.Run<IInventoryEvidenceSession>(() =>
    {
        token.ThrowIfCancellationRequested(); lifetime.ThrowIfCancellationRequested();
        return new InventoryEvidenceSession(currentScan, isScanStale, root, excludedPaths, lifetime, this);
    }, token);

    public Task<InventoryFolderSize?> ReadFolderSizeAsync(string path, bool refresh, CancellationToken token) => Task.Run(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        token = linked.Token;
        token.ThrowIfCancellationRequested();
        ValidateAncestors(path);
        if (!refresh)
        {
            var scan = currentScan();
            if (scan is null || isScanStale() || scan.FindEntry(path) is not { } index) return null;
            var entry = scan.Entry(index);
            if (!entry.IsDirectory || scan.IsUnmeasuredDirectory(entry.FileIndex) || scan.ContainsSharedFileNames(entry.FileIndex)) return null;
            bool complete = !entry.IsIncomplete && scan.WarningCount == 0;
            var summary = new InventoryFolderSize(entry.LogicalBytes, scan.HasAllocationData ? entry.AllocatedBytes : null,
                complete, scan.ScannedAtUtc, "Drive scan", SizeWarnings(scan));
            // A replaced/stale scan must not publish apparently current sizes.
            return ReferenceEquals(currentScan(), scan) && !isScanStale() ? summary : null;
        }
        using var discovery = beginDiscovery();
        var measured = await _operations.MeasureFolderAsync(path, token, diagnostic).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new InventoryFolderSize(measured.LogicalBytes, measured.HasAllocationData ? measured.AllocatedBytes : null,
            !measured.RootEntry.IsIncomplete && measured.WarningCount == 0, measured.ScannedAtUtc, measured.Backend, SizeWarnings(measured));
    }, token);

    private static IReadOnlyList<string> SizeWarnings(ScanSnapshot scan)
    {
        var warnings = scan.Warnings.Take(30).ToList();
        if (!scan.IncludesAllDataStreams) warnings.Add("Named DATA streams are outside this measurement.");
        if (!scan.HasCompleteVolumeLinks) warnings.Add("Files shared with locations outside this folder may also contribute to other apps' sizes.");
        return warnings;
    }

    public async IAsyncEnumerable<InventoryBatch> ReadAsync(string root, [EnumeratorCancellation] CancellationToken token, bool preferExistingInventory = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        token = linked.Token;
        token.ThrowIfCancellationRequested();
        using var discovery = beginDiscovery();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        ValidateAncestors(root);
        var scan = currentScan();
        int? entry = preferExistingInventory ? scan?.FindEntry(root) : null;
        Guid generation = Guid.NewGuid();
        // One bounded set per active reader. Moving to another batch expires
        // only this reader's handles; another extension cannot replace them.
        lock (_gate)
        {
            if (_readers.Count >= 16) throw new InvalidOperationException("Too many active inventory readers. Finish an existing discovery first.");
            _readers.Add(generation, new(null, [], token));
        }
        try
        {
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
                        Publish(scan, []);
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
                        Publish(scan, batch.ToDictionary(item => item.Reference.EntryIndex, _ => (string?)null));
                        if (batch.Count > 0) yield return new(coverage, batch);
                    }
                }
                finally { foreach (var iterator in pending) iterator.Dispose(); }
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(currentScan(), scan)) throw new IOException("The storage scan changed during collection discovery. Refresh the collection again.");
                complete &= !isScanStale();
                Publish(scan, []);
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
                    Publish(null, []);
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
                    Publish(null, paths.ToDictionary(pair => pair.Key, pair => (string?)pair.Value));
                    if (items.Count > 0) yield return new(new(root, observed, false, false, false, false, errors.ToArray()), items);
                }
            }
            finally { active?.Dispose(); }
            token.ThrowIfCancellationRequested();
            Publish(null, []);
            yield return new(new(root, observed, finished, false, false, false, errors.ToArray()), []);
            void RecordFailure(string message) { finished = false; if (errors.Count < 32) errors.Add(message); }
            void Publish(ScanSnapshot? source, Dictionary<int, string?> locations)
            { lock (_gate) _readers[generation] = new(source, locations, token); }
        }
        finally { lock (_gate) _readers.Remove(generation); }
    }

    public string? ResolveLocation(InventoryReference reference)
    {
        lock (_gate)
        {
            if (!_readers.TryGetValue(reference.Generation, out var batch) || batch.Cancellation.IsCancellationRequested
                || !batch.Locations.TryGetValue(reference.EntryIndex, out var location)) return null;
            if (batch.Scan is { } scan)
                return ReferenceEquals(currentScan(), scan) ? scan.EntryPath(reference.EntryIndex) : null;
            return location;
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
