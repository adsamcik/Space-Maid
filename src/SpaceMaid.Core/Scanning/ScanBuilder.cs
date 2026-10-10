using System.Diagnostics;

namespace SpaceMaid.Core.Scanning;

internal sealed class ScanBuilder(bool trackPreliminary = false)
{
    private readonly List<FileRecord> _files = [];
    private readonly List<NameLink> _links = [];
    private readonly Dictionary<FileIdentity, int> _ids = [];
    private char[] _names = new char[64 * 1024];
    private int _nameCount;
    private int _omissions;
    private readonly List<string> _warnings = [];
    private readonly HashSet<FileIdentity> _incomplete = [];
    // Only interactive scans need the first-name index and bounded largest-file heap.
    private readonly List<int>? _firstNames = trackPreliminary ? [] : null;
    private readonly PriorityQueue<int, (long Bytes, int Tie)> _largest = new();
    private long _observedLogical, _observedAllocated;

    public int Count => _files.Count;
    public int NameCount => _links.Count;
    public int WarningCount => _omissions;
    public Action<string>? WarningReported { get; set; }
    public int RootIndex { get; set; } = -1;
    public bool AllStreams { get; set; }
    public bool AllocationData { get; set; } = true;
    public bool CompleteVolumeLinks { get; set; }
    public string? FallbackReason { get; set; }

    public int AddFile(FileRecord file)
    {
        if (_ids.TryGetValue(file.Identity, out int existing)) return existing;
        int index = _files.Count;
        _ids.Add(file.Identity, index);
        _files.Add(file);
        if (_firstNames is not null)
        {
            _firstNames.Add(-1);
            _observedLogical = checked(_observedLogical + file.LogicalBytes);
            _observedAllocated = checked(_observedAllocated + file.AllocatedBytes);
            if (!file.IsDirectory)
            {
                var priority = (AllocationData ? file.AllocatedBytes : file.LogicalBytes, -index);
                if (_largest.Count < 100) _largest.Enqueue(index, priority);
                else if (_largest.TryPeek(out _, out var smallest) && priority.CompareTo(smallest) > 0)
                    _largest.EnqueueDequeue(index, priority);
            }
        }
        return index;
    }

    public void AddLink(int file, FileIdentity parent, ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name.SequenceEqual(".") || name.SequenceEqual("..")) return;
        if (name.ContainsAny('\\', '/', '\0'))
            throw new InvalidDataException("A metadata record contains an invalid basename.");
        int needed = checked(_nameCount + name.Length);
        if (needed > _names.Length)
            Array.Resize(ref _names, Math.Max(needed, checked(_names.Length * 2)));
        name.CopyTo(_names.AsSpan(_nameCount));
        if (_firstNames is not null && _firstNames[file] < 0) _firstNames[file] = _links.Count;
        _links.Add(new(file, parent, _nameCount, name.Length));
        _nameCount = needed;
    }

    public ScanPreliminary Preliminary(string rootPath)
    {
        var largest = _largest.UnorderedItems.OrderByDescending(item => item.Priority).Select(item =>
        {
            var file = _files[item.Element];
            int link = _firstNames![item.Element];
            string name = link < 0 ? "Name pending" : LinkName(link);
            return new PreliminaryFile(name, PreviewPath(item.Element, rootPath), file.LogicalBytes, file.AllocatedBytes);
        }).ToArray();
        return new(_observedLogical, _observedAllocated, AllocationData, Array.AsReadOnly(largest));
    }

    private string LinkName(int index)
    {
        var link = _links[index];
        return new string(_names, link.NameOffset, link.NameLength);
    }

    private string? PreviewPath(int file, string rootPath)
    {
        // Parents can arrive in later NTFS batches. Do not emit coverage warnings
        // or invent a rooted path from an unresolved/cyclic namespace.
        var parts = new List<string>();
        var visited = new HashSet<int>();
        while (file != RootIndex)
        {
            if (!visited.Add(file) || _firstNames![file] < 0) return null;
            var link = _links[_firstNames[file]];
            parts.Add(LinkName(_firstNames[file]));
            if (!_ids.TryGetValue(link.ParentIdentity, out file) || !_files[file].IsDirectory) return null;
        }
        parts.Reverse();
        return Path.Join(rootPath, Path.Combine(parts.ToArray()));
    }

    public void Warn(string message, FileIdentity? directory = null)
    {
        _omissions++;
        CompleteVolumeLinks = false;
        if (_warnings.Count < 8) _warnings.Add(message);
        if (directory is { } id) _incomplete.Add(id);
        WarningReported?.Invoke(message);
    }

    public ScanSnapshot Build(string rootPath, string backend, TimeSpan acquisition,
        long allocatedBefore, CancellationToken cancellationToken)
    {
        if (RootIndex < 0) throw new InvalidDataException("The scan did not return its root directory.");
        var timer = Stopwatch.StartNew();
        var files = _files.ToArray();
        var links = _links.ToArray();
        int[] first = Enumerable.Repeat(-1, files.Length).ToArray();
        int[] heads = Enumerable.Repeat(-1, files.Length).ToArray();
        int[] next = Enumerable.Repeat(-1, links.Length).ToArray();
        byte[] flags = new byte[files.Length]; // shared-name and incomplete-branch bits
        foreach (var id in _incomplete)
            if (_ids.TryGetValue(id, out int index)) flags[index] |= 2;
        int directoryCount = 0;
        for (int i = 0; i < links.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var link = links[i];
            if (link.FileIndex == RootIndex) continue;
            if (!_ids.TryGetValue(link.ParentIdentity, out int parent) || !files[parent].IsDirectory)
            {
                Warn("Some names could not be joined to their parent directories; results are incomplete.");
                continue;
            }
            links[i] = link with { ParentIndex = parent };
            next[i] = heads[parent];
            heads[parent] = i;
            if (first[link.FileIndex] < 0) first[link.FileIndex] = i;
            else flags[link.FileIndex] |= 1;
        }

        // Attribute each file's allocation once, even when it has several names.
        long[] logical = new long[files.Length];
        long[] allocated = new long[files.Length];
        int[] pending = new int[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            logical[i] = files[i].LogicalBytes;
            allocated[i] = files[i].AllocatedBytes;
            if (files[i].IsDirectory) directoryCount++;
            if (i != RootIndex && files[i].IsDirectory && first[i] >= 0)
                pending[links[first[i]].ParentIndex]++;
        }
        var ready = new Queue<int>();
        for (int i = 0; i < files.Length; i++)
        {
            if (files[i].IsDirectory)
            {
                if (pending[i] == 0) ready.Enqueue(i);
            }
            else if (first[i] >= 0)
            {
                int parent = links[first[i]].ParentIndex;
                logical[parent] = checked(logical[parent] + logical[i]);
                allocated[parent] = checked(allocated[parent] + allocated[i]);
            }
        }
        while (ready.TryDequeue(out int directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory == RootIndex || first[directory] < 0) continue;
            int parent = links[first[directory]].ParentIndex;
            logical[parent] = checked(logical[parent] + logical[directory]);
            allocated[parent] = checked(allocated[parent] + allocated[directory]);
            flags[parent] |= (byte)(flags[directory] & 2);
            if (--pending[parent] == 0) ready.Enqueue(parent);
        }
        if (pending.Any(p => p > 0))
        {
            Warn("Directory relationships changed or contain a cycle; some totals are incomplete.");
            for (int i = 0; i < pending.Length; i++) if (pending[i] > 0) flags[i] |= 2;
        }
        if (_omissions > 0) flags[RootIndex] |= 2;
        if (_omissions > _warnings.Count) _warnings.Add($"{_omissions:N0} coverage warnings in total.");
        timer.Stop();
        using var process = Process.GetCurrentProcess();
        return new(rootPath, backend, files, links, _names, RootIndex, first, heads, next,
            logical, allocated, AllStreams, AllocationData, CompleteVolumeLinks,
            _warnings.ToArray(), new(acquisition, timer.Elapsed, GC.GetTotalAllocatedBytes(false) - allocatedBefore,
                process.PeakWorkingSet64), flags, _omissions, directoryCount, FallbackReason);
    }
}
