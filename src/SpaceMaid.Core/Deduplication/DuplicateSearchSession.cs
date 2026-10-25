using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Deduplication;

// A session belongs to one immutable scan and one captured scope/policy. It
// retains bounded facts, never open file handles, across user-requested pages.
public sealed class DuplicateSearchSession : IDisposable
{
    private readonly ScanSnapshot _scan;
    private readonly DuplicateSearchPolicy _policy;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, Captured> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<long, List<Captured>> _sizes = [];
    private readonly Dictionary<(long Bytes, string Hash), List<DuplicateFile>> _hashes = [];
    private readonly Dictionary<(long Bytes, string Hash), List<DuplicateFile>> _verified = [];
    private int _cursor, _aliases;
    private bool _inputComplete, _capacityReached, _unknown, _disposed;
    public bool CanContinue { get; private set; } = true;
    public int RetainedFiles => _objects.Count;
    private sealed class Captured(DuplicateFile file)
    {
        internal DuplicateFile File = file;
        internal bool Hashed, Suppressed;
    }

    public DuplicateSearchSession(ScanSnapshot scan, DuplicateSearchPolicy? policy = null)
    {
        _scan = scan; _policy = policy ?? new(); _policy.Validate();
        _policy = _policy with { ExcludedPaths = _policy.ExcludedPaths.Select(OptimizationPolicy.NormalizePath).ToArray() };
        _cursor = Math.Min(_policy.StartRecord, scan.FileCount); _unknown = scan.WarningCount > 0;
    }

    public async Task<DuplicateSearchResult> ContinueAsync(IProgress<DuplicateSearchProgress>? progress = null, CancellationToken token = default)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            return await Task.Run(() => Page(progress, linked.Token)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private DuplicateSearchResult Page(IProgress<DuplicateSearchProgress>? progress, CancellationToken token)
    {
        var budget = new DuplicateFinder.ReadBudget(_policy.MaximumReadBytes, _policy.MaximumTime, token);
        var issues = new List<DuplicateSearchIssue>(); int examined = 0;
        try
        {
            while (!_inputComplete && !_capacityReached && _cursor < _scan.FileCount)
            {
                budget.Check();
                if (examined > 0 && budget.Elapsed >= _policy.MaximumTime / 2) break;
                var record = _scan.Records[_cursor];
                if (record.IsDirectory || record.LogicalBytes < _policy.MinimumBytes) { _cursor++; continue; }
                if (examined >= _policy.MaximumFiles) break;
                if (_objects.Count >= _policy.MaximumRetainedFiles) { _capacityReached = true; _unknown = true; break; }
                examined++;
                string? path = _scan.PathForFile(_cursor++);
                if (path is null) { _unknown = true; continue; }
                if (_policy.ExcludedPaths.Any(p => OptimizationPolicy.Covers(p, path))) continue;
                if ((record.Attributes & StorageFile.Unavailable) != 0) { Issue(path, "Linked, encrypted, system or offline file was not compared."); continue; }
                try
                {
                    using var metadata = StorageFile.Open(path, metadataOnly: true);
                    if (_objects.ContainsKey(metadata.Revision.ObjectKey)) continue;
                    if (metadata.Revision.Length < _policy.MinimumBytes) continue;
                    _aliases += (int)Math.Max(0, metadata.Revision.Links - 1);
                    var captured = new Captured(new(path, metadata.Revision, "")); _objects.Add(metadata.Revision.ObjectKey, captured);
                    if (!_sizes.TryGetValue(metadata.Revision.Length, out var size)) _sizes.Add(metadata.Revision.Length, size = []);
                    size.Add(captured);
                }
                catch (Exception error) when (DuplicateFinder.Expected(error)) { Issue(path, error.Message); }
            }
            _inputComplete = _cursor >= _scan.FileCount;
            progress?.Report(new("Comparing candidates", examined, budget.Bytes));
            long comparisonReserve = _hashes.Where(g => g.Value.Count(f => !_objects[f.Revision.ObjectKey].Suppressed) > 1)
                .Where(g => g.Value.Any(f => !_objects[f.Revision.ObjectKey].Suppressed && (!_verified.TryGetValue(g.Key, out var done)
                    || !done.Any(v => v.Revision.ObjectKey == f.Revision.ObjectKey))))
                .Select(g => g.Key.Bytes * 2).Where(bytes => bytes <= _policy.MaximumReadBytes).DefaultIfEmpty(0).Min();
            foreach (var candidate in _sizes.Values.Where(g => g.Count > 1).SelectMany(g => g).Where(c => !c.Hashed && !c.Suppressed))
            {
                budget.Check();
                if (candidate.File.Revision.Length > _policy.MaximumReadBytes / 2)
                { candidate.Suppressed = true; Issue(candidate.File.Path, "Raise the content-read limit to fully compare this file pair."); continue; }
                if (candidate.File.Revision.Length > Math.Max(0, budget.Remaining - comparisonReserve)) continue;
                try
                {
                    using var file = StorageFile.Open(candidate.File.Path); file.Verify(candidate.File.Revision);
                    string digest = DuplicateFinder.Hash(file, budget);
                    candidate.File = new(candidate.File.Path, candidate.File.Revision, digest); candidate.Hashed = true;
                    var key = (file.Revision.Length, digest);
                    if (!_hashes.TryGetValue(key, out var hash)) _hashes.Add(key, hash = []);
                    hash.Add(candidate.File);
                    if (hash.Count > 1)
                    {
                        long required = file.Revision.Length * 2;
                        comparisonReserve = comparisonReserve == 0 ? required : Math.Min(comparisonReserve, required);
                    }
                    progress?.Report(new("Hashing files", examined, budget.Bytes));
                }
                catch (Exception error) when (DuplicateFinder.Expected(error)) { candidate.Suppressed = true; Issue(candidate.File.Path, error.Message); }
            }
            foreach (var bucket in _hashes.Where(pair => pair.Value.Count > 1))
            {
                budget.Check();
                if (!_verified.TryGetValue(bucket.Key, out var verified)) _verified.Add(bucket.Key, verified = []);
                foreach (var candidate in bucket.Value.Where(f => !_objects[f.Revision.ObjectKey].Suppressed
                    && !verified.Any(v => v.Revision.ObjectKey == f.Revision.ObjectKey)))
                {
                    budget.Check();
                    if (candidate.Revision.Length > _policy.MaximumReadBytes / 2)
                    { _objects[candidate.Revision.ObjectKey].Suppressed = true; Issue(candidate.Path, "Raise the content-read limit to byte-compare this file pair."); continue; }
                    if (candidate.Revision.Length > budget.Remaining / 2) break;
                    DuplicateFile? keeper = null; StorageFile? first = null;
                    while (first is null)
                    {
                        keeper = verified.FirstOrDefault(f => !_objects[f.Revision.ObjectKey].Suppressed)
                            ?? bucket.Value.FirstOrDefault(f => f.Revision.ObjectKey != candidate.Revision.ObjectKey && !_objects[f.Revision.ObjectKey].Suppressed);
                        if (keeper is null) break;
                        try { first = StorageFile.Open(keeper.Path); first.Verify(keeper.Revision); }
                        catch (Exception error) when (DuplicateFinder.Expected(error))
                        {
                            first?.Dispose(); first = null; _objects[keeper.Revision.ObjectKey].Suppressed = true;
                            verified.RemoveAll(f => f.Revision.ObjectKey == keeper.Revision.ObjectKey); Issue(keeper.Path, error.Message);
                        }
                    }
                    if (first is null || keeper is null) continue;
                    try
                    {
                        using var keeperLease = first;
                        using var second = StorageFile.Open(candidate.Path); second.Verify(candidate.Revision);
                        if (!DuplicateFinder.Equal(first, second, budget)) { Issue(candidate.Path, "Contents differ despite matching hashes."); continue; }
                        if (!verified.Any(v => v.Revision.ObjectKey == keeper.Revision.ObjectKey)) verified.Add(keeper);
                        verified.Add(candidate);
                    }
                    catch (Exception error) when (DuplicateFinder.Expected(error))
                    {
                        _objects[candidate.Revision.ObjectKey].Suppressed = true;
                        verified.RemoveAll(f => f.Revision.ObjectKey == candidate.Revision.ObjectKey);
                        Issue(candidate.Path, error.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (DuplicateFinder.BudgetReachedException) { }
        bool hashWork = _sizes.Values.Where(g => g.Count > 1).SelectMany(g => g).Any(c => !c.Hashed && !c.Suppressed);
        bool comparisonWork = _hashes.Where(g => g.Value.Count(f => !_objects[f.Revision.ObjectKey].Suppressed) > 1).Any(g => g.Value.Any(f => !_objects[f.Revision.ObjectKey].Suppressed
            && (!_verified.TryGetValue(g.Key, out var completed) || !completed.Any(v => v.Revision.ObjectKey == f.Revision.ObjectKey))));
        CanContinue = !_capacityReached && (!_inputComplete || hashWork || comparisonWork);
        bool complete = !CanContinue && !_capacityReached && !_unknown && _policy.StartRecord == 0;
        string note = token.IsCancellationRequested ? "Cancelled · Completed comparisons retained; continue to resume."
            : _capacityReached ? $"Retained-object limit ({_policy.MaximumRetainedFiles:N0}) reached. Choose a smaller scanned location; coverage is incomplete."
            : CanContinue ? "Bounded page · Continue to compare the remaining candidates; earlier revisions and hashes are retained."
            : !complete ? "Comparison finished with unavailable or unverified content; coverage remains incomplete."
            : "Comparison complete for this scan. Physical savings remain separate from redundant content.";
        var groups = _verified.Where(g => g.Value.Count > 1).Select(g => new DuplicateGroup(g.Key.Hash, g.Key.Bytes, Array.AsReadOnly(g.Value.ToArray())))
            .OrderByDescending(g => g.RedundantLogicalBytes).ToArray();
        return new(groups, issues.AsReadOnly(), examined, budget.Bytes, _aliases, complete, token.IsCancellationRequested,
            CanContinue ? _cursor : -1, note);

        void Issue(string path, string reason) { _unknown = true; if (issues.Count < 500) issues.Add(new(path, reason)); }
    }

    public void Dispose() { _disposed = true; _shutdown.Cancel(); }
}
