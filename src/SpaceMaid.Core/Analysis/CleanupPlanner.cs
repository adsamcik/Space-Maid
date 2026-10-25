using System.ComponentModel;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

public sealed record CleanupPreview
{
    public IReadOnlyList<Opportunity> Locations { get; }
    public long EstimatedBytes { get; }
    public long ExcludedSharedBytes { get; }
    public int FileCount { get; }
    public bool UsesLogicalSize { get; }
    public IReadOnlyList<string> Cautions { get; }

    public CleanupPreview(IReadOnlyList<Opportunity> locations, long estimatedBytes, long excludedSharedBytes,
        int fileCount, bool usesLogicalSize, IReadOnlyList<string> cautions)
    {
        Locations = Array.AsReadOnly(locations.ToArray()); EstimatedBytes = estimatedBytes;
        ExcludedSharedBytes = excludedSharedBytes; FileCount = fileCount; UsesLogicalSize = usesLogicalSize;
        Cautions = Array.AsReadOnly(cautions.ToArray());
    }
}

public static class CleanupPlanner
{
    public static CleanupPlan Prepare(ScanSnapshot scan, IEnumerable<Opportunity> selected, CancellationToken token = default,
        IReadOnlyDictionary<string, string>? blockedPaths = null, OpportunityDiscovery? discover = null, BuildCleanupPolicy? buildPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(scan); ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(discover);
        token.ThrowIfCancellationRequested();
        var actual = new Dictionary<(int Directory, string Provider, string Rule, string Title, string Category, string Path), Opportunity>();
        foreach (var finding in discover(scan, token))
        {
            token.ThrowIfCancellationRequested();
            actual.TryAdd((finding.DirectoryIndex, finding.ProviderId, finding.RuleId, finding.Title, finding.Category, finding.Path), finding);
        }
        var policy = buildPolicy ?? BuildCleanupPolicy.Default;
        var assessments = BuildCleanupProtection.Assess(scan, actual.Values.ToArray(), policy, token);
        var protectedDirectories = new Dictionary<int, string>();
        foreach (var finding in actual.Values)
        {
            if (assessments[finding.DirectoryIndex].State != CleanupReadiness.Protected) continue;
            int directory = finding.DirectoryIndex, remaining = scan.FileCount;
            protectedDirectories.TryAdd(directory, assessments[directory].Reason);
            while (directory != scan.RootIndex && remaining-- > 0)
            {
                token.ThrowIfCancellationRequested();
                int link = scan.PrimaryLink(directory);
                if (link < 0 || (directory = scan.Links[link].ParentIndex) < 0) break;
                protectedDirectories.TryAdd(directory, finding.CleanupBlockReason.Length > 0
                    ? "Contains storage managed by its owning tool · Kept" : "Contains protected build files · Kept");
            }
        }
        var valid = new List<Opportunity>(); var blocked = new List<CleanupBlockedLocation>();
        var originals = new Dictionary<int, Opportunity>();
        foreach (var choice in selected)
        {
            token.ThrowIfCancellationRequested();
            if (blockedPaths?.TryGetValue(choice.Path, out string? reason) == true)
            { blocked.Add(new(choice, reason)); continue; }
            if (protectedDirectories.TryGetValue(choice.DirectoryIndex, out string? protection))
            { blocked.Add(new(choice, protection)); continue; }
            if (actual.TryGetValue((choice.DirectoryIndex, choice.ProviderId, choice.RuleId, choice.Title, choice.Category, choice.Path), out var finding))
            { valid.Add(finding); originals.TryAdd(choice.DirectoryIndex, choice); }
            else blocked.Add(new(choice, "This location does not match an opportunity detected in the current inventory. Recheck it before cleanup."));
        }
        var collapsed = Create(scan, valid, token);
        var targets = new List<FolderDeletionTarget>(); var eligible = new List<Opportunity>();
        foreach (var location in collapsed.Locations)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                targets.Add(FolderDeletionTarget.FromScan(scan, scan.PrimaryLink(location.DirectoryIndex)));
                eligible.Add(location);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException
                or UnauthorizedAccessException or Win32Exception or NotSupportedException)
            { blocked.Add(new(originals[location.DirectoryIndex], ex.Message)); }
        }
        token.ThrowIfCancellationRequested();
        var preview = eligible.Count == collapsed.Locations.Count ? collapsed : Create(scan, eligible, token);
        return new(scan, preview, targets, blocked, policy, assessments);
    }

    // A review can narrow consent, never add a target or recapture a replacement.
    public static CleanupPlan Restrict(CleanupPlan plan, IEnumerable<Opportunity> selected, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(selected);
        var allowed = plan.Preview.Locations.Select((item, index) => (Item: item, Target: plan.Targets[index]))
            .ToDictionary(pair => (pair.Item.ProviderId, pair.Item.Path, pair.Item.RuleId));
        var keys = new HashSet<(string, string, string)>();
        foreach (var item in selected)
        {
            token.ThrowIfCancellationRequested();
            var key = (item.ProviderId, item.Path, item.RuleId);
            if (!allowed.TryGetValue(key, out var pair)) throw new ArgumentException("Review can only keep previously checked folders.", nameof(selected));
            keys.Add(key);
        }
        // Preserve original execution order and exact captured native targets.
        var ordered = plan.Preview.Locations.Where(item => keys.Contains((item.ProviderId, item.Path, item.RuleId))).ToArray();
        return new(plan.Snapshot, Create(plan.Snapshot, ordered, token),
            ordered.Select(item => allowed[(item.ProviderId, item.Path, item.RuleId)].Target),
            plan.BlockedLocations, plan.BuildPolicy, plan.BuildAssessments);
    }

    public static CleanupPreview Create(ScanSnapshot scan, IEnumerable<Opportunity> selected,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scan); ArgumentNullException.ThrowIfNull(selected);
        token.ThrowIfCancellationRequested();
        var roots = selected.DistinctBy(o => o.DirectoryIndex).ToArray();
        var rootSet = roots.Select(o => o.DirectoryIndex).ToHashSet();
        foreach (var root in roots)
            if (root.DirectoryIndex < 0 || root.DirectoryIndex >= scan.FileCount || !scan.Records[root.DirectoryIndex].IsDirectory
                || !string.Equals(scan.PathForFile(root.DirectoryIndex), root.Path, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A selected location does not belong to this scan.", nameof(selected));
        // Nested selections are already covered by their selected ancestor.
        roots = roots.Where(root => !HasSelectedAncestor(scan, root.DirectoryIndex, rootSet)).ToArray();
        bool[] includedDirectories = new bool[scan.FileCount];
        var pending = new Stack<int>(roots.Select(o => o.DirectoryIndex));
        while (pending.TryPop(out int directory))
        {
            token.ThrowIfCancellationRequested();
            if (includedDirectories[directory]) continue;
            includedDirectories[directory] = true;
            foreach (int link in scan.Children(directory))
            {
                int file = scan.Links[link].FileIndex;
                if (scan.Records[file].IsDirectory) pending.Push(file);
            }
        }
        int[] totalLinks = new int[scan.FileCount], selectedLinks = new int[scan.FileCount];
        for (int i = 0; i < scan.Links.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var link = scan.Links[i];
            totalLinks[link.FileIndex]++;
            if (link.ParentIndex >= 0 && includedDirectories[link.ParentIndex]) selectedLinks[link.FileIndex]++;
        }
        long estimate = 0, shared = 0;
        int count = 0;
        bool specialStorage = false;
        for (int i = 0; i < scan.FileCount; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var file = scan.Records[i];
            if (file.IsDirectory ? !includedDirectories[i] : selectedLinks[i] == 0) continue;
            long bytes = scan.HasAllocationData ? file.AllocatedBytes : file.LogicalBytes;
            if (!file.IsDirectory && selectedLinks[i] < totalLinks[i]) shared = checked(shared + bytes);
            else estimate = checked(estimate + bytes);
            if (!file.IsDirectory) count++;
            specialStorage |= (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0;
        }
        var cautions = new List<string>
        {
            "Review the exact locations and their current contents. Folders are moved to the Recycle Bin only after confirmation.",
            "Recycled files remain recoverable and keep occupying drive space until the Recycle Bin is emptied.",
            "Stop active builds and tools first. Restore dependencies or rebuild generated outputs when needed; preserve any files you cannot recreate."
        };
        if (!scan.HasAllocationData) cautions.Add("Logical size is shown; physical space that could be freed is unknown.");
        if (!scan.HasCompleteVolumeLinks) cautions.Add("Hard links outside the scanned scope may reduce the estimate. This is not a guaranteed saving.");
        if (!scan.IncludesAllDataStreams) cautions.Add("Standard scanning does not include all alternate data streams.");
        if (shared > 0) cautions.Add("Allocation shared with unselected names has been excluded from the estimate.");
        if (specialStorage) cautions.Add("Cloud/provider-backed files need separate review; their backing storage may be shared or managed elsewhere.");
        if (scan.Warnings.Count > 0) cautions.Add("The scan has omissions. Review its coverage warnings.");
        cautions.Add("This is a live scan, not a snapshot. Sizes and files can change after scanning.");
        return new(roots, estimate, shared, count, !scan.HasAllocationData, cautions);
    }

    private static bool HasSelectedAncestor(ScanSnapshot scan, int directory, HashSet<int> selected)
    {
        int remaining = scan.FileCount;
        while (directory != scan.RootIndex && remaining-- > 0)
        {
            int link = scan.FirstLinks[directory];
            if (link < 0) return false;
            directory = scan.Links[link].ParentIndex;
            if (directory < 0) return false;
            if (selected.Contains(directory)) return true;
        }
        return false;
    }
}
