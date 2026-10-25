using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App.Extensions;

// Extensions classify the indexed metadata. The host assigns ownership, paths,
// sizes and membership; cleanup never trusts plugin-supplied deletion targets.
internal sealed class OpportunityCatalog(Action<string, object>? diagnostic = null, CancellationToken lifetime = default) : IAsyncDisposable
{
    private readonly Dictionary<string, IOpportunityExtension> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<ScanSnapshot, Result> _results = new();
    private readonly SemaphoreSlim _gate = new(1);
    private bool _stopped;
    private sealed record Result(OpportunityAnalysis Combined, Dictionary<string, OpportunityAnalysis> Parts, string[] Failed);

    internal void Register(string id, IOpportunityExtension provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!Regex.IsMatch(id, "^[a-z][a-z0-9.-]{2,63}$") || id.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Invalid opportunity extension ID.", nameof(id));
        _gate.Wait(lifetime);
        try { ObjectDisposedException.ThrowIf(_stopped, this); _providers.Add(id, provider); _results.Clear(); }
        finally { _gate.Release(); }
    }

    internal OpportunityAnalysis Analyze(ScanSnapshot scan, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        _gate.Wait(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_results.TryGetValue(scan, out var existing)) return existing.Combined;
            var inventory = new Inventory(scan);
            var parts = new Dictionary<string, OpportunityAnalysis>(StringComparer.OrdinalIgnoreCase);
            var failed = new List<string>();
            foreach (var (id, provider) in _providers)
            {
                linked.Token.ThrowIfCancellationRequested();
                var timer = Stopwatch.StartNew();
                try
                {
                    var output = provider.Analyze(inventory, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    var findings = NormalizeFindings(scan, id, output.Findings, linked.Token);
                    var groups = NormalizeGroups(scan, id, output.Groups, findings, linked.Token, namespaced: false);
                    parts.Add(id, new(findings, groups));
                    diagnostic?.Invoke("opportunity-extension-complete", new { Owner = id, Findings = findings.Count, ElapsedMs = timer.Elapsed.TotalMilliseconds });
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    failed.Add(id);
                    diagnostic?.Invoke("opportunity-extension-failed", new { Owner = id, Error = error.ToString(), ElapsedMs = timer.Elapsed.TotalMilliseconds });
                }
            }
            var all = parts.Values.SelectMany(part => part.Findings).ToArray();
            var sets = parts.Select(part => NamespaceGroups(part.Key, part.Value.Groups)).ToArray();
            var combined = new OpportunityAnalysis(Array.AsReadOnly(all), new(
                Array.AsReadOnly(sets.SelectMany(set => set.Projects).ToArray()),
                Array.AsReadOnly(sets.SelectMany(set => set.Types).ToArray()),
                Cleanup: Array.AsReadOnly(sets.SelectMany(set => set.Cleanup ?? []).ToArray())));
            _results.Add(scan, new(combined, parts, failed.ToArray()));
            return combined;
        }
        finally { _gate.Release(); }
    }

    internal IReadOnlyList<Opportunity> Find(ScanSnapshot scan, CancellationToken token = default) => Analyze(scan, token).Findings;

    internal string Status(ScanSnapshot scan)
    {
        if (_results.TryGetValue(scan, out var result) && result.Failed.Length > 0)
            return $"{result.Failed.Length} cleanup {(result.Failed.Length == 1 ? "extension" : "extensions")} unavailable · Open log";
        return _providers.Count == 0 ? "No cleanup extensions enabled · Check Extensions" : "";
    }

    internal IReadOnlyList<OpportunityGroup> BuildCleanupGroups(ScanSnapshot scan, IReadOnlyList<Opportunity> locations,
        OpportunityGroupSet groups, OpportunityGroupContext context, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        _gate.Wait(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            _results.TryGetValue(scan, out var cached);
            var result = new List<OpportunityGroup>();
            foreach (var (id, provider) in _providers)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (cached is not null && !cached.Parts.ContainsKey(id)) continue;
                try
                {
                    // Publishing an externally prepared inventory (e.g. a recheck)
                    // also uses its supplied findings; never discover on the UI thread.
                    var findings = locations.Where(item => item.ProviderId == id).ToArray();
                    if (findings.Length == 0) continue;
                    var input = cached?.Parts.GetValueOrDefault(id) ?? new OpportunityAnalysis(findings, groups);
                    var output = provider.BuildCleanupGroups(new Inventory(scan), input with { Findings = findings }, context, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    result.AddRange(NormalizeList(scan, id, output, findings, linked.Token, namespaced: true));
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    if (cached is not null && !cached.Failed.Contains(id))
                    {
                        cached = cached with { Failed = [.. cached.Failed, id] };
                        _results.Remove(scan); _results.Add(scan, cached);
                    }
                    diagnostic?.Invoke("opportunity-extension-grouping-failed", new { Owner = id, Error = error.ToString() });
                }
            }
            return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    private static IReadOnlyList<Opportunity> NormalizeFindings(ScanSnapshot scan, string id, IReadOnlyList<Opportunity> source, CancellationToken token)
    {
        if (source.Count > scan.FileCount) throw new InvalidDataException("Too many opportunity findings.");
        IOpportunityInventory inventory = scan;
        var directories = new HashSet<int>(); var result = new List<Opportunity>(source.Count);
        foreach (var item in source)
        {
            token.ThrowIfCancellationRequested();
            if (item is null || (uint)item.DirectoryIndex >= (uint)scan.FileCount
                || !inventory.IsDirectory(item.DirectoryIndex) || !directories.Add(item.DirectoryIndex)
                || inventory.PathForFile(item.DirectoryIndex) is not { } path)
                throw new InvalidDataException("An opportunity must reference one resolved scanned folder.");
            Text(item.Title, 256); Text(item.Category, 256); Text(item.RuleId, 256, allowEmpty: true);
            Text(item.Reason, 4096); Text(item.Caution, 4096); Text(item.SuggestedAction, 4096);
            Text(item.CleanupBlockReason, 4096, allowEmpty: true);
            if (!Enum.IsDefined(item.Appearance)) throw new InvalidDataException("Invalid opportunity appearance.");
            result.Add(item with { ProviderId = id, RuleId = item.RuleId.Length == 0 ? item.Title : item.RuleId,
                Path = path, LogicalBytes = inventory.LogicalSize(item.DirectoryIndex), AllocatedBytes = inventory.AllocatedSize(item.DirectoryIndex) });
        }
        return result.AsReadOnly();
    }

    private static OpportunityGroupSet NormalizeGroups(ScanSnapshot scan, string id, OpportunityGroupSet source,
        IReadOnlyList<Opportunity> findings, CancellationToken token, bool namespaced) => new(
            NormalizeList(scan, id, source.Projects, findings, token, namespaced),
            NormalizeList(scan, id, source.Types, findings, token, namespaced),
            source.BuildLanguages is null ? null : NormalizeList(scan, id, source.BuildLanguages, findings, token, namespaced),
            source.CleanupBreakdowns?.ToDictionary(pair => Key(id, pair.Key, namespaced),
                pair => NormalizeList(scan, id, pair.Value, findings, token, namespaced), StringComparer.Ordinal),
            source.Cleanup is null ? null : NormalizeList(scan, id, source.Cleanup, findings, token, namespaced));

    private static IReadOnlyList<OpportunityGroup> NormalizeList(ScanSnapshot scan, string id, IReadOnlyList<OpportunityGroup> source,
        IReadOnlyList<Opportunity> findings, CancellationToken token, bool namespaced)
    {
        var accepted = findings.ToDictionary(item => item.DirectoryIndex);
        IOpportunityInventory inventory = scan;
        int budget = 65_536;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return Normalize(source, null, 0);
        IReadOnlyList<OpportunityGroup> Normalize(IReadOnlyList<OpportunityGroup> input, HashSet<int>? parent, int depth)
        {
            if (depth > 8) throw new InvalidDataException("Opportunity groups are nested too deeply.");
            var result = new List<OpportunityGroup>();
            foreach (var group in input)
            {
                token.ThrowIfCancellationRequested();
                if (--budget < 0 || group is null || !keys.Add(group.Key)) throw new InvalidDataException("Invalid or duplicate opportunity group.");
                Text(group.Key, 32768); Text(group.Name, 256); Text(group.Kind, 256, true);
                Text(group.Summary, 4096, true); Text(group.Caption, 4096, true); Text(group.StatusLabel, 256, true);
                Text(group.Glyph, 8); Text(group.BreakdownHeading, 256, true);
                if (!Enum.IsDefined(group.Role)) throw new InvalidDataException("Invalid opportunity group role.");
                var members = new Dictionary<int, Opportunity>();
                foreach (var member in group.Locations)
                {
                    token.ThrowIfCancellationRequested();
                    if (!accepted.TryGetValue(member.DirectoryIndex, out var actual) || parent?.Contains(member.DirectoryIndex) == false
                        || actual.Title != member.Title || actual.Category != member.Category
                        || actual.RuleId != (member.RuleId.Length == 0 ? member.Title : member.RuleId))
                        throw new InvalidDataException("A group contains a finding outside its extension or parent group.");
                    members.TryAdd(actual.DirectoryIndex, actual);
                }
                if (members.Count == 0) continue;
                var roots = members.Keys.ToHashSet(); long bytes = 0;
                foreach (var member in members.Values)
                {
                    int directory = member.DirectoryIndex, remaining = scan.FileCount; bool nested = false;
                    while (directory != scan.RootIndex && remaining-- > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        int link = scan.PrimaryLink(directory);
                        if (link < 0 || (directory = inventory.LinkParent(link)) < 0) break;
                        if (roots.Contains(directory)) { nested = true; break; }
                    }
                    if (!nested) bytes = checked(bytes + member.DisplayBytes(scan.HasAllocationData));
                }
                result.Add(group with { Key = Key(id, group.Key, namespaced), ProviderId = id,
                    Locations = Array.AsReadOnly(members.Values.ToArray()), ObservedBytes = bytes,
                    Children = group.Children is null ? null : Normalize(group.Children, roots, depth + 1) });
            }
            return result.AsReadOnly();
        }
    }

    private static OpportunityGroupSet NamespaceGroups(string id, OpportunityGroupSet groups) => new(
        MapGroups(id, groups.Projects), MapGroups(id, groups.Types), Cleanup: MapGroups(id, groups.Cleanup ?? []));
        // Namespace only at the host boundary; a provider sees its own stable keys.
    private static string Key(string id, string key, bool namespaced)
    {
        if (!namespaced || id == "space-maid.developer") return key;
        return key.StartsWith("cleanup:", StringComparison.Ordinal) ? "cleanup:" + id + "/" + key[8..]
            : id + "/" + key;
    }
    private static IReadOnlyList<OpportunityGroup> MapGroups(string id, IReadOnlyList<OpportunityGroup> groups) =>
        Array.AsReadOnly(groups.Select(group => group with { Key = Key(id, group.Key, true),
            Children = group.Children is null ? null : MapGroups(id, group.Children) }).ToArray());
    private static void Text(string? value, int maximum, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new InvalidDataException("Invalid opportunity label.");
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _stopped = true; _providers.Clear(); _results.Clear(); }
        finally { _gate.Release(); }
    }

    // No mutable ScanSnapshot is passed across the extension boundary.
    private sealed class Inventory(ScanSnapshot scan) : IOpportunityInventory
    {
        private IOpportunityInventory Source => scan;
        public string RootPath => scan.RootPath;
        public int RootIndex => scan.RootIndex;
        public int FileCount => scan.FileCount;
        public bool HasAllocationData => scan.HasAllocationData;
        public bool IsDirectory(int index) => Source.IsDirectory(index);
        public int PrimaryLink(int index) => scan.PrimaryLink(index);
        public int LinkObject(int index) => Source.LinkObject(index);
        public int LinkParent(int index) => Source.LinkParent(index);
        public ReadOnlySpan<char> Name(int index) => Source.Name(index);
        public IEnumerable<int> Children(int index) => Source.Children(index);
        public string? PathForFile(int index) => Source.PathForFile(index);
        public long LogicalSize(int index) => Source.LogicalSize(index);
        public long AllocatedSize(int index) => Source.AllocatedSize(index);
    }
}
