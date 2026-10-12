using System.IO;
using System.Text;
using System.Text.Json;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App.Extensions;

// This is a metadata capability, not a sandbox for an in-process DLL. The
// future model transport may receive only this session's query/proposal API.
internal sealed class InventoryEvidenceSession : IInventoryEvidenceSession
{
    internal const int MaximumPage = 100, MaximumIds = 4096, MaximumCursors = 8;
    internal const int WorkPerQuery = 20_000, WorkPerSession = 250_000, MaximumRequests = 128;
    private const uint UnavailableAttributes = 0x400 | 0x1000 | 0x40000 | 0x400000;
    private readonly object _gate = new();
    private readonly Func<ScanSnapshot?> _currentScan;
    private readonly Func<bool> _isStale;
    private readonly CancellationToken _lifetime;
    private readonly ScanSnapshot _scan;
    private readonly int _rootLink, _rootFile;
    private readonly HashSet<int> _excluded = [];
    private readonly Dictionary<EvidenceId, int> _issued = [];
    private readonly Dictionary<int, EvidenceId> _ids = [];
    private readonly Dictionary<Guid, Cursor> _cursors = [];
    private int _work, _requests;
    private bool _disposed;
    public EvidenceScope Scope { get; }

    private sealed record Cursor(int Directory, string? Query, IEnumerator<int> Entries) : IDisposable
    { public void Dispose() => Entries.Dispose(); }

    internal InventoryEvidenceSession(Func<ScanSnapshot?> currentScan, Func<bool> isStale, string root,
        IReadOnlyList<string> excludedPaths, CancellationToken lifetime = default)
    {
        _currentScan = currentScan; _isStale = isStale; _lifetime = lifetime;
        _scan = currentScan() ?? throw new InvalidOperationException("Scan the selected location before opening an evidence session.");
        EnsureCurrent();
        if (excludedPaths is null || excludedPaths.Count > 64) throw new ArgumentException("Choose at most 64 exclusions.", nameof(excludedPaths));
        _rootLink = _scan.FindEntry(root) ?? throw new ArgumentException("The scope is outside the available scan.", nameof(root));
        var entry = _scan.Entry(_rootLink); _rootFile = entry.FileIndex;
        if (!entry.IsDirectory || !entry.IsPrimaryName || !CanDescend(_rootLink) || !RegularAncestors(_rootLink))
            throw new ArgumentException("Choose a regular, resolved scanned folder.", nameof(root));
        var exclusions = new List<string>();
        foreach (var path in excludedPaths)
        {
            int link = _scan.FindEntry(path) ?? throw new ArgumentException("An exclusion is outside the available scan.", nameof(excludedPaths));
            if (link == _rootLink || !Allowed(link)) throw new ArgumentException("An exclusion must be inside the selected scope.", nameof(excludedPaths));
            // Collect first, so redundant/nested exclusions remain valid input.
            exclusions.Add(_scan.EntryPath(link)!);
        }
        foreach (var path in exclusions) _excluded.Add(_scan.FindEntry(path)!.Value);
        Scope = new(Guid.NewGuid(), Describe(_rootLink), Array.AsReadOnly(exclusions.ToArray()), Coverage(false));
        EnsureCurrent();
    }

    public Task<EvidenceEntry> DetailsAsync(EvidenceId id, CancellationToken token) => Run(() => Describe(Resolve(id)), token);

    public Task<EvidencePage> ChildrenAsync(EvidenceId directory, int limit, Guid? continuation, CancellationToken token) =>
        Run(() => Page(directory, null, limit, continuation, token), token);

    public Task<EvidencePage> SearchAsync(EvidenceId directory, string nameContains, int limit, Guid? continuation, CancellationToken token) =>
        Run(() =>
        {
            if (string.IsNullOrWhiteSpace(nameContains) || nameContains.Length > 128)
                throw new ArgumentException("Search for 1–128 characters.", nameof(nameContains));
            return Page(directory, nameContains, limit, continuation, token);
        }, token);

    public Task<EvidenceSummary> SummarizeAsync(IReadOnlyList<EvidenceId> roots, CancellationToken token) =>
        Run(() => Summarize(roots, token), token);

    public Task<EvidenceProposalAdmission> ValidateProposalAsync(string json, CancellationToken token) => Run(() =>
    {
        try { return Admit(json, token); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        { return new(false, [], null, [error.Message]); }
    }, token);

    private Task<T> Run<T>(Func<T> operation, CancellationToken token) => Task.Run(() =>
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested(); EnsureCurrent();
            if (++_requests > MaximumRequests || _work >= WorkPerSession)
                throw new InvalidOperationException("The evidence session reached its query budget. Open a new user-selected session.");
            var result = operation();
            token.ThrowIfCancellationRequested(); EnsureCurrent();
            return result;
        }
    }, token);

    private EvidencePage Page(EvidenceId directory, string? query, int limit, Guid? continuation, CancellationToken token)
    {
        if (limit is < 1 or > MaximumPage) throw new ArgumentOutOfRangeException(nameof(limit));
        int link = Resolve(directory);
        if (!_scan.Entry(link).IsDirectory || !CanDescend(link)) throw new InvalidDataException("This item cannot be browsed as a regular folder.");
        Cursor cursor;
        if (continuation is { } key)
        {
            if (!_cursors.TryGetValue(key, out cursor!) || cursor.Directory != link || cursor.Query != query)
                throw new InvalidDataException("Unknown continuation or changed query.");
            _cursors.Remove(key);
        }
        else
        {
            if (_cursors.Count >= MaximumCursors) throw new InvalidDataException("Finish an existing page sequence before starting another.");
            cursor = new(link, query, (query is null ? _scan.EnumerateChildren(_scan.Entry(link).FileIndex)
                : Walk(link, includeRoot: false)).GetEnumerator());
        }
        try
        {
            var items = new List<EvidenceEntry>(limit); int examined = 0;
            while (items.Count < limit && Spend(ref examined))
            {
                token.ThrowIfCancellationRequested(); _lifetime.ThrowIfCancellationRequested();
                if (!cursor.Entries.MoveNext()) { cursor.Dispose(); return new(items.AsReadOnly(), null, true); }
                int child = cursor.Entries.Current;
                if (Allowed(child) && (query is null || _scan.Entry(child).Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    items.Add(Describe(child));
            }
            Guid next = Guid.NewGuid(); _cursors.Add(next, cursor);
            return new(items.AsReadOnly(), next, false);
        }
        catch { cursor.Dispose(); throw; }
    }

    private EvidenceSummary Summarize(IReadOnlyList<EvidenceId> roots, CancellationToken token)
    {
        if (roots is null || roots.Count is < 1 or > MaximumPage) throw new ArgumentException("Summarize 1–100 issued items.", nameof(roots));
        var selected = roots.Distinct().Select(Resolve).ToHashSet();
        // Collapse containing selections before traversal; preserve exact link
        // membership for hard-link aliases, rather than comparing path strings.
        var top = selected.Where(link => !HasSelectedAncestor(link, selected)).ToArray();
        var objects = new HashSet<int>(); var directories = new HashSet<int>();
        var types = new Dictionary<string, long>(StringComparer.Ordinal);
        long names = 0, logical = 0, allocated = 0, shared = 0, unknownDates = 0;
        long? oldest = null, newest = null;
        bool traversalComplete = true, metadataComplete = true; int examined = 0;
        foreach (int root in top)
        {
            foreach (int link in Walk(root, includeRoot: true))
            {
                token.ThrowIfCancellationRequested(); _lifetime.ThrowIfCancellationRequested();
                if (!Spend(ref examined)) { traversalComplete = false; break; }
                if (!Allowed(link)) continue;
                var entry = _scan.Entry(link); var metadata = _scan.GetMetadata(link);
                if (entry.IsIncomplete || (metadata.Attributes & UnavailableAttributes) != 0) metadataComplete = false;
                if (entry.IsDirectory)
                {
                    if (!directories.Add(entry.FileIndex)) { metadataComplete = false; continue; }
                    if (_scan.IsUnmeasuredDirectory(entry.FileIndex)) metadataComplete = false;
                    continue;
                }
                names++;
                string type = FileTypeClassifier.Label(FileTypeClassifier.Classify(entry.Name));
                types[type] = types.GetValueOrDefault(type) + 1;
                if (!ScanSnapshot.IsValidFileTime(metadata.LastWriteFileTime)) unknownDates++;
                else { oldest = Math.Min(oldest ?? metadata.LastWriteFileTime, metadata.LastWriteFileTime);
                    newest = Math.Max(newest ?? metadata.LastWriteFileTime, metadata.LastWriteFileTime); }
                if (!objects.Add(entry.FileIndex) && _scan.HasAllocationData) continue;
                logical = checked(logical + entry.LogicalBytes); allocated = checked(allocated + entry.AllocatedBytes);
                if (entry.IsShared) shared = checked(shared + entry.LogicalBytes);
            }
            if (!traversalComplete) break;
        }
        var totals = new EvidenceTotals(names, directories.Count, _scan.HasAllocationData ? objects.Count : null,
            logical, _scan.HasAllocationData ? allocated : null, _scan.HasAllocationData ? shared : null, traversalComplete, unknownDates, oldest, newest);
        var coverage = Coverage(traversalComplete && metadataComplete);
        var cautions = new List<string>
        {
            "Metadata is an observation, not proof that personal data can be discarded. Live identity and classification must be checked again before any later cleanup.",
            "Sizes describe observed files in the selected union, not bytes guaranteed to be released. Directory aggregates are not added to file sizes.",
            "Filename types and modification dates do not establish contents, ownership, last use or abandonment."
        };
        if (!traversalComplete) cautions.Add("The query budget was reached. Counts and sizes cover only the visited part; choose a smaller scope for a complete summary.");
        if (!_scan.HasAllocationData) cautions.Add("Physical identity and allocation are unavailable. Logical sizes are file-name sizes and may count hard-link aliases more than once.");
        if (shared > 0 || !_scan.HasCompleteVolumeLinks) cautions.Add("Files may have names outside the selected scope. Removing one name may release no space.");
        if (!_scan.IncludesAllDataStreams) cautions.Add("Named DATA streams are outside this scan's size measurements.");
        if (_excluded.Count > 0) cautions.Add("User-excluded items and their descendants are omitted from these counts and sizes.");
        if (!coverage.Complete) cautions.Add("Incomplete, unavailable or retained metadata prevents a complete current account of this scope.");
        string basis = _scan.HasAllocationData ? "unique observed file objects" : "observed file names; physical identity unknown";
        string text = FormattableString.Invariant($"Observed {names} file names in {directories.Count} folders. Logical size: {logical} bytes ({basis}). ")
            + (totals.AllocatedBytes is { } bytes ? FormattableString.Invariant($"Allocation: {bytes} bytes. ") : "Allocation: unknown. ")
            + (coverage.Complete ? "Metadata traversal complete." : "Coverage incomplete.");
        return new(Scope.Revision, Array.AsReadOnly(top.Select(link => _ids[link]).ToArray()), "host.metadata-summary", 1,
            coverage, totals, Array.AsReadOnly(types.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new EvidenceFileTypeCount(pair.Key, pair.Value)).ToArray()), cautions.AsReadOnly(), text);
    }

    private EvidenceProposalAdmission Admit(string json, CancellationToken token)
    {
        if (json is null || json.Length > 65_536 || Encoding.UTF8.GetByteCount(json) > 65_536)
            throw new InvalidDataException("Proposal exceeds 64 KiB.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        Fields(root, "schemaVersion", "items");
        if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number
            || !root.GetProperty("schemaVersion").TryGetInt32(out int schema) || schema != 1)
            throw new InvalidDataException("Unsupported proposal schema.");
        var input = root.GetProperty("items");
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() is < 1 or > MaximumPage)
            throw new InvalidDataException("A proposal must contain 1–100 investigation items.");
        var items = new List<EvidenceReviewItem>(); var unique = new HashSet<EvidenceId>();
        foreach (var item in input.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            Fields(item, "id", "action", "explanation");
            string idText = String(item, "id", 36);
            if (!Guid.TryParseExact(idText, "D", out var guid)) throw new InvalidDataException("Invalid item ID.");
            var id = new EvidenceId(guid);
            if (!unique.Add(id)) throw new InvalidDataException("Duplicate proposal item.");
            if (String(item, "action", 32) != "investigate") throw new InvalidDataException("Only investigation is supported; cleanup actions are not admitted.");
            var entry = Describe(Resolve(id));
            if (entry.ReviewPolicy.Disposition == EvidenceReviewDisposition.Blocked)
                throw new InvalidDataException("Host policy blocks this item: " + string.Join(" ", entry.ReviewPolicy.Reasons));
            items.Add(new(entry, String(item, "explanation", 2000)));
        }
        var summary = Summarize(items.Select(item => item.Entry.Id).ToArray(), token);
        if (!summary.Coverage.Complete) throw new InvalidDataException("Refresh or narrow the scope before proposing items with incomplete coverage.");
        return new(true, items.AsReadOnly(), summary, []);
    }

    private static void Fields(JsonElement item, params string[] required)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a proposal object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
            if (!required.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate proposal field.");
        if (seen.Count != required.Length) throw new InvalidDataException("A required proposal field is missing.");
    }
    private static string String(JsonElement item, string name, int maximum)
    {
        var value = item.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum)
            throw new InvalidDataException("Invalid proposal text.");
        return text;
    }

    private EvidenceEntry Describe(int link)
    {
        if (!Allowed(link)) throw new InvalidDataException("This item is outside the authorized evidence scope.");
        if (!_ids.TryGetValue(link, out var id))
        {
            if (_ids.Count >= MaximumIds) throw new InvalidDataException("The session reached its issued-item limit. Choose a smaller scope.");
            id = new(Guid.NewGuid()); _ids.Add(link, id); _issued.Add(id, link);
        }
        var entry = _scan.Entry(link); var metadata = _scan.GetMetadata(link);
        var reasons = new List<string>();
        if (entry.IsDirectory && FolderDeletionTarget.SelectionBlockReason(_scan, link) is { } blocked) reasons.Add(blocked);
        if ((metadata.Attributes & UnavailableAttributes) != 0) reasons.Add("Linked, offline or recall-on-access items are metadata-only.");
        if (entry.IsIncomplete || (entry.IsDirectory && _scan.IsUnmeasuredDirectory(entry.FileIndex))) reasons.Add("This item has incomplete metadata.");
        string path = _scan.EntryPath(link) ?? throw new InvalidDataException("The scanned path is unresolved.");
        if (IsProtectedPath(path)) reasons.Add("Filesystem metadata and Recycle Bin storage are protected.");
        var policy = new EvidenceReviewPolicy(reasons.Count > 0 ? EvidenceReviewDisposition.Blocked : EvidenceReviewDisposition.ManualInvestigation,
            "host.metadata-review", 1, reasons.Count > 0 ? reasons.AsReadOnly() : ["No deterministic rule here establishes that these contents are disposable."]);
        return new(id, entry.Name, path, entry.IsDirectory, entry.IsDirectory ? null : entry.LogicalBytes,
            !entry.IsDirectory && _scan.HasAllocationData ? entry.AllocatedBytes : null, metadata.Attributes, metadata.LastWriteFileTime,
            metadata.NativeIdLow is { } low && metadata.NativeIdHigh is { } high ? new(low, high) : null, entry.IsShared, policy);
    }

    private int Resolve(EvidenceId id) => _issued.TryGetValue(id, out int link) && Allowed(link) ? link
        : throw new InvalidDataException("Unknown, expired or out-of-scope item ID.");

    private bool Allowed(int link)
    {
        if (_excluded.Contains(link)) return false;
        if (link == _rootLink) return true;
        // An unavailable/reparse leaf is visible as blocked metadata. Its
        // descendants are omitted; otherwise a skipped leaf could look like
        // an empty, completely measured folder.
        int directory = _scan.Entry(link).ParentIndex;
        for (int depth = 0; directory >= 0 && depth < 512; depth++)
        {
            int ancestor = _scan.PrimaryLink(directory);
            if (_excluded.Contains(ancestor) || !CanDescend(ancestor)) return false;
            if (directory == _rootFile) return true;
            directory = _scan.Entry(ancestor).ParentIndex;
        }
        if (directory >= 0) throw new InvalidDataException("The scanned namespace exceeds the ancestry limit. Its contents cannot be reported as complete.");
        return false;
    }

    private bool RegularAncestors(int link)
    {
        int directory = _scan.Entry(link).ParentIndex;
        for (int depth = 0; directory >= 0 && depth < 512; depth++)
        {
            int ancestor = _scan.PrimaryLink(directory);
            if (!CanDescend(ancestor)) return false;
            if (directory == _scan.RootIndex) return true;
            directory = _scan.Entry(ancestor).ParentIndex;
        }
        return link == -1;
    }

    private bool CanDescend(int link) => (_scan.GetMetadata(link).Attributes & UnavailableAttributes) == 0;

    private bool HasSelectedAncestor(int link, HashSet<int> selected)
    {
        int directory = _scan.Entry(link).ParentIndex;
        for (int depth = 0; directory >= 0 && depth < 512; depth++)
        {
            int parent = _scan.PrimaryLink(directory);
            if (selected.Contains(parent)) return true;
            if (directory == _rootFile) return false;
            directory = _scan.Entry(parent).ParentIndex;
        }
        if (directory >= 0) throw new InvalidDataException("The scanned namespace exceeds the ancestry limit. Its selection union cannot be verified.");
        return false;
    }

    private IEnumerable<int> Walk(int root, bool includeRoot)
    {
        var pending = new Stack<IEnumerator<int>>(); var directories = new HashSet<int>();
        pending.Push(Enumerable.Repeat(root, 1).GetEnumerator());
        try
        {
            while (pending.Count > 0)
            {
                var iterator = pending.Peek();
                if (!iterator.MoveNext()) { iterator.Dispose(); pending.Pop(); continue; }
                int link = iterator.Current;
                var entry = _scan.Entry(link);
                if (Allowed(link) && entry.IsDirectory && CanDescend(link) && directories.Add(entry.FileIndex))
                    pending.Push(_scan.EnumerateChildren(entry.FileIndex).GetEnumerator());
                if (includeRoot || link != root) yield return link;
            }
        }
        finally { foreach (var iterator in pending) iterator.Dispose(); }
    }

    private EvidenceCoverage Coverage(bool complete) => new(_scan.Backend, _scan.ScannedAtUtc, _scan.LastRecheckUtc,
        _scan.RetainsEarlierNtfsMetadata, complete && _scan.WarningCount == 0 && !_scan.RetainsEarlierNtfsMetadata,
        _scan.HasAllocationData, _scan.IncludesAllDataStreams, _scan.HasCompleteVolumeLinks, _scan.WarningCount,
        Array.AsReadOnly(_scan.Warnings.Take(32).ToArray()));

    private bool Spend(ref int examined)
    { if (examined >= WorkPerQuery || _work >= WorkPerSession) return false; examined++; _work++; return true; }

    private static bool IsProtectedPath(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || path.StartsWith('\\')) return true;
        string first = path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.StartsWith('$') || first.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Recycler", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _lifetime.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_currentScan(), _scan) || _isStale())
            throw new InvalidOperationException("The evidence snapshot changed or is out of date. Refresh and open a new session.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var cursor in _cursors.Values) cursor.Dispose();
            _cursors.Clear(); _issued.Clear(); _ids.Clear();
        }
    }
}
