using SpaceMaid.Core.Analysis;

namespace SpaceMaid.Extensions.Developer.Analysis;

// A small task-oriented checklist; project/path lists remain a drill-down.
public static class CleanupGrouping
{
    public static IReadOnlyList<OpportunityGroup> Build(IOpportunityInventory scan, IReadOnlyList<Opportunity> locations,
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
            ("cache", "Package caches", "Downloads may be needed again", "Cached package downloads, including NuGet, Gradle, npm, pip, uv and Cargo archives. Keep packages needed for offline work."),
            ("tools", "Tool caches", "Recreated when tools run", "Project build, test and analysis caches. Stop the tools first; review custom files and expect slower first runs."),
            ("tests", "Test results & reports", "Keep evidence you need", "Test reports, traces and run results. Preserve failure evidence and useful recordings before cleanup."),
            ("downloads", "Downloaded tooling", "Needed again for builds or tests", "Gradle distributions and Playwright browsers. Stop using them first; keep tooling needed offline. Reinstalling may require downloads."),
            ("editor", "Editor workspace data", "Local settings may be lost", "Visual Studio workspace data and caches. Close Visual Studio first; preserve personal settings, breakpoints and other local state."),
            ("dependencies", "Installed dependencies", "Reinstall before working", "Project node_modules folders. Keep local changes and make sure you can reinstall packages."),
            ("old-worktrees", worktreeGroup.Item1, worktreeGroup.Item2, worktreeGroup.Item3),
            ("environments", "Python environments", "Review first", "Virtual environments can be recreated if their interpreter and dependencies are recorded."),
            ("agent", "Agent logs & history", "Review first", "Coding-agent sessions, tool results and diagnostic logs. Keep conversations, project memory and recovery information you still need."),
            ("checkpoints", "Agent file checkpoints", "Keep recovery copies", "File snapshots used to undo agent edits. Preserve unique and untracked work before removing rollback history."),
            ("models", "AI models & data", "Review in the owning tool", "Local model weights, dataset downloads and cached revisions. Keep offline copies and companion files; use the owning tool to manage shared blobs and revisions."),
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
                    parent = scan.LinkParent(link);
                    if (parent < 0) break;
                    if (roots.Contains(parent)) { nested = true; break; }
                }
                if (!nested) bytes = checked(bytes + item.DisplayBytes(scan.HasAllocationData));
            }
            groups.Add(new("cleanup:" + key, name, "", kind, Array.AsReadOnly(findings), bytes, summary)
            {
                Caption = Caption(key),
                Glyph = Glyph(key),
                StatusLabel = Status(key, kind),
                CanSelectGroup = key != "worktrees" && findings.Any(item => item.CleanupBlockReason.Length == 0),
                Role = key switch
                {
                    "worktrees" => OpportunityGroupRole.Worktrees,
                    "old-worktrees" => OpportunityGroupRole.MatchedWorktrees,
                    _ => OpportunityGroupRole.General
                },
                BreakdownHeading = key == "build" ? "By language and project" : "By tool and project"
            });
        }
        return groups.AsReadOnly();

        string Key(Opportunity item) => item.Category switch
        {
            "Build output" => "build",
            "Package caches" => "cache",
            "Tool caches" => "tools",
            "Test artifacts" => "tests",
            "Downloaded tooling" => "downloads",
            "Editor workspace data" => "editor",
            "Dependencies" => "dependencies",
            "Python environments" => "environments",
            "Agent files" => "agent",
            "Agent checkpoints" => "checkpoints",
            "AI models & data" => "models",
            "Git worktrees" => oldIntegratedWorktrees?.Contains(item.Path) == true ? "old-worktrees" : "worktrees",
            _ => "other"
        };
    }

    private static string Caption(string key) => key switch
    {
        "build" => "Build output · Stop builds first",
        "cache" => "Package downloads · Keep what you need offline",
        "tools" => "Build, test and analysis caches · Stop tools first",
        "tests" => "Reports and recordings · Keep useful evidence",
        "downloads" => "Build and test tools · Downloads may be needed again",
        "editor" => "Close Visual Studio · Local settings may be lost",
        "dependencies" => "Installed packages · Keep local changes",
        "old-worktrees" => "Clean checkouts · Includes ignored files · Branches kept",
        "environments" => "Keep interpreter and dependency records",
        "agent" => "Sessions and logs · Keep recovery information",
        "checkpoints" => "File snapshots · Keep recovery copies",
        "models" => "Models and datasets · Keep needed offline copies",
        "worktrees" => "Check activity and upstream status before cleanup",
        _ => "Inspect contents before cleanup"
    };

    private static string Glyph(string key) => key switch
    {
        "build" => "\uE7C1", "cache" => "\uE8B7", "tools" => "\uE713",
        "tests" => "\uE73E", "downloads" => "\uE896", "editor" => "\uE70F",
        "dependencies" => "\uE8EC", "old-worktrees" or "worktrees" => "\uE8AD",
        "environments" => "\uE943", "agent" or "checkpoints" => "\uE8F2", "models" => "\uE943", _ => "\uE8B7"
    };

    private static string Status(string key, string kind) => key switch
    {
        "build" => "Stop builds first", "cache" => "Keep offline packages",
        "tools" => "Stop tools first", "tests" => "Keep useful reports",
        "downloads" => "Downloads needed again", "editor" => "Local settings may be lost",
        "dependencies" => "Keep local changes", "old-worktrees" => "Includes ignored files · Branches kept",
        "environments" => "Keep dependency records", "agent" => "Keep recovery history",
        "checkpoints" => "Keep recovery copies", "models" => "Review in owning tool",
        "worktrees" => "Check before selecting", _ => kind
    };
}
