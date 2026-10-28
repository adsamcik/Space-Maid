using System.IO;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App.AgentAccess;

internal enum AgentAccessLevel { Summary, Groups, SuggestReview }

// Only these records cross the agent boundary. Never serialize scan, finding,
// evidence, exception, path, rule label or extension-provided prose here.
internal sealed record AgentCoverage(bool Complete, bool TraversalComplete, bool HasAllocationData,
    bool IncludesAllStreams, bool CompleteVolumeLinks, bool RetainsEarlierMetadata, int WarningCount,
    bool ClassificationsComplete);
internal sealed record AgentOverview(Guid SessionId, string Level, long FileNames, long Directories,
    long LogicalBytes, long? AllocatedBytes, AgentCoverage Coverage, string SizeMeaning);
internal sealed record AgentGroup(Guid Id, string ToolClass, string StorageClass, string Policy,
    int Locations, long LogicalBytes, long? AllocatedBytes, bool HasSharedNames, bool Complete,
    bool CanSuggestReview);
internal sealed record AgentSuggestion(Guid Id, IReadOnlyList<Guid> Groups, string AgentNote);

internal sealed class AgentAggregateSession : IDisposable
{
    private readonly Func<bool> _isCurrent;
    private readonly Dictionary<Guid, Opportunity[]> _members;
    private bool _disposed, _revoked;
    private int _suggestionCount;
    internal AgentOverview Overview { get; }
    internal IReadOnlyList<AgentGroup> Groups { get; }
    internal AgentSuggestion? Suggestion { get; private set; }
    internal AgentAccessLevel Level { get; }
    internal string Root { get; }
    internal IReadOnlyList<string> Exclusions { get; }

    private AgentAggregateSession(AgentOverview overview, IReadOnlyList<AgentGroup> groups,
        Dictionary<Guid, Opportunity[]> members, AgentAccessLevel level, string root,
        IReadOnlyList<string> exclusions, Func<bool> isCurrent)
    {
        Overview = overview; Groups = groups; _members = members; Level = level;
        Root = root; Exclusions = exclusions; _isCurrent = isCurrent;
    }

    internal static async Task<AgentAggregateSession> CreateAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> findings,
        string root, IReadOnlyList<string> exclusions, AgentAccessLevel level, bool classificationsComplete,
        Func<bool> isCurrent, CancellationToken token)
    {
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        // Reuse the scoped metadata traversal and its exclusions/work budget.
        // Its path-bearing output stays inside the host and is projected below.
        using var evidence = new InventoryEvidenceSession(() => scan, () => false, root, exclusions, token);
        var summary = await evidence.SummarizeAsync([evidence.Scope.Root.Id], token);
        var coverage = summary.Coverage;
        var overview = new AgentOverview(Guid.NewGuid(), level.ToString(), summary.Totals.FileNames,
            summary.Totals.Directories, summary.Totals.LogicalBytes, summary.Totals.AllocatedBytes,
            new(coverage.Complete, summary.Totals.TraversalComplete, coverage.HasAllocationData,
                coverage.IncludesAllStreams, coverage.CompleteVolumeLinks, coverage.RetainsEarlierMetadata,
                coverage.WarningCount, classificationsComplete),
            "Observed metadata only; recovery is unknown. Logical sizes may count aliases when physical identity is unavailable. Group sizes overlap across groups and must not be added.");
        var groups = new List<AgentGroup>(); var members = new Dictionary<Guid, Opportunity[]>();
        if (level != AgentAccessLevel.Summary)
        {
            int scope = scan.Entry(scan.FindEntry(root)!.Value).FileIndex;
            var excluded = evidence.Scope.ExcludedPaths;
            var selected = findings.Where(item => Inside(scan, item.DirectoryIndex, scope)
                // Exclude containing targets too: directory totals would disclose
                // excluded data and a later folder review would include it.
                && !excluded.Any(path => Covers(path, item.Path) || Covers(item.Path, path)))
                .DistinctBy(item => (item.ProviderId, item.DirectoryIndex, item.RuleId));
            foreach (var group in selected.GroupBy(item => (ToolClass(item.ProviderId), StorageClass(item), Policy(item))))
            {
                token.ThrowIfCancellationRequested();
                var items = group.ToArray(); var roots = items.Select(item => item.DirectoryIndex).ToHashSet();
                long logical = 0, allocated = 0; bool shared = false, complete = true;
                foreach (int directory in roots)
                {
                    var entry = scan.Entry(scan.PrimaryLink(directory));
                    shared |= scan.ContainsSharedFileNames(directory);
                    complete &= !entry.IsIncomplete && !scan.IsUnmeasuredDirectory(directory);
                    if (HasAncestor(scan, directory, roots)) continue;
                    logical = checked(logical + entry.LogicalBytes); allocated = checked(allocated + entry.AllocatedBytes);
                }
                var id = Guid.NewGuid();
                bool canSuggest = level == AgentAccessLevel.SuggestReview && overview.Coverage.Complete
                    && classificationsComplete && complete && group.Key.Item3 != "Protected";
                groups.Add(new(id, group.Key.Item1, group.Key.Item2, group.Key.Item3, roots.Count,
                    logical, scan.HasAllocationData ? allocated : null, shared, complete, canSuggest));
                members.Add(id, items);
            }
        }
        if (!isCurrent()) throw new InvalidOperationException("The scan changed. Open a new agent session.");
        return new(overview, groups.AsReadOnly(), members, level, evidence.Scope.Root.Path,
            evidence.Scope.ExcludedPaths, isCurrent);
    }

    internal void EnsureCurrent()
    {
        if (_disposed || _revoked || !_isCurrent())
        {
            _revoked = true; Suggestion = null;
            throw new InvalidOperationException("Agent access ended. Open a new session in Space Maid.");
        }
    }

    internal AgentSuggestion Suggest(IReadOnlyList<Guid> groups, string note)
    {
        EnsureCurrent();
        if (Level != AgentAccessLevel.SuggestReview || groups.Count is < 1 or > 8
            || groups.Distinct().Count() != groups.Count || note.Length is < 1 or > 500
            || note.Any(char.IsControl) || groups.Any(id => !Groups.Any(group => group.Id == id && group.CanSuggestReview)))
            throw new InvalidDataException("Choose 1–8 issued reviewable group IDs and a plain note of 1–500 characters.");
        if (Suggestion is not null || _suggestionCount >= 4)
            throw new InvalidDataException("A suggestion is pending or this session reached its four-suggestion limit.");
        _suggestionCount++;
        return Suggestion = new(Guid.NewGuid(), Array.AsReadOnly(groups.ToArray()), note);
    }

    // Human-only capability. Never registered as an agent tool.
    internal IReadOnlyList<Opportunity> TakeReview()
    {
        EnsureCurrent();
        var suggestion = Suggestion ?? throw new InvalidOperationException("There is no pending suggestion.");
        var result = suggestion.Groups.SelectMany(id => _members[id])
            .DistinctBy(item => (item.ProviderId, item.DirectoryIndex, item.RuleId)).ToArray();
        Suggestion = null;
        return result;
    }

    internal void Dismiss() { EnsureCurrent(); Suggestion = null; }
    public void Dispose() { _disposed = true; Suggestion = null; _members.Clear(); }

    private static string ToolClass(string provider) => provider switch
    {
        "space-maid.developer" => "Developer tools", "space-maid.agents" => "Agent tools",
        "space-maid.models" => "Model tools", "space-maid.containers" => "Containers",
        "space-maid.photos" => "Photos", _ => "Other tools"
    };
    private static string StorageClass(Opportunity item) => item.IsWorktree ? "Worktree"
        : item.CleanupBlockReason.Length > 0 ? "Tool-managed storage"
        : item.IsBuildOutput ? "Build output" : item.IsRegeneratable ? "Regenerable storage" : "Other storage";
    private static string Policy(Opportunity item) => item.CleanupBlockReason.Length > 0 ? "Protected"
        : item.RequiresManualReview || item.IsWorktree ? "Manual review" : "Live review required";
    private static bool Covers(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Inside(ScanSnapshot scan, int directory, int root)
    {
        for (int depth = 0; directory >= 0 && depth < 512; depth++)
        {
            if (directory == root) return true;
            if (directory == scan.RootIndex) return false;
            directory = scan.Entry(scan.PrimaryLink(directory)).ParentIndex;
        }
        return false;
    }
    private static bool HasAncestor(ScanSnapshot scan, int directory, HashSet<int> selected)
    {
        for (int depth = 0; directory != scan.RootIndex && depth < 512; depth++)
        {
            directory = scan.Entry(scan.PrimaryLink(directory)).ParentIndex;
            if (directory < 0) break;
            if (selected.Contains(directory)) return true;
        }
        return false;
    }
}
