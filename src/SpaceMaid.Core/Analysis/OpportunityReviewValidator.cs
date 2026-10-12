using System.Diagnostics;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

public sealed record OpportunityReviewValidation(ScanSnapshot Snapshot, IReadOnlyList<Opportunity> Findings,
    IReadOnlyList<Opportunity> ValidatedLocations, IReadOnlyList<string> ExcludedPaths,
    IReadOnlyList<ScanRecheckRequest> RecheckedScopes, bool CanMergeInventory,
    IReadOnlyList<ScanRecheckRequest> UncheckedScopes);

public sealed record OpportunityValidationProgress(int Completed, int Total, string Path);

public static class OpportunityReviewValidator
{
    public static Task<OpportunityReviewValidation> ValidateAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> selected,
        CancellationToken token = default, Action<string, object>? diagnostic = null, OpportunityDiscovery? discover = null,
        IProgress<OpportunityValidationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(scan); ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(discover);
        var captured = selected.ToArray();
        return Task.Run(() => ValidateCoreAsync(scan, captured, token, diagnostic, discover, progress), token);
    }

    private static async Task<OpportunityReviewValidation> ValidateCoreAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> selected,
        CancellationToken token, Action<string, object>? diagnostic, OpportunityDiscovery discover, IProgress<OpportunityValidationProgress>? progress)
    {
        token.ThrowIfCancellationRequested();
        if (selected.Count == 0) throw new ArgumentException("Select at least one opportunity to validate.", nameof(selected));
        var timer = Stopwatch.StartNew();
        var choices = new List<Choice>();
        var requests = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var opportunity in selected.DistinctBy(value => (value.Path, value.ProviderId, value.RuleId)))
        {
            token.ThrowIfCancellationRequested();
            string path = Normalize(opportunity.Path);
            if ((uint)opportunity.DirectoryIndex >= (uint)scan.FileCount || !scan.Records[opportunity.DirectoryIndex].IsDirectory
                || !string.Equals(Normalize(scan.PathForFile(opportunity.DirectoryIndex)
                    ?? throw new ArgumentException("A selected opportunity has no resolved location.")), path, StringComparison.Ordinal))
                throw new ArgumentException("A selected opportunity does not belong to this scan.", nameof(selected));
            bool available = IsLiveDirectory(path);
            choices.Add(new(opportunity, path, scan.Records[opportunity.DirectoryIndex].Identity, available));
            if (available)
            {
                AddRequest(path, recursive: true);
                string? parent = Path.GetDirectoryName(path);
                if (parent is not null && scan.FindEntry(parent) is { } link && scan.Entry(link).IsDirectory)
                    AddRequest(NearestLiveParent(parent), recursive: false);
            }
            else AddRequest(NearestLiveParent(Path.GetDirectoryName(path)), recursive: false);
        }
        var scopes = PlanScopes(requests.Select(value => new ScanRecheckRequest(value.Key, value.Value)), token);
        diagnostic?.Invoke("opportunity-validation-start", new { Selected = choices.Count, Scopes = scopes.Length, scan.HasAllocationData });
        var reader = new VolumeScanner();
        var replacements = new List<ScanRechecker.Replacement>(scopes.Length);
        var progressTimer = Stopwatch.StartNew(); int completed = 0;
        foreach (var scope in scopes)
        {
            token.ThrowIfCancellationRequested();
            ScanRechecker.ValidateAncestors(scope.Path);
            diagnostic?.Invoke("opportunity-validation-read-start", new { scope.Path, scope.Recursive });
            if (completed == 0 || progressTimer.ElapsedMilliseconds >= 150)
            { progress?.Report(new(completed, scopes.Length, scope.Path)); progressTimer.Restart(); }
            var fresh = await reader.ReadDirectoryAsync(scope.Path, scope.Recursive, token,
                allStreams: scan.HasAllocationData && scan.IncludesAllDataStreams, allowBasic: !scan.HasAllocationData).ConfigureAwait(false);
            if (fresh.WarningCount > 0) throw new IOException("The selected scope could not be fully validated: " + string.Join(" ", fresh.Warnings));
            replacements.Add(new(scope.Path, scope.Recursive, fresh));
            diagnostic?.Invoke("opportunity-validation-read-complete", new { scope.Path, scope.Recursive, fresh.FileCount, ElapsedMs = fresh.Metrics.Acquisition.TotalMilliseconds });
            completed++;
        }
        progress?.Report(new(completed, scopes.Length, ""));
        var sourceIndex = new Dictionary<string, ScanRechecker.Replacement>(StringComparer.Ordinal);
        var childIndexes = new Dictionary<string, ChildNameIndex>(StringComparer.Ordinal);
        foreach (var source in replacements)
        {
            token.ThrowIfCancellationRequested(); sourceIndex.Add(source.Path, source);
            if (!source.Recursive) childIndexes.Add(source.Path, new(source.Fresh, token));
        }
        CheckFreshNamespace(replacements, sourceIndex, childIndexes, token);
        var uncheckedDirectories = scan.HasAllocationData ? UncheckedDirectories(scan, replacements, scopes, token) : new UncheckedDirectorySet([], []);
        ScanSnapshot updated = scan.HasAllocationData
            ? ScanRechecker.Merge(scan, replacements, token, uncheckedDirectories.Identities)
            : BuildLogicalSelection(scan, replacements, sourceIndex, childIndexes, token, timer.Elapsed);
        var findings = discover(updated, token);
        var findingIndex = new Dictionary<(int Directory, string Provider, string Rule, string Title, string Category), Opportunity>();
        foreach (var finding in findings)
        {
            token.ThrowIfCancellationRequested();
            findingIndex.TryAdd((finding.DirectoryIndex, finding.ProviderId, finding.RuleId, finding.Title, finding.Category), finding);
        }
        var validated = new List<Opportunity>(); var excluded = new List<string>();
        foreach (var choice in choices)
        {
            token.ThrowIfCancellationRequested();
            int? link = choice.Available ? updated.FindEntry(choice.Path) : null;
            if (link is { } current && updated.Entry(current).IsDirectory
                && (!scan.HasAllocationData || updated.Records[updated.Entry(current).FileIndex].Identity == choice.Identity))
            {
                if (findingIndex.TryGetValue((updated.Entry(current).FileIndex, choice.Original.ProviderId, choice.Original.RuleId, choice.Original.Title, choice.Original.Category), out var finding))
                { validated.Add(finding); continue; }
            }
            excluded.Add(choice.Original.Path);
        }
        diagnostic?.Invoke("opportunity-validation-complete", new { Validated = validated.Count, Excluded = excluded.Count,
            Unchecked = uncheckedDirectories.Scopes.Count, CanMergeInventory = scan.HasAllocationData, ElapsedMs = timer.Elapsed.TotalMilliseconds });
        return new(updated, findings, validated, excluded, scopes, scan.HasAllocationData, uncheckedDirectories.Scopes);

        void AddRequest(string path, bool recursive)
        { path = Normalize(path); requests[path] = recursive || requests.GetValueOrDefault(path); }
        string NearestLiveParent(string? path)
        {
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                token.ThrowIfCancellationRequested();
                if (scan.FindEntry(current) is { } known && scan.Entry(known).IsDirectory && IsLiveDirectory(current)) return current;
                if (Normalize(current) == Normalize(scan.RootPath)) break;
            }
            throw new DirectoryNotFoundException("No available parent in the original scan could validate the missing opportunity.");
        }
    }

    internal static ScanRecheckRequest[] PlanScopes(IEnumerable<ScanRecheckRequest> requests, CancellationToken token)
    {
        var unique = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested(); string path = Normalize(request.Path);
            unique[path] = request.Recursive || unique.GetValueOrDefault(path);
        }
        var ordered = unique.OrderBy(value => value.Key.Length).ThenBy(value => value.Key, StringComparer.Ordinal).ToArray();
        token.ThrowIfCancellationRequested();
        var recursive = new Dictionary<string, bool>(StringComparer.Ordinal);
        var result = new List<ScanRecheckRequest>(ordered.Length);
        foreach (var request in ordered)
        {
            token.ThrowIfCancellationRequested();
            if (TryFindAncestor(request.Key, recursive, token, out _, includeSelf: false)) continue;
            result.Add(new(request.Key, request.Value));
            if (request.Value) recursive.Add(request.Key, true);
        }
        return result.ToArray();
    }

    private static void CheckFreshNamespace(IReadOnlyList<ScanRechecker.Replacement> replacements,
        IReadOnlyDictionary<string, ScanRechecker.Replacement> sourceIndex,
        IReadOnlyDictionary<string, ChildNameIndex> childIndexes, CancellationToken token)
    {
        // Planned recursive scopes never overlap. Only an immediate shallow
        // parent can have observed the selected root's identity beforehand.
        foreach (var scope in replacements)
        {
            token.ThrowIfCancellationRequested(); string? path = Path.GetDirectoryName(scope.Path);
            if (path is null || !sourceIndex.TryGetValue(path, out var parent) || parent.Recursive
                || !parent.Fresh.HasAllocationData || !scope.Fresh.HasAllocationData) continue;
            int? found = childIndexes[path].Find(Path.GetFileName(scope.Path));
            if (found is null || parent.Fresh.Records[parent.Fresh.Entry(found.Value).FileIndex].Identity != scope.Fresh.Records[scope.Fresh.RootIndex].Identity)
                throw new IOException("A selected folder changed between its parent and subtree checks. Validate the selection again.");
        }
    }

    private static UncheckedDirectorySet UncheckedDirectories(ScanSnapshot original,
        IReadOnlyList<ScanRechecker.Replacement> replacements, IReadOnlyList<ScanRecheckRequest> scopes, CancellationToken token)
    {
        var uncheckedPaths = new HashSet<string>(StringComparer.Ordinal);
        var uncheckedIds = new HashSet<FileIdentity>(); var replacedRoots = new HashSet<FileIdentity>();
        var recursive = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        { token.ThrowIfCancellationRequested(); if (scope.Recursive) recursive.Add(scope.Path, true); }
        foreach (var source in replacements)
        {
            token.ThrowIfCancellationRequested();
            if (source.Recursive && original.FindEntry(source.Path) is { } old
                && original.Records[original.Entry(old).FileIndex].Identity != source.Fresh.Records[source.Fresh.RootIndex].Identity)
                replacedRoots.Add(original.Records[original.Entry(old).FileIndex].Identity);
        }
        foreach (var parent in replacements)
        {
            token.ThrowIfCancellationRequested(); if (parent.Recursive) continue;
            int? old = original.FindEntry(parent.Path);
            var oldIds = new HashSet<FileIdentity>();
            if (old is { } link)
                foreach (int child in original.Children(original.Entry(link).FileIndex))
                {
                    token.ThrowIfCancellationRequested(); var entry = original.Entry(child);
                    if (entry.IsDirectory && !original.IsUnmeasuredDirectory(entry.FileIndex)) oldIds.Add(original.Records[entry.FileIndex].Identity);
                }
            foreach (int child in parent.Fresh.Children(parent.Fresh.RootIndex))
            {
                token.ThrowIfCancellationRequested();
                var entry = parent.Fresh.Entry(child);
                var identity = parent.Fresh.Records[entry.FileIndex].Identity;
                if (!entry.IsDirectory || oldIds.Contains(identity) && !replacedRoots.Contains(identity)) continue;
                string path = parent.Fresh.EntryPath(child) ?? throw new IOException("A newly observed sibling path is unresolved.");
                if (!TryFindAncestor(path, recursive, token, out _))
                { uncheckedPaths.Add(path); uncheckedIds.Add(identity); }
            }
        }
        return new(uncheckedPaths.Order(StringComparer.Ordinal).Select(path => new ScanRecheckRequest(path)).ToArray(), uncheckedIds);
    }

    private static ScanSnapshot BuildLogicalSelection(ScanSnapshot original, IReadOnlyList<ScanRechecker.Replacement> sources,
        IReadOnlyDictionary<string, ScanRechecker.Replacement> sourceIndex,
        IReadOnlyDictionary<string, ChildNameIndex> childIndexes, CancellationToken token, TimeSpan elapsed)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var builder = new ScanBuilder { AllocationData = false, AllStreams = false, CompleteVolumeLinks = false };
        var ids = new Dictionary<string, (FileIdentity Identity, int Index)>(StringComparer.Ordinal);
        string root = Normalize(original.RootPath);
        ulong sequence = 1;
        builder.RootIndex = Add(root, new(default, FileAttributes.Directory, 0, 0, 0));
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            string observedRoot = source.Path;
            if (TryFindAncestor(source.Path, sourceIndex, token, out var parent, includeSelf: false))
            {
                int? link = Path.GetDirectoryName(source.Path) == parent.Path && !parent.Recursive
                    ? childIndexes[parent.Path].Find(Path.GetFileName(source.Path)) : parent.Fresh.FindEntry(source.Path);
                if (link is { } found) observedRoot = parent.Fresh.EntryPath(found)!;
            }
            Add(observedRoot, source.Fresh.Records[source.Fresh.RootIndex]);
            for (int i = 0; i < source.Fresh.Links.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string path = source.Fresh.EntryPath(i) ?? throw new IOException("A refreshed logical path is unresolved.");
                if (observedRoot != source.Path) path = observedRoot + path[source.Path.Length..];
                Add(path, source.Fresh.Records[source.Fresh.Links[i].FileIndex]);
            }
        }
        builder.Warn("Only selected scopes and immediate parent metadata were validated. Physical allocation and hard-link identity are unknown; the full inventory remains out of date.");
        return builder.Build(original.RootPath, "Fresh logical selection review", elapsed, allocatedBefore, token);

        int Add(string path, FileRecord metadata)
        {
            token.ThrowIfCancellationRequested(); path = Normalize(path);
            if (!Contains(root, path)) throw new IOException("A refreshed selection path is outside the original scan.");
            if (ids.TryGetValue(path, out var existing))
            {
                builder.ReplaceMetadata(metadata with { Identity = existing.Identity, AllocatedBytes = 0 });
                return existing.Index;
            }
            FileIdentity? parentId = null;
            if (path != root)
            {
                string parent = Path.GetDirectoryName(path) ?? throw new IOException("A logical selection parent is unresolved.");
                if (!ids.ContainsKey(parent)) Add(parent, new(default, FileAttributes.Directory, 0, 0, 0));
                parentId = ids[parent].Identity;
            }
            var identity = new FileIdentity(sequence++);
            int index = builder.AddFile(metadata with { Identity = identity, AllocatedBytes = 0 }); ids[path] = (identity, index);
            if (parentId is { } parentIdentity) builder.AddLink(index, parentIdentity, Path.GetFileName(path));
            return index;
        }
    }
    private static bool IsLiveDirectory(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Contains(string root, string path)
    {
        root = Normalize(root); path = Normalize(path);
        return path == root || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private sealed record Choice(Opportunity Original, string Path, FileIdentity Identity, bool Available);
    private sealed record UncheckedDirectorySet(IReadOnlyList<ScanRecheckRequest> Scopes, IReadOnlyCollection<FileIdentity> Identities);

    private static bool TryFindAncestor<T>(string path, IReadOnlyDictionary<string, T> index, CancellationToken token,
        out T value, bool includeSelf = true)
    {
        for (string? current = includeSelf ? path : Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested();
            if (index.TryGetValue(current, out value!)) return true;
        }
        value = default!; return false;
    }

    private sealed class ChildNameIndex
    {
        private readonly Dictionary<string, int> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _insensitive = new(StringComparer.OrdinalIgnoreCase);
        public ChildNameIndex(ScanSnapshot snapshot, CancellationToken token)
        {
            foreach (int child in snapshot.Children(snapshot.RootIndex))
            {
                token.ThrowIfCancellationRequested(); string name = snapshot.Entry(child).Name;
                _exact.TryAdd(name, child);
                if (!_insensitive.TryAdd(name, child)) _insensitive[name] = -1;
            }
        }
        public int? Find(string name) => _exact.TryGetValue(name, out int exact) ? exact
            : _insensitive.TryGetValue(name, out int insensitive) && insensitive >= 0 ? insensitive : null;
    }
}
