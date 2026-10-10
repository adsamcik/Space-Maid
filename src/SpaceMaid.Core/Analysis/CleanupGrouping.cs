using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

// A small task-oriented checklist; project/path lists remain a drill-down.
public static class CleanupGrouping
{
    public static IReadOnlyList<OpportunityGroup> Build(ScanSnapshot scan, IReadOnlyList<Opportunity> locations,
        IReadOnlySet<string>? oldIntegratedWorktrees = null, CancellationToken token = default,
        WorktreeCleanupPolicy? worktreePolicy = null)
    {
        var policy = worktreePolicy ?? WorktreeCleanupPolicy.Default;
        var worktreeGroup = policy.Mode switch
        {
            WorktreeCleanupMode.Old => ("Old clean worktrees", "Checked locally · Branches retained", $"Clean checkouts with no observed file changes or Git activity for at least {policy.InactiveDays} days. Unmerged commits stay in their branches; cleanup includes ignored files."),
            WorktreeCleanupMode.Integrated => ("Worktrees already in upstream", "Checked locally · Any age", "Clean checkouts whose commits are contained in a locally cached upstream/default branch. Cleanup includes ignored files; branches are kept."),
            _ => ("Old integrated worktrees", "Checked locally · Review ignored files", $"Clean checkouts already in a local upstream/default branch, with no observed file changes or Git activity for at least {policy.InactiveDays} days. Cleanup includes ignored files.")
        };
        var definitions = new[]
        {
            ("build", "Build files", "Usually rebuildable", "Compiled output and temporary build files. Stop builds first; keep anything you added yourself."),
            ("cache", "Package caches", "Downloads may be needed again", "Cached NuGet, Gradle, npm and pip downloads. Tools will download missing packages again."),
            ("dependencies", "Installed dependencies", "Reinstall before working", "Project node_modules folders. Keep local changes and make sure you can reinstall packages."),
            ("old-worktrees", worktreeGroup.Item1, worktreeGroup.Item2, worktreeGroup.Item3),
            ("environments", "Python environments", "Review first", "Virtual environments can be recreated if their interpreter and dependencies are recorded."),
            ("agent", "Agent logs & history", "Review first", "Coding-agent sessions and diagnostic logs. Keep conversations and recovery information you still need."),
            ("worktrees", "Other worktrees", "Outside this rule or not checked", "These checkouts do not yet match the chosen cleanup rule. Check worktrees or view folders for activity, upstream status and reasons."),
            ("other", "Other development folders", "Review first", "Other detected development folders. Inspect their contents before cleanup.")
        };
        var buckets = locations.GroupBy(location => Key(location)).ToDictionary(group => group.Key, group => group.ToArray());
        var groups = new List<OpportunityGroup>();
        foreach (var (key, name, kind, summary) in definitions)
        {
            token.ThrowIfCancellationRequested();
            if (!buckets.TryGetValue(key, out var findings)) continue;
            var roots = findings.Select(item => item.DirectoryIndex).ToHashSet();
            long bytes = 0;
            foreach (var item in findings)
            {
                token.ThrowIfCancellationRequested();
                int parent = item.DirectoryIndex, remaining = scan.FileCount;
                bool nested = false;
                while (parent != scan.RootIndex && remaining-- > 0)
                {
                    int link = scan.PrimaryLink(parent);
                    if (link < 0) break;
                    parent = scan.Entry(link).ParentIndex;
                    if (parent < 0) break;
                    if (roots.Contains(parent)) { nested = true; break; }
                }
                if (!nested) bytes = checked(bytes + item.DisplayBytes(scan.HasAllocationData));
            }
            groups.Add(new("cleanup:" + key, name, "", kind, Array.AsReadOnly(findings), bytes, summary));
        }
        return groups.AsReadOnly();

        string Key(Opportunity item) => item.Category switch
        {
            "Build output" => "build",
            "Package caches" => "cache",
            "Dependencies" => "dependencies",
            "Python environments" => "environments",
            "Agent files" => "agent",
            "Git worktrees" => oldIntegratedWorktrees?.Contains(item.Path) == true ? "old-worktrees" : "worktrees",
            _ => "other"
        };
    }
}
