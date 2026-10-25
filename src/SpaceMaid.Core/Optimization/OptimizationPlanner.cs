using System.Diagnostics;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Optimization;

public static class OptimizationPlanner
{
    public static OptimizationPreview Create(ScanSnapshot scan, IReadOnlyList<Opportunity> findings, OptimizationPolicy policy,
        CancellationToken token = default, DateTime? nowUtc = null, int cursor = 0, IReadOnlyList<OptimizationCooldown>? cooldowns = null)
    {
        policy = policy.Validate();
        var files = new List<OptimizationFile>(); var skipped = new List<OptimizationSkipped>();
        int examined = 0, next = scan.FileCount == 0 ? 0 : Math.Max(0, cursor) % scan.FileCount;
        long bytes = 0, mutationBudget = 0; bool limited = false;
        if (policy.ScopePath.Length == 0 || scan.FileCount == 0) return Result(true);
        DateTime now = nowUtc ?? DateTime.UtcNow;
        var enabled = policy.EnabledRules.ToHashSet(StringComparer.Ordinal);
        var locations = findings.Where(f => f.AutomaticCleanup is { } rule && enabled.Contains(rule.ConsentKey(f.ProviderId))
                && OptimizationPolicy.Covers(policy.ScopePath, f.Path)).GroupBy(f => f.DirectoryIndex)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.ProviderId, StringComparer.Ordinal).ToArray());
        if (locations.Count == 0) return Result(true);
        // Checkout retirement and exact generated-file eviction are separate actions.
        var toolManaged = findings.Where(f => f.CleanupBlockReason.Length > 0).Select(f => f.Path).ToArray();
        var seen = new HashSet<int>(); var watch = Stopwatch.StartNew(); int index = next;
        for (int visited = 0; visited < scan.FileCount; visited++, index = (index + 1) % scan.FileCount)
        {
            token.ThrowIfCancellationRequested();
            if (examined >= policy.MaxEntriesPerRun || watch.Elapsed.TotalSeconds >= policy.MaxInspectionSeconds) return Result(false);
            examined++; next = (index + 1) % scan.FileCount;
            var record = scan.Records[index];
            if (record.IsDirectory || (record.Attributes & UnsafeAttributes) != 0 || record.LastWriteFileTime <= 0 || record.CreationFileTime <= 0) continue;
            int fileLink = scan.PrimaryLink(index);
            if (fileLink < 0 || !scan.Entry(fileLink).IsPrimaryName) continue;
            string name = scan.Name(fileLink).ToString(); int directory = scan.Links[fileLink].ParentIndex, depth = 0;
            Opportunity? location = null;
            while (directory >= 0 && depth++ < 512)
            {
                if (locations.TryGetValue(directory, out var choices))
                { location = choices.FirstOrDefault(f => f.AutomaticCleanup!.Matches(name)); if (location is not null) break; }
                int parentLink = scan.PrimaryLink(directory); directory = parentLink < 0 ? -1 : scan.Links[parentLink].ParentIndex;
            }
            if (location is null || !seen.Add(index)) continue;
            var rule = location.AutomaticCleanup!;
            if (location.IsWorktree || location.RequiresManualReview || location.CleanupBlockReason.Length > 0 || rule.ResourceKind == GeneratedResourceKind.Unverified)
            { if (skipped.Count < 128) skipped.Add(new(location.Path, "This action needs producer evidence or manual review.")); continue; }
            if (scan.Entry(scan.PrimaryLink(location.DirectoryIndex)).IsIncomplete) continue;
            long cutoff = now.AddDays(-Math.Max(policy.KeepRecentDays, rule.MinimumAgeDays)).ToFileTimeUtc();
            if (Math.Max(record.LastWriteFileTime, record.CreationFileTime) >= cutoff) continue;
            string? path = scan.EntryPath(fileLink);
            if (path is null || Blocked(path)) continue;
            if (cooldowns?.Any(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && c.Written == record.LastWriteFileTime
                && c.Created == record.CreationFileTime && c.RetryAfterUtc > now) == true) continue;
            string? source = rule.RequiredSourcePath(path, location.Path);
            if (rule.RequiresPythonSource && (source is null || scan.FindEntry(source) is not { } sourceLink || scan.Entry(sourceLink).IsDirectory)) continue;
            if (rule.RequiresPythonSource && source is not null && scan.FindEntry(source) is { } knownSource)
            {
                var input = scan.Records[scan.Entry(knownSource).FileIndex];
                if ((input.Attributes & UnsafeAttributes) != 0 || input.LastWriteFileTime <= 0 || input.CreationFileTime <= 0
                    || Math.Max(input.LastWriteFileTime, input.CreationFileTime) >= cutoff) continue;
            }
            long estimate = scan.HasAllocationData ? record.AllocatedBytes : record.LogicalBytes; long cost = Math.Max(estimate, record.LogicalBytes);
            if (cost < 0 || cost > policy.MaxBytesPerRun - mutationBudget) { limited = true; continue; }
            files.Add(new(path, location.Path, rule.ConsentKey(location.ProviderId), record.LogicalBytes, estimate,
                record.LastWriteFileTime, record.CreationFileTime, scan.HasAllocationData ? record.Identity : null, rule)
                { ProviderId = location.ProviderId, ScanRecordIndex = index });
            bytes = checked(bytes + estimate); mutationBudget += cost;
            if (files.Count >= policy.MaxFilesPerRun) return Result(false);
        }
        return Result(true);

        bool Blocked(string path) => policy.ExcludedPaths.Any(p => OptimizationPolicy.Covers(p, path)) || toolManaged.Any(p => OptimizationPolicy.Covers(p, path))
            || path.Split(['\\', '/']).Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase) || p.StartsWith('$') || p.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase));
        OptimizationPreview Result(bool complete) => new(files.AsReadOnly(), skipped.AsReadOnly(), bytes, !scan.HasAllocationData, limited || !complete)
        { ExaminedEntries = examined, NextCursor = next, CoverageComplete = complete };
    }

    internal const FileAttributes UnsafeAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.System
        | FileAttributes.ReadOnly | FileAttributes.Encrypted | (FileAttributes)(0x40000 | 0x400000);
}
