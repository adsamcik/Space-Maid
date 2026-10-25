using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

// Retain the condition that made a worktree a bulk recommendation.
// A failed/changed assessment blocks any selected root covering that checkout.
public static class GitWorktreeGuard
{
    public static bool Covers(string root, string path) => path.Equals(root, StringComparison.Ordinal)
        || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public static Task<IReadOnlyDictionary<string, string>> CheckAsync(ScanSnapshot scan,
        IReadOnlyList<Opportunity> selected, IReadOnlyList<string> requiredPaths, CancellationToken token = default,
        Action<string, object>? diagnostic = null, IProgress<string>? progress = null)
        => CheckAsync(scan, selected, requiredPaths.Distinct(StringComparer.Ordinal)
            .ToDictionary(path => path, _ => WorktreeCleanupPolicy.Default, StringComparer.Ordinal), token, diagnostic, progress);

    public static async Task<IReadOnlyDictionary<string, string>> CheckAsync(ScanSnapshot scan,
        IReadOnlyList<Opportunity> selected, IReadOnlyDictionary<string, WorktreeCleanupPolicy> requiredPolicies, CancellationToken token = default,
        Action<string, object>? diagnostic = null, IProgress<string>? progress = null)
    {
        var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, policy) in requiredPolicies.ToArray())
        {
            token.ThrowIfCancellationRequested();
            var covered = selected.Where(item => Covers(item.Path, path)).ToArray();
            if (covered.Length == 0) continue;
            progress?.Report(path);
            string? reason = null;
            if (scan.FindEntry(path) is not { } link || !scan.Entry(link).IsDirectory)
                reason = "The previously checked worktree is missing from the current inventory.";
            else
            {
                var location = new Opportunity(scan.Entry(link).FileIndex, "Linked Git checkout", "Git worktrees", path,
                    scan.Entry(link).LogicalBytes, scan.Entry(link).AllocatedBytes, "", "", "", true);
                var assessment = policy.IntegrationReference is { } reference
                    ? await GitWorktreeAnalyzer.AssessAsync(scan, location, reference, token).ConfigureAwait(false)
                    : await GitWorktreeAnalyzer.AssessAsync(scan, location, token).ConfigureAwait(false);
                diagnostic?.Invoke("worktree-cleanup-recheck", assessment);
                if (!policy.Matches(assessment, DateTime.UtcNow))
                {
                    string condition = policy.Mode switch
                    {
                        WorktreeCleanupMode.Old => $"clean with observed activity at least {policy.InactiveDays} days ago",
                        WorktreeCleanupMode.Integrated => "clean and fully integrated in the selected integration reference",
                        _ => $"clean, locally integrated and with observed activity at least {policy.InactiveDays} days ago"
                    };
                    reason = $"The captured cleanup condition was not verified: {condition}. {assessment.Status}: {assessment.Reason}";
                }
                else
                {
                    foreach (var root in covered)
                        if (assessment.CommonGitDirectory is null || assessment.CommonGitDirectory.Equals(root.Path, StringComparison.OrdinalIgnoreCase)
                            || assessment.CommonGitDirectory.StartsWith(root.Path.EndsWith(Path.DirectorySeparatorChar)
                                ? root.Path : root.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            blocked[root.Path] = "This selected folder includes shared Git storage, or its location could not be established. Review it separately to preserve branches and worktree registration.";
                }
            }
            if (reason is not null)
                foreach (var root in covered) blocked[root.Path] = "Worktree no longer qualifies for grouped cleanup. " + reason;
        }
        return blocked;
    }
}
