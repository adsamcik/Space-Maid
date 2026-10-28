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
    private readonly HashSet<FileIdentity> _unmeasuredDirectories = [];
    // Only interactive scans need the first-name index and bounded largest-file heap.
    private readonly List<int>? _firstNames = trackPreliminary ? [] : null;
    private readonly PriorityQueue<int, (long Bytes, int Tie)> _largest = new();
    private long _observedLogical, _observedAllocated;
    private bool _largestNeedsRefresh;

    public int Count => _files.Count;
    internal IReadOnlyList<FileRecord> Files => _files;
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

    internal void ReplaceMetadata(FileRecord file)
    {
        int index = _ids[file.Identity];
        var previous = _files[index];
        _files[index] = file;
        if (_firstNames is null) return;
        _observedLogical = checked(_observedLogical - previous.LogicalBytes + file.LogicalBytes);
        _observedAllocated = checked(_observedAllocated - previous.AllocatedBytes + file.AllocatedBytes);
        if (previous.LogicalBytes != file.LogicalBytes || previous.AllocatedBytes != file.AllocatedBytes)
            _largestNeedsRefresh = true;
    }
    internal void MarkUnmeasuredDirectory(FileIdentity identity) => _unmeasuredDirectories.Add(identity);
    internal void ClearUnmeasuredDirectory(FileIdentity identity) => _unmeasuredDirectories.Remove(identity);

    internal bool HasSameNamespace(ScanBuilder current, CancellationToken token)
    {
        if (RootIndex < 0 || current.RootIndex < 0 || _files[RootIndex].Identity != current._files[current.RootIndex].Identity
            || _files.Count != current._files.Count || _links.Count != current._links.Count || WarningCount != current.WarningCount) return false;
        for (int i = 0; i < current._files.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var file = current._files[i];
            if (!_ids.TryGetValue(file.Identity, out int index) || _files[index].IsDirectory != file.IsDirectory) return false;
        }
        // Most physical files have one name. Index their aliases without
        // materializing paths or depending on directory enumeration order.
        int[] heads = new int[_files.Count], next = new int[_links.Count];
        Array.Fill(heads, -1);
        for (int i = 0; i < _links.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            int file = _links[i].FileIndex;
            next[i] = heads[file]; heads[file] = i;
        }
        bool[] matched = new bool[_links.Count];
        for (int position = 0; position < current._links.Count; position++)
        {
            if ((position & 4095) == 0) token.ThrowIfCancellationRequested();
            var link = current._links[position];
            int file = _ids[current._files[link.FileIndex].Identity];
            bool found = false;
            for (int i = heads[file]; i >= 0; i = next[i])
            {
                var previous = _links[i];
                if (matched[i] || previous.ParentIdentity != link.ParentIdentity || previous.NameLength != link.NameLength
                    || !_names.AsSpan(previous.NameOffset, previous.NameLength)
                        .SequenceEqual(current._names.AsSpan(link.NameOffset, link.NameLength))) continue;
                matched[i] = found = true; break;
            }
            if (!found) return false;
        }
        return true;
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
        // Folder scans first enumerate names, then replace stream sizes in
        // batches. Rebuild the bounded ranking only when a preview is requested.
        if (_largestNeedsRefresh)
        {
            _largest.Clear();
            for (int i = 0; i < _files.Count; i++)
            {
                var file = _files[i];
                if (file.IsDirectory) continue;
                var priority = (AllocationData ? file.AllocatedBytes : file.LogicalBytes, -i);
                if (_largest.Count < 100) _largest.Enqueue(i, priority);
                else if (_largest.TryPeek(out _, out var smallest) && priority.CompareTo(smallest) > 0)
                    _largest.EnqueueDequeue(i, priority);
            }
            _largestNeedsRefresh = false;
        }
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

    internal void CarryCoverage(ScanSnapshot scan, IReadOnlySet<FileIdentity>? verified = null)
    {
        var unresolved = scan.UnreadableEntries.Where(id => verified?.Contains(id) != true).ToHashSet();
        for (int index = 0; unresolved.Count > 0 && index < scan.Records.Length; index++)
        {
            var id = scan.Records[index].Identity;
            if (!unresolved.Remove(id)) continue;
            string? path = scan.EntryPath(scan.PrimaryLink(index));
            Warn($"Earlier values retained for an unavailable location: {path ?? id.ToString()}", id);
        }
        // Namespace/accounting failures have no local identity that a focused
        // recheck can establish as resolved. Preserve that coverage separately.
        int unscoped = scan.WarningCount - scan.UnreadableEntries.Count;
        if (unscoped > 0)
        {
            _omissions += unscoped;
            _warnings.AddRange(scan.Warnings.Take(Math.Max(0, 8 - _warnings.Count)));
        }
        for (int i = 0; i < scan.Records.Length; i++)
        {
            if ((scan.EntryFlags[i] & 8) != 0) _unmeasuredDirectories.Add(scan.Records[i].Identity);
        }
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
        byte[] flags = new byte[files.Length]; // 1 shared, 2 omission, 4 unmeasured branch, 8 direct unmeasured directory, 16 contains shared names
        foreach (var id in _incomplete)
            if (_ids.TryGetValue(id, out int index)) flags[index] |= 2;
        foreach (var id in _unmeasuredDirectories)
            if (_ids.TryGetValue(id, out int index) && files[index].IsDirectory) flags[index] |= 12;
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
            flags[parent] |= (byte)(flags[link.FileIndex] & 2);
            if (first[link.FileIndex] < 0) first[link.FileIndex] = i;
            else
            {
                flags[link.FileIndex] |= 1;
                if (!files[link.FileIndex].IsDirectory)
                {
                    flags[parent] |= 16;
                    flags[links[first[link.FileIndex]].ParentIndex] |= 16;
                }
            }
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
            flags[parent] |= (byte)(flags[directory] & 22);
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
                process.PeakWorkingSet64), flags, _omissions, directoryCount, FallbackReason)
            { UnreadableEntries = _incomplete.ToHashSet() };
    }
}
