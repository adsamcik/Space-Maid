using System.Diagnostics;

namespace SpaceMaid.Core.Scanning;

public sealed record ScanRecheckRequest(string Path, bool Recursive = true);

public static class ScanRechecker
{
    internal sealed record Replacement(string Path, bool Recursive, ScanSnapshot Fresh);

    public static async Task<ScanSnapshot> RefreshAsync(ScanSnapshot scan, IEnumerable<ScanRecheckRequest> requests,
        CancellationToken token = default, Action<string, object>? diagnostic = null, INtfsLayoutSource? layoutSource = null)
    {
        if (!scan.HasAllocationData) throw new InvalidOperationException("This scan has no stable file identities. A full rescan is required.");
        var scanner = new VolumeScanner(layoutSource);
        var replacements = new List<Replacement>();
        var pending = new Queue<ScanRecheckRequest>(requests);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var request))
        {
            token.ThrowIfCancellationRequested();
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Path));
            string? parent = Path.GetDirectoryName(path);
            if (scan.FindEntry(path) is null && (parent is null || scan.FindEntry(parent) is null)
                && !replacements.Any(other => parent?.Equals(other.Path, StringComparison.Ordinal) == true))
                throw new InvalidOperationException("The recheck location is outside the known scan. A full rescan is required.");
            if (!visited.Add(path)) continue;
            ValidateAncestors(path);
            diagnostic?.Invoke("recheck-read-start", new { Path = path, request.Recursive });
            var fresh = await scanner.ReadDirectoryAsync(path, request.Recursive, token, scan.IncludesAllDataStreams,
                diagnostic: diagnostic, previous: scan);
            if (fresh.WarningCount > 0) throw new IOException("The recheck was incomplete: " + string.Join(" ", fresh.Warnings));
            replacements.Add(new(path, request.Recursive, fresh));
            diagnostic?.Invoke("recheck-read-complete", new { Path = path, fresh.FileCount, fresh.NameCount, fresh.Backend,
                fresh.FallbackReason, fresh.Metrics.Acquisition });
            if (!request.Recursive)
            {
                int? oldLink = scan.FindEntry(path);
                HashSet<FileIdentity> known = oldLink is { } link ? scan.Children(scan.Entry(link).FileIndex)
                    .Where(child => scan.Entry(child).IsDirectory).Select(child => scan.Records[scan.Entry(child).FileIndex].Identity).ToHashSet() : [];
                foreach (int child in fresh.Children(fresh.RootIndex))
                    if (fresh.Entry(child).IsDirectory && !known.Contains(fresh.Records[fresh.Entry(child).FileIndex].Identity))
                        pending.Enqueue(new(fresh.EntryPath(child)!, true));
            }
        }
        var timer = Stopwatch.StartNew();
        var merged = await Task.Run(() => Merge(scan, replacements, token), token);
        diagnostic?.Invoke("recheck-merge-complete", new { ElapsedMs = timer.Elapsed.TotalMilliseconds, merged.FileCount, merged.NameCount, merged.WarningCount });
        return merged;
    }

    internal static ScanSnapshot Merge(ScanSnapshot scan, IReadOnlyList<Replacement> replacements, CancellationToken token,
        IReadOnlyCollection<FileIdentity>? unmeasuredDirectoryIds = null)
    {
        var removed = new bool[scan.Links.Length];
        var traversed = new bool[scan.Links.Length];
        var updates = new Dictionary<FileIdentity, FileRecord>();
        var parentIds = new Dictionary<string, FileIdentity>(StringComparer.Ordinal);
        var newDirectoryIds = new HashSet<FileIdentity>();
        var directoryNames = new Dictionary<FileIdentity, (FileIdentity Parent, string Name)>();
        var freshRoots = new Dictionary<string, (FileIdentity Identity, ScanSnapshot Snapshot)>(StringComparer.Ordinal);
        bool retainedNtfsMetadata = scan.RetainsEarlierNtfsMetadata;
        foreach (var replacement in replacements)
        {
            token.ThrowIfCancellationRequested();
            var fresh = replacement.Fresh;
            if (!fresh.HasAllocationData || fresh.WarningCount > 0 || fresh.IncludesAllDataStreams != scan.IncludesAllDataStreams)
                throw new IOException("The recheck cannot preserve file accounting.");
            freshRoots[replacement.Path] = (fresh.Records[fresh.RootIndex].Identity, fresh);
            int? oldLink = scan.FindEntry(replacement.Path);
            if (oldLink is { } link)
            {
                var old = scan.Entry(link);
                if (!old.IsDirectory) throw new IOException("A recheck location is no longer a folder.");
                if (link == -1 && scan.Records[old.FileIndex].Identity != fresh.Records[fresh.RootIndex].Identity)
                    throw new IOException("The scanned root was replaced. A full rescan is required.");
                if (link >= 0) removed[link] = true;
                var liveIds = fresh.Children(fresh.RootIndex).Select(next => fresh.Records[fresh.Entry(next).FileIndex].Identity).ToHashSet();
                foreach (int child in scan.Children(old.FileIndex))
                {
                    var entry = scan.Entry(child);
                    var identity = scan.Records[entry.FileIndex].Identity;
                    // Directory enumeration omits reserved NTFS namespace
                    // objects. Absence cannot establish their deletion.
                    if (link == -1 && scan.RootPath.Length == 3 && scan.IncludesAllDataStreams
                        && identity.High == 0 && (identity.Low & 0x0000FFFFFFFFFFFF) < 16
                        && scan.Name(child).StartsWith("$", StringComparison.Ordinal) && !liveIds.Contains(identity))
                    { retainedNtfsMetadata = true; continue; }
                    bool stillPresent = !replacement.Recursive && entry.IsDirectory
                        && liveIds.Contains(scan.Records[entry.FileIndex].Identity);
                    if (replacement.Recursive || !stillPresent) RemoveBranch(child);
                    else removed[child] = true; // Keep this directory's cached descendants.
                }
            }
            foreach (var record in fresh.Records)
            {
                token.ThrowIfCancellationRequested();
                updates[record.Identity] = record;
                if (record.IsDirectory) newDirectoryIds.Add(record.Identity);
            }
            for (int i = 0; i < fresh.Links.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var name = fresh.Links[i]; var file = fresh.Records[name.FileIndex];
                if (!file.IsDirectory) continue;
                var observed = (name.ParentIdentity, new string(fresh.Name(i)));
                if (directoryNames.TryGetValue(file.Identity, out var previous) && previous != observed)
                    throw new IOException("A folder moved during the recheck. Recheck its parents again.");
                directoryNames[file.Identity] = observed;
            }
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(replacement.Path));
            if (parent is not null && !parentIds.ContainsKey(parent) && scan.FindEntry(parent) is { } value)
                parentIds[parent] = scan.Records[scan.Entry(value).FileIndex].Identity;
        }
        // Windows directories have a single location; remove cached old names
        // for directory identities observed at a new location by the recheck.
        for (int i = 0; i < scan.Links.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var record = scan.Records[scan.Links[i].FileIndex];
            if (record.IsDirectory && newDirectoryIds.Contains(record.Identity)) removed[i] = true;
        }
        var alive = new bool[scan.Records.Length];
        alive[scan.RootIndex] = true;
        for (int i = 0; i < scan.Links.Length; i++) if (!removed[i]) alive[scan.Links[i].FileIndex] = true;
        var builder = new ScanBuilder { AllStreams = scan.IncludesAllDataStreams, CompleteVolumeLinks = false, FallbackReason = scan.FallbackReason };
        builder.CarryCoverage(scan);
        foreach (var replacement in replacements)
        {
            token.ThrowIfCancellationRequested();
            builder.ClearUnmeasuredDirectory(replacement.Fresh.Records[replacement.Fresh.RootIndex].Identity);
            if (replacement.Recursive)
                foreach (var record in replacement.Fresh.Records)
                {
                    token.ThrowIfCancellationRequested();
                    if (record.IsDirectory) builder.ClearUnmeasuredDirectory(record.Identity);
                }
        }
        if (unmeasuredDirectoryIds is not null)
            foreach (var identity in unmeasuredDirectoryIds) { token.ThrowIfCancellationRequested(); builder.MarkUnmeasuredDirectory(identity); }
        for (int i = 0; i < scan.Records.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var record = scan.Records[i];
            if (!alive[i] && !updates.ContainsKey(record.Identity)) continue;
            if (updates.TryGetValue(record.Identity, out var update))
                record = update;
            int added = builder.AddFile(record);
            if (i == scan.RootIndex) builder.RootIndex = added;
        }
        for (int i = 0; i < scan.Links.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (removed[i]) continue;
            var link = scan.Links[i];
            builder.AddLink(builder.AddFile(scan.Records[link.FileIndex]), link.ParentIdentity, scan.Name(i));
        }
        var names = new HashSet<(FileIdentity Parent, string Name)>();
        foreach (var replacement in replacements)
        {
            token.ThrowIfCancellationRequested();
            var fresh = replacement.Fresh;
            foreach (var record in fresh.Records) { token.ThrowIfCancellationRequested(); builder.AddFile(record); }
            if (!replacement.Path.Equals(Path.TrimEndingDirectorySeparator(scan.RootPath), StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(replacement.Path))!;
                FileIdentity? observedParent = freshRoots.TryGetValue(parent, out var freshRoot) ? freshRoot.Identity : null;
                if (observedParent is null)
                    for (string? ancestor = Path.GetDirectoryName(parent); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                    {
                        token.ThrowIfCancellationRequested();
                        if (freshRoots.TryGetValue(ancestor, out var candidate) && candidate.Snapshot.FindEntry(parent) is { } parentLink)
                        { observedParent = candidate.Snapshot.Records[candidate.Snapshot.Entry(parentLink).FileIndex].Identity; break; }
                    }
                FileIdentity id = observedParent ?? (parentIds.TryGetValue(parent, out var parentId) ? parentId
                    : throw new IOException("The refreshed folder's parent is unresolved."));
                AddName(fresh.Records[fresh.RootIndex], id, Path.GetFileName(Path.TrimEndingDirectorySeparator(replacement.Path)));
            }
            for (int i = 0; i < fresh.Links.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var link = fresh.Links[i];
                AddName(fresh.Records[link.FileIndex], link.ParentIdentity, new string(fresh.Name(i)));
            }
        }
        var result = builder.Build(scan.RootPath, scan.Backend, scan.Metrics.Acquisition, GC.GetTotalAllocatedBytes(false), token);
        if (result.WarningCount > scan.WarningCount) throw new IOException("The recheck could not preserve namespace accounting. A full rescan is required.");
        return new(result.RootPath, result.Backend, result.Records, result.Links, result.NameArena, result.RootIndex,
            result.FirstLinks, result.ChildHeads, result.NextLinks, result.DirectoryLogical, result.DirectoryAllocated,
            scan.IncludesAllDataStreams, true, false, result.Warnings, scan.Metrics, result.EntryFlags, result.WarningCount, result.DirectoryCount, result.FallbackReason)
        { ScannedAtUtc = scan.ScannedAtUtc, LastRecheckUtc = DateTime.UtcNow,
            RecheckedPaths = replacements.Select(r => r.Path).ToArray(), RetainsEarlierNtfsMetadata = retainedNtfsMetadata,
            JournalScopes = VolumeScanner.MergeJournalScopes(scan, replacements) };

        void AddName(FileRecord file, FileIdentity parent, string name)
        {
            if (file.IsDirectory && directoryNames.TryGetValue(file.Identity, out var observed))
            { parent = observed.Parent; name = observed.Name; }
            if (names.Add((parent, name))) builder.AddLink(builder.AddFile(file), parent, name);
        }
        void RemoveBranch(int link)
        {
            var stack = new Stack<int>(); stack.Push(link);
            while (stack.TryPop(out int next))
            {
                token.ThrowIfCancellationRequested();
                if (traversed[next]) continue;
                traversed[next] = true;
                removed[next] = true;
                var entry = scan.Entry(next);
                if (entry.IsDirectory) foreach (int child in scan.Children(entry.FileIndex)) stack.Push(child);
            }
        }
    }

    internal static void ValidateAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (CloudFiles.HasUnsupportedLink(current)) throw new IOException("A linked recheck path cannot be followed.");
    }
}
