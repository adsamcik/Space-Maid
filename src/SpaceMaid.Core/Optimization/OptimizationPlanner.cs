using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Optimization;

public static class OptimizationPlanner
{
    public static OptimizationPreview Create(ScanSnapshot scan, IReadOnlyList<Opportunity> findings, OptimizationPolicy policy,
        CancellationToken token = default, DateTime? nowUtc = null)
    {
        policy = policy.Validate();
        var files = new List<OptimizationFile>(); var skipped = new List<OptimizationSkipped>();
        if (policy.ScopePath.Length == 0) return new(files.AsReadOnly(), skipped.AsReadOnly(), 0, !scan.HasAllocationData, false);
        var enabled = policy.EnabledRules.ToHashSet(StringComparer.Ordinal);
        // Deleting one batch updates directory timestamps. File/source activity
        // still protects builds without making our own cleanup block the next batch.
        var assessments = BuildCleanupProtection.Assess(scan, findings, new(policy.KeepRecentDays), token, nowUtc, includeDirectoryActivity: false);
        var protectedPaths = findings.Where(f => f.IsWorktree || f.RequiresManualReview || f.CleanupBlockReason.Length > 0
            || assessments[f.DirectoryIndex].State != CleanupReadiness.Ready).Select(f => f.Path).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0, usedBudget = 0; bool limited = false;
        DateTime now = nowUtc ?? DateTime.UtcNow;
        foreach (var location in findings.Where(f => f.AutomaticCleanup is not null).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var rule = location.AutomaticCleanup!;
            string key = rule.ConsentKey(location.ProviderId);
            if (!enabled.Contains(key) || !OptimizationPolicy.Covers(policy.ScopePath, location.Path)) continue;
            if (location.RequiresManualReview || location.IsWorktree || location.CleanupBlockReason.Length > 0
                || assessments[location.DirectoryIndex].State != CleanupReadiness.Ready)
            { skipped.Add(new(location.Path, "Requires manual review or has recent activity.")); continue; }
            int link = scan.PrimaryLink(location.DirectoryIndex);
            if (scan.Entry(link).IsIncomplete)
            { skipped.Add(new(location.Path, "Folder coverage is incomplete.")); continue; }
            long cutoff = now.AddDays(-Math.Max(policy.KeepRecentDays, rule.MinimumAgeDays)).ToFileTimeUtc();
            var pending = new Stack<int>(); pending.Push(location.DirectoryIndex);
            var visited = new HashSet<int>();
            while (pending.TryPop(out int directory))
            {
                token.ThrowIfCancellationRequested();
                if (!visited.Add(directory)) continue;
                string? directoryPath = scan.PathForFile(directory);
                if (directoryPath is null || Blocked(directoryPath, policy, protectedPaths)
                    || (scan.Records[directory].Attributes & UnsafeAttributes) != 0 || (scan.EntryFlags[directory] & 6) != 0) continue;
                foreach (int childLink in scan.Children(directory))
                {
                    token.ThrowIfCancellationRequested();
                    var file = scan.Records[scan.Links[childLink].FileIndex];
                    if (file.IsDirectory) { pending.Push(scan.Links[childLink].FileIndex); continue; }
                    string name = scan.Name(childLink).ToString();
                    if (!rule.Matches(name) || (file.Attributes & UnsafeAttributes) != 0 || file.LastWriteFileTime <= 0
                        || file.CreationFileTime <= 0 || Math.Max(file.LastWriteFileTime, file.CreationFileTime) >= cutoff) continue;
                    string? path = scan.EntryPath(childLink);
                    if (path is null || Blocked(path, policy, protectedPaths) || seen.Contains(path)) continue;
                    string? requiredSource = rule.RequiredSourcePath(path, location.Path);
                    if (rule.RequiresPythonSource && requiredSource is null) continue;
                    if (requiredSource is { } source)
                    {
                        int? sourceLink = scan.FindEntry(source);
                        if (sourceLink is null || scan.Entry(sourceLink.Value).IsDirectory
                            || (scan.Records[scan.Entry(sourceLink.Value).FileIndex].Attributes & UnsafeAttributes) != 0) continue;
                    }
                    long estimate = scan.HasAllocationData ? file.AllocatedBytes : file.LogicalBytes;
                    long budget = Math.Max(estimate, file.LogicalBytes);
                    if (files.Count >= policy.MaxFilesPerRun)
                        return new(files.AsReadOnly(), skipped.AsReadOnly(), bytes, !scan.HasAllocationData, true);
                    if (budget > policy.MaxBytesPerRun - usedBudget)
                    { limited = true; continue; }
                    seen.Add(path);
                    files.Add(new(path, location.Path, key, file.LogicalBytes, estimate, file.LastWriteFileTime, file.CreationFileTime,
                        scan.HasAllocationData ? file.Identity : null, rule));
                    bytes = checked(bytes + estimate); usedBudget += budget;
                }
            }
        }
        return new(files.AsReadOnly(), skipped.AsReadOnly(), bytes, !scan.HasAllocationData, limited);
    }

    internal const FileAttributes UnsafeAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.System
        | FileAttributes.ReadOnly | FileAttributes.Encrypted | (FileAttributes)(0x40000 | 0x400000);

    private static bool Blocked(string path, OptimizationPolicy policy, IReadOnlyList<string> protectedPaths)
    {
        if (policy.ExcludedPaths.Any(p => OptimizationPolicy.Covers(p, path)) || protectedPaths.Any(p => OptimizationPolicy.Covers(p, path))) return true;
        return path.Split(['\\', '/']).Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || p.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || p.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase));
    }
}
