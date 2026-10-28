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
            diagnostic?.Invoke("recheck-read-start", new { Path = path, request.Recursive });
            var fresh = await scanner.ReadDirectoryAsync(path, request.Recursive, token, scan.IncludesAllDataStreams,
                diagnostic: diagnostic, previous: scan);
            replacements.Add(new(path, request.Recursive, fresh));
            diagnostic?.Invoke("recheck-read-complete", new { Path = path, fresh.FileCount, fresh.NameCount, fresh.Backend,
                fresh.FallbackReason, fresh.Metrics.Acquisition, fresh.WarningCount, fresh.Warnings });
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
        var merged = await Task.Run(() => Merge(scan, replacements, token, allowIncomplete: true), token);
        diagnostic?.Invoke("recheck-merge-complete", new { ElapsedMs = timer.Elapsed.TotalMilliseconds, merged.FileCount, merged.NameCount, merged.WarningCount });
        return merged;
    }

    internal static ScanSnapshot Merge(ScanSnapshot scan, IReadOnlyList<Replacement> replacements, CancellationToken token,
        IReadOnlyCollection<FileIdentity>? unmeasuredDirectoryIds = null, bool allowIncomplete = false)
    {
        var removed = new bool[scan.Links.Length];
        var traversed = new bool[scan.Links.Length];
        var updates = new Dictionary<FileIdentity, FileRecord>();
        var parentIds = new Dictionary<string, FileIdentity>(StringComparer.Ordinal);
        var newDirectoryIds = new HashSet<FileIdentity>();
        var directoryNames = new Dictionary<FileIdentity, (FileIdentity Parent, string Name)>();
        var freshRoots = new Dictionary<string, (FileIdentity Identity, ScanSnapshot Snapshot)>(StringComparer.Ordinal);
        var unreadable = replacements.SelectMany(replacement => replacement.Fresh.UnreadableEntries).ToHashSet();
        var oldFiles = new Dictionary<FileIdentity, int>();
        var needed = replacements.SelectMany(replacement => replacement.Fresh.Records
            .Where(record => replacement.Recursive && record.IsDirectory || unreadable.Contains(record.Identity)))
            .Select(record => record.Identity).ToHashSet();
        if (needed.Count > 0)
        {
            for (int i = 0; i < scan.Records.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (needed.Contains(scan.Records[i].Identity)) oldFiles.Add(scan.Records[i].Identity, i);
            }
        }
        var verified = new HashSet<FileIdentity>();
        var observedNames = new Dictionary<(FileIdentity Parent, string Name), FileIdentity>();
        if (unreadable.Count > 0)
        {
            foreach (var replacement in replacements)
                for (int i = 0; i < replacement.Fresh.Links.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var fresh = replacement.Fresh;
                    var link = fresh.Links[i];
                    var key = (link.ParentIdentity, new string(fresh.Name(i)));
                    var id = fresh.Records[link.FileIndex].Identity;
                    if (observedNames.TryGetValue(key, out var previous) && previous != id)
                        throw new IOException("A name changed during the recheck. Recheck its parent again.");
                    observedNames[key] = id;
                }
        }
        bool retainedNtfsMetadata = scan.RetainsEarlierNtfsMetadata;
        foreach (var replacement in replacements)
        {
            token.ThrowIfCancellationRequested();
            var fresh = replacement.Fresh;
            if (!fresh.HasAllocationData || fresh.IncludesAllDataStreams != scan.IncludesAllDataStreams
                || fresh.WarningCount > 0 && (!allowIncomplete || fresh.WarningCount != fresh.UnreadableEntries.Count))
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
                bool rootUnreadable = unreadable.Contains(fresh.Records[fresh.RootIndex].Identity)
                    && scan.Records[old.FileIndex].Identity == fresh.Records[fresh.RootIndex].Identity;
                foreach (int child in scan.Children(old.FileIndex))
                {
                    var entry = scan.Entry(child);
                    var identity = scan.Records[entry.FileIndex].Identity;
                    // An incomplete enumeration establishes observed names, not
                    // the absence of cached names. Keep unknown children.
                    if (rootUnreadable && (!observedNames.TryGetValue((scan.Records[old.FileIndex].Identity, new string(scan.Name(child))), out var seen)
                        || seen == identity)) continue;
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
                var update = record;
                if (unreadable.Contains(record.Identity) && oldFiles.TryGetValue(record.Identity, out int cached))
                    update = record with { LogicalBytes = scan.Records[cached].LogicalBytes, AllocatedBytes = scan.Records[cached].AllocatedBytes };
                else if (scan.UnreadableEntries.Contains(record.Identity)
                    && (!record.IsDirectory || replacement.Recursive || record.Identity == fresh.Records[fresh.RootIndex].Identity))
                    verified.Add(record.Identity);
                updates[record.Identity] = update;
                if (record.IsDirectory) newDirectoryIds.Add(record.Identity);
                // A partial parent may still have fully readable children. Apply
                // those children's complete namespaces independently.
                if (!replacement.Recursive || !record.IsDirectory || record.Identity == fresh.Records[fresh.RootIndex].Identity
                    || !oldFiles.TryGetValue(record.Identity, out int oldDirectory)) continue;
                foreach (int child in scan.Children(oldDirectory))
                    if (!unreadable.Contains(record.Identity)
                        || observedNames.TryGetValue((record.Identity, new string(scan.Name(child))), out var seen)
                            && seen != scan.Records[scan.Entry(child).FileIndex].Identity)
                        RemoveBranch(child);
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
        // Removing one hard-link name does not validate the remaining aliases.
        // A deleted object's warning can clear only when none of its names remain.
        for (int i = 0; i < scan.Records.Length; i++)
            if (!alive[i] && scan.UnreadableEntries.Contains(scan.Records[i].Identity)
                && !updates.ContainsKey(scan.Records[i].Identity)) verified.Add(scan.Records[i].Identity);
        var builder = new ScanBuilder { AllStreams = scan.IncludesAllDataStreams, CompleteVolumeLinks = false, FallbackReason = scan.FallbackReason };
        verified.ExceptWith(unreadable);
        builder.CarryCoverage(scan, verified);
        var reported = scan.UnreadableEntries.ToHashSet();
        var incompleteScopes = new HashSet<ScanRecheckRequest>();
        foreach (var replacement in replacements)
            for (int i = 0; i < replacement.Fresh.Records.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var fresh = replacement.Fresh;
                var id = fresh.Records[i].Identity;
                if (!unreadable.Contains(id)) continue;
                string path = fresh.EntryPath(fresh.PrimaryLink(i)) ?? replacement.Path;
                incompleteScopes.Add(fresh.Records[i].IsDirectory ? new(path) : new(Path.GetDirectoryName(path) ?? replacement.Path, false));
                if (reported.Add(id))
                    builder.Warn($"Metadata could not be refreshed for {path}; earlier values retained where available.", id);
            }
        foreach (var replacement in replacements)
        {
            token.ThrowIfCancellationRequested();
            if (!unreadable.Contains(replacement.Fresh.Records[replacement.Fresh.RootIndex].Identity))
                builder.ClearUnmeasuredDirectory(replacement.Fresh.Records[replacement.Fresh.RootIndex].Identity);
            if (replacement.Recursive)
                foreach (var record in replacement.Fresh.Records)
                {
                    token.ThrowIfCancellationRequested();
                    if (record.IsDirectory && !unreadable.Contains(record.Identity)) builder.ClearUnmeasuredDirectory(record.Identity);
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
            if (observedNames.Count > 0 && observedNames.ContainsKey((link.ParentIdentity, new string(scan.Name(i))))) continue;
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
        int expectedWarnings = builder.WarningCount;
        var result = builder.Build(scan.RootPath, scan.Backend, scan.Metrics.Acquisition, GC.GetTotalAllocatedBytes(false), token);
        if (result.WarningCount > expectedWarnings) throw new IOException("The recheck could not preserve namespace accounting. A full rescan is required.");
        return new(result.RootPath, result.Backend, result.Records, result.Links, result.NameArena, result.RootIndex,
            result.FirstLinks, result.ChildHeads, result.NextLinks, result.DirectoryLogical, result.DirectoryAllocated,
            scan.IncludesAllDataStreams, true, false, result.Warnings, scan.Metrics, result.EntryFlags, result.WarningCount, result.DirectoryCount, result.FallbackReason)
        { ScannedAtUtc = scan.ScannedAtUtc, LastRecheckUtc = DateTime.UtcNow,
            RecheckedPaths = replacements.Select(r => r.Path).ToArray(), RetainsEarlierNtfsMetadata = retainedNtfsMetadata,
            UnreadableEntries = result.UnreadableEntries,
            IncompleteRecheckScopes = incompleteScopes.ToArray(),
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
                var id = scan.Records[entry.FileIndex].Identity;
                if (unreadable.Contains(id)) continue;
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
