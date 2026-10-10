using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

// A small task-oriented checklist; project/path lists remain a drill-down.
public static class CleanupGrouping
{
    public static IReadOnlyList<OpportunityGroup> Build(ScanSnapshot scan, IReadOnlyList<Opportunity> locations,
        IReadOnlySet<string>? oldIntegratedWorktrees = null, CancellationToken token = default)
    {
        var definitions = new[]
        {
            ("build", "Build files", "Usually rebuildable", "Compiled output and temporary build files. Stop builds first; keep anything you added yourself."),
            ("cache", "Package caches", "Downloads may be needed again", "Cached NuGet, Gradle, npm and pip downloads. Tools will download missing packages again."),
            ("dependencies", "Installed dependencies", "Reinstall before working", "Project node_modules folders. Keep local changes and make sure you can reinstall packages."),
            ("old-worktrees", "Old integrated worktrees", "Checked locally · Review ignored files", "Old, clean Git checkouts whose commits are already in a local upstream/default branch. No observed activity for 30 days; cleanup includes ignored files."),
            ("environments", "Python environments", "Review first", "Virtual environments can be recreated if their interpreter and dependencies are recorded."),
            ("agent", "Agent logs & history", "Review first", "Coding-agent sessions and diagnostic logs. Keep conversations and recovery information you still need."),
            ("worktrees", "Worktrees to review", "Check before selecting a group", "Check worktrees to find old, clean, integrated checkouts. Review other folders individually; they may contain unfinished work or be submodules."),
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
