using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

public sealed record GitWorktreeAssessment(string Path, bool Recommended, string Status, string Reason,
    string? Branch = null, string? IntegrationTarget = null, DateTime? LastObservedModificationUtc = null,
    DateTime? LastReflogActivityUtc = null, string? CommonGitDirectory = null,
    bool VerifiedClean = false, bool Integrated = false, bool HasActivityEvidence = false, bool IsDetached = false)
{
    public string? Head { get; init; }
    public string? UpstreamReference { get; init; }
    public bool RetainedOnUpstream { get; init; }
    public bool IntegrationTargetConfirmed { get; init; }
    public bool PatchEquivalent { get; init; }
    public bool HasIgnoredFiles { get; init; }
    public bool OperationInProgress { get; init; }
    public IReadOnlyList<string> IgnoredPaths { get; init; } = [];
    public DateTime? LastActivityUtc => HasActivityEvidence && LastObservedModificationUtc is { } modified
        && LastReflogActivityUtc is { } reflog ? (modified >= reflog ? modified : reflog) : null;
}

// Explicit offline inspection. Git status may read ordinary local file content;
// Known provider-managed/reparse storage and unsafe repository configuration
// are refused. Held leases protect existing paths; an external process can
// still introduce new paths during this live namespace inspection.
public static partial class GitWorktreeAnalyzer
{
    public const int InactiveDays = 30;
    private const int MetadataLimit = 25000, OutputLimit = 1024 * 1024;

    public static Task<GitWorktreeAssessment> AssessAsync(ScanSnapshot scan, Opportunity location, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scan); ArgumentNullException.ThrowIfNull(location);
        return Task.Run(() => AssessCoreAsync(scan, location, null, token), token);
    }

    public static Task<GitWorktreeAssessment> AssessAsync(ScanSnapshot scan, Opportunity location, string integrationReference,
        CancellationToken token = default)
    {
        if (!ValidIntegrationReference(integrationReference)) throw new ArgumentException("Choose a full local branch or remote-tracking reference.", nameof(integrationReference));
        return Task.Run(() => AssessCoreAsync(scan, location, integrationReference, token), token);
    }

    private static async Task<GitWorktreeAssessment> AssessCoreAsync(ScanSnapshot scan, Opportunity location, string? approvedTarget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        string path = location.Path; string? branch = null, target = null, commonPath = null;
        DateTime? modified = null, reflog = null;
        bool verifiedClean = false, integrated = false, hasActivityEvidence = false, detached = false;
        string? headId = null, upstreamRef = null;
        bool retainedUpstream = false, patchEquivalent = false, operation = false;
        string[] ignoredPaths = [];
        try
        {
            path = Normalize(path);
            if ((uint)location.DirectoryIndex >= (uint)scan.FileCount || location.Category != "Git worktrees"
                || !scan.Records[location.DirectoryIndex].IsDirectory || scan.PathForFile(location.DirectoryIndex) != path)
                return Result("Needs review", "This is not a resolved linked-checkout finding in the current inventory.");
            modified = ScanActivity(scan, location.DirectoryIndex, budget.Token);
            bool completeModificationEvidence = modified is not null;
            var captured = FolderDeletionTarget.FromScan(scan, scan.PrimaryLink(location.DirectoryIndex));
            using var guard = new MetadataGuard(budget.Token);
            guard.Ancestors(path);
            DateTime? liveModified = guard.Tree(path, rejectNestedGit: true);
            completeModificationEvidence &= liveModified is not null;
            modified = Max(modified, liveModified);
            string pointer = Path.Join(path, ".git");
            string gitDir = ResolvePointer(ReadSmall(pointer), "gitdir: ", path);
            guard.Ancestors(gitDir);
            guard.File(Path.Join(gitDir, "commondir"));
            string common = ResolvePointer(ReadSmall(Path.Join(gitDir, "commondir")), "", gitDir);
            commonPath = common;
            guard.Ancestors(common);
            if (gitDir.Equals(common, StringComparison.Ordinal)
                || !Normalize(Path.GetDirectoryName(gitDir)!).Equals(Path.Join(common, "worktrees"), StringComparison.Ordinal)
                || Contains(path, common))
                return Result("Needs review", "This checkout is not an independent linked worktree; submodules and repository storage need manual review.");
            guard.File(Path.Join(gitDir, "gitdir"));
            if (!Normalize(ReadSmall(Path.Join(gitDir, "gitdir")).Trim()).Equals(pointer, StringComparison.Ordinal))
                return Result("Needs review", "The linked-worktree registration does not point back to this exact folder.");
            string config = Path.Join(common, "config");
            guard.File(config); // Mandatory and held before any Git invocation.
            RejectUnsafeConfig(config);
            guard.File(Path.Join(gitDir, "HEAD"));
            guard.File(Path.Join(gitDir, "index"));
            operation = OperationActive(gitDir, common);
            if (operation) return Result("Git operation in progress", "A merge, rebase, cherry-pick, bisect or Git lock is present. Finish it before retiring the checkout.");
            if (File.Exists(Path.Join(common, "objects", "info", "alternates")))
                return Result("Needs review", "Alternate Git object storage cannot be verified by this bounded local check.");
            var git = new GitReader(path, budget.Token);
            var rootsTask = git.Run("rev-parse", "--path-format=absolute", "--show-toplevel", "--git-dir", "--git-common-dir");
            var listedTask = git.Run("worktree", "list", "--porcelain", "-z");
            await Task.WhenAll(rootsTask, listedTask).ConfigureAwait(false);
            var roots = rootsTask.Result;
            var rootLines = roots.Output.TrimEnd('\n', '\r').Split('\n');
            if (roots.Code != 0 || rootLines.Length != 3 || !Normalize(rootLines[0].TrimEnd('\r')).Equals(path, StringComparison.Ordinal)
                || !Normalize(rootLines[1].TrimEnd('\r')).Equals(gitDir, StringComparison.Ordinal)
                || !Normalize(rootLines[2].TrimEnd('\r')).Equals(common, StringComparison.Ordinal))
                return Result("Needs review", "Git did not resolve this exact registered worktree root.");
            var listed = listedTask.Result;
            if (listed.Code != 0) return Result("Needs review", "Git could not read the worktree registry.");
            var registration = Registered(listed.Output, path);
            if (registration is null || registration.ContainsKey("bare") || registration.ContainsKey("locked") || registration.ContainsKey("prunable"))
                return Result("Needs review", "The worktree is unregistered, locked or prunable. Review it in Git first.");
            branch = registration.GetValueOrDefault("branch");
            detached = registration.ContainsKey("detached") && branch is null;
            if (!detached && branch?.StartsWith("refs/heads/", StringComparison.Ordinal) != true)
                return Result("Needs review", "The registered branch could not be resolved.");
            string? fullBranch = branch; branch = detached ? "Detached HEAD" : branch!["refs/heads/".Length..];
            // These independent, read-only queries share the same protected
            // paths and deadline. Overlap process startup rather than spending
            // the bounded check waiting for each Git process in turn.
            var trackedTask = git.Run("ls-files", "-v", "-z");
            var stagedTask = git.Run("ls-files", "--stage", "-z");
            await Task.WhenAll(trackedTask, stagedTask).ConfigureAwait(false);
            var tracked = trackedTask.Result;
            if (tracked.Code != 0 || tracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(row => !row.StartsWith("H ", StringComparison.Ordinal)))
                return Result("Needs review", "Unmerged, sparse or assume-unchanged index entries need manual review.");
            var staged = stagedTask.Result;
            if (staged.Code != 0 || staged.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(row => row.StartsWith("160000 ", StringComparison.Ordinal)))
                return Result("Needs review", "Submodules or unreadable index entries need manual review.");
            // Only status reads ordinary file content. Keep the index and
            // submodule refusal checks ahead of that content read.
            var statusTask = git.Run("status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignore-submodules=all");
            var headTask = git.Run("rev-parse", "--verify", "--end-of-options", "HEAD^{commit}");
            var upstreamTask = git.Run("rev-parse", "--symbolic-full-name", "@{upstream}");
            var ignoredTask = git.Run("ls-files", "--others", "--ignored", "--exclude-standard", "-z");
            await Task.WhenAll(statusTask, headTask, upstreamTask, ignoredTask).ConfigureAwait(false);
            var status = statusTask.Result;
            if (status.Code != 0) return Result("Needs review", "Git could not establish a clean worktree.");
            if (status.Output.Length != 0) return Result("Local changes", "Tracked changes or untracked files are present. Preserve this work before cleanup.");
            var head = headTask.Result;
            if (head.Code != 0 || !IsObjectId(head.Output.Trim()) || registration.GetValueOrDefault("HEAD") != head.Output.Trim())
                return Result("Needs review", "The worktree HEAD changed or is not fully available locally.");
            headId = head.Output.Trim();
            if (ignoredTask.Result.Code != 0) return Result("Needs review", "Ignored local content could not be classified.");
            ignoredPaths = ignoredTask.Result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            string integrationReason = "No approved integration reference or single locally cached remote default is available. No fetch was performed.";
            var upstream = upstreamTask.Result;
            if (upstream.Code == 0 && ValidIntegrationReference(upstream.Output.Trim()))
            {
                upstreamRef = upstream.Output.Trim();
                var retained = await git.Run("merge-base", "--is-ancestor", headId, upstreamRef).ConfigureAwait(false);
                retainedUpstream = retained.Code == 0;
            }
            target = approvedTarget;
            if (target is null)
            {
                var defaults = await git.Run("for-each-ref", "--format=%(refname)%00%(symref)", "refs/remotes/");
                var candidates = defaults.Code != 0 ? [] : defaults.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd('\r').Split('\0')).Where(parts => parts.Length == 2 && parts[0].EndsWith("/HEAD", StringComparison.Ordinal)
                        && parts[1].StartsWith("refs/remotes/", StringComparison.Ordinal)).Select(parts => parts[1]).Distinct(StringComparer.Ordinal).ToArray();
                if (candidates.Length == 1) target = candidates[0];
                // Agent worktrees commonly have detached HEADs. A local main or
                // master is a fallback only for these checkouts; ancestry must
                // still prove that no detached commit is unique.
                else if (detached && candidates.Length == 0)
                {
                    foreach (string candidate in new[] { "refs/heads/main", "refs/heads/master" })
                    {
                        var local = await git.Run("rev-parse", "--verify", "--end-of-options", candidate + "^{commit}");
                        if (local.Code == 0 && IsObjectId(local.Output.Trim())) { target = candidate; break; }
                    }
                }
            }
            GitResult? destination = null;
            bool unmerged = false;
            if (target == fullBranch) integrationReason = "The selected target is this same branch; choose an independent integration reference.";
            else if (target is not null && (target.StartsWith("refs/remotes/", StringComparison.Ordinal) || target.StartsWith("refs/heads/", StringComparison.Ordinal)))
            {
                var resolved = await git.Run("rev-parse", "--verify", "--end-of-options", target + "^{commit}");
                if (resolved.Code == 0 && IsObjectId(resolved.Output.Trim()))
                {
                    destination = resolved;
                    var ancestry = await git.Run("merge-base", "--is-ancestor", head.Output.Trim(), destination.Output.Trim());
                    integrated = ancestry.Code == 0; unmerged = ancestry.Code == 1;
                    if (unmerged)
                    {
                        var equivalent = await git.Run("cherry", target, headId).ConfigureAwait(false);
                        var rows = equivalent.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        patchEquivalent = equivalent.Code == 0 && rows.Length > 0 && rows.All(row => row.StartsWith("- ", StringComparison.Ordinal));
                    }
                    integrationReason = unmerged ? detached
                        ? "This detached HEAD has commits outside the local integration branch. Create a branch or integrate the work before cleanup."
                        : "HEAD is not fully contained in the selected local integration reference. Its branch and shared Git storage will be retained."
                        : "Git could not verify local ancestry. Remote freshness is unknown.";
                }
                else integrationReason = "The integration reference is not fully available locally. No fetch was performed.";
            }
            string headLogPath = Path.Join(gitDir, "logs", "HEAD");
            string? branchLogPath = fullBranch is null ? null : Normalize(Path.Join(common, "logs", fullBranch));
            if (File.Exists(headLogPath) && new FileInfo(headLogPath).Length != 0
                && (detached || File.Exists(branchLogPath) && new FileInfo(branchLogPath!).Length != 0))
            {
                DateTime? headLogModified = guard.File(headLogPath);
                var headLogTask = git.Run("reflog", "show", "-1", "--date=unix", "--format=%gD", "HEAD");
                if (detached)
                {
                    DateTime? headActivity = ReflogTime(await headLogTask.ConfigureAwait(false));
                    if (headActivity is not null && headLogModified is not null) reflog = Max(headActivity, headLogModified);
                }
                else
                {
                    DateTime? branchLogModified = guard.File(branchLogPath!);
                    var branchLogTask = git.Run("reflog", "show", "-1", "--date=unix", "--format=%gD", fullBranch!);
                    await Task.WhenAll(headLogTask, branchLogTask).ConfigureAwait(false);
                    DateTime? headActivity = ReflogTime(headLogTask.Result), branchActivity = ReflogTime(branchLogTask.Result);
                    if (headActivity is not null && branchActivity is not null && headLogModified is not null && branchLogModified is not null)
                        reflog = Max(Max(headActivity, branchActivity), Max(headLogModified, branchLogModified));
                }
            }
            captured.ValidateCurrent();
            if (OperationActive(gitDir, common)) { operation = true; return Result("Git operation in progress", "Git operation state changed during inspection."); }
            liveModified = guard.Tree(path, rejectNestedGit: true);
            completeModificationEvidence &= liveModified is not null;
            modified = Max(modified, liveModified);
            // Age is now a selectable filter, so recent namespace changes no
            // longer reject a checkout automatically. Re-establish clean status
            // after the final live walk before publishing usable facts.
            var finalStatusTask = git.Run("status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignore-submodules=all");
            var finalHeadTask = git.Run("rev-parse", "--verify", "--end-of-options", "HEAD^{commit}");
            var finalBranchTask = git.Run("symbolic-ref", "--quiet", "HEAD");
            var finalTargetTask = destination is null ? null : git.Run("rev-parse", "--verify", "--end-of-options", target + "^{commit}");
            var finalRegistrationTask = git.Run("worktree", "list", "--porcelain", "-z");
            var finalIgnoredTask = git.Run("ls-files", "--others", "--ignored", "--exclude-standard", "-z");
            await Task.WhenAll(finalStatusTask, finalHeadTask, finalBranchTask,
                (Task?)finalTargetTask ?? Task.CompletedTask, finalRegistrationTask, finalIgnoredTask).ConfigureAwait(false);
            if (finalIgnoredTask.Result.Code != 0 || finalIgnoredTask.Result.Output != ignoredTask.Result.Output)
                return Result("Needs review", "Ignored local content changed during inspection.");
            var finalStatus = finalStatusTask.Result;
            if (finalStatus.Code != 0) return Result("Needs review", "Git could not re-establish a clean worktree after inspection.");
            if (finalStatus.Output.Length != 0) return Result("Local changes", "Tracked changes or untracked files are present. Preserve this work before cleanup.");
            var finalHead = finalHeadTask.Result; var finalBranch = finalBranchTask.Result;
            var finalTarget = finalTargetTask?.Result; var finalRegistration = finalRegistrationTask.Result;
            var finalEntry = finalRegistration.Code == 0 ? Registered(finalRegistration.Output, path) : null;
            bool sameBranch = detached ? finalBranch.Code == 1 && finalBranch.Output.Length == 0
                : finalBranch.Code == 0 && finalBranch.Output.Trim() == fullBranch;
            if (finalHead.Code != 0 || finalHead.Output != head.Output || !sameBranch
                || (destination is not null && (finalTarget is null || finalTarget.Code != 0 || finalTarget.Output != destination.Output)) || finalEntry is null || finalEntry.ContainsKey("locked")
                || finalEntry.ContainsKey("prunable") || finalEntry.ContainsKey("detached") != detached
                || finalEntry.GetValueOrDefault("HEAD") != head.Output.Trim() || finalEntry.GetValueOrDefault("branch") != fullBranch)
                return Result("Needs review", "Git references or worktree registration changed during inspection. Check again.");
            captured.ValidateCurrent();
            verifiedClean = true;
            hasActivityEvidence = completeModificationEvidence && reflog is not null;
            if (ignoredPaths.Length > 0) return Result("Ignored local content", "Ignored files may contain unique local data. Preserve or classify them before retiring this checkout.");
            if (patchEquivalent) return Result("Patch-equivalent changes", "Equivalent patches exist in the selected target, but unique commit history is retained for review.");
            if (!integrated) return Result(unmerged ? "Unmerged commits" : "No integration evidence", integrationReason);
            if (!hasActivityEvidence) return Result("Activity unavailable", "Clean and integrated locally, but modification or HEAD/branch reflog activity evidence is unavailable. Commit dates do not establish inactivity.");
            if (Max(modified, reflog) > DateTime.UtcNow.AddDays(-InactiveDays))
                return Result("Recent activity", "Clean and integrated locally, with observed modification or reflog activity within 30 days. This does not measure actual use.");
            return Result("Stale worktree", "Clean, registered and integrated locally; no observed modification or reflog activity for 30 days. Remote freshness and actual use are unknown.");

        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Result("Needs review", "The bounded 10-second check timed out; no cleanup recommendation was made."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        { return Result("Needs review", error.Message); }

        GitWorktreeAssessment Result(string status, string reason)
        {
            var assessment = new GitWorktreeAssessment(path, false, status, reason, branch, target, modified, reflog, commonPath,
                verifiedClean, integrated, hasActivityEvidence, detached)
            { Head = headId, UpstreamReference = upstreamRef, RetainedOnUpstream = retainedUpstream,
                IntegrationTargetConfirmed = approvedTarget is not null, PatchEquivalent = patchEquivalent,
                HasIgnoredFiles = ignoredPaths.Length > 0, IgnoredPaths = Array.AsReadOnly(ignoredPaths.Take(128).ToArray()), OperationInProgress = operation };
            return assessment with { Recommended = WorktreeCleanupPolicy.Default.Matches(assessment, DateTime.UtcNow) };
        }
    }

    private static DateTime? ScanActivity(ScanSnapshot scan, int root, CancellationToken token)
    {
        if (scan.Entry(scan.PrimaryLink(root)).IsIncomplete) return null;
        DateTime? latest = null; var pending = new Stack<int>(); pending.Push(root); int count = 0, scheduled = 1;
        while (pending.TryPop(out int index))
        {
            token.ThrowIfCancellationRequested(); if (++count > MetadataLimit) throw new IOException("This worktree exceeds the bounded inspection size; review it manually.");
            var file = scan.Records[index];
            if ((scan.EntryFlags[index] & 14) != 0) return null;
            if (((uint)file.Attributes & (0x400u | 0x1000u | 0x40000u | 0x400000u)) != 0) throw new IOException("Cloud-only or reparse entries require manual review without Git content reads.");
            if (file.LastWriteFileTime <= 0 || file.CreationFileTime <= 0) return null;
            latest = Max(latest, DateTime.FromFileTimeUtc(Math.Max(file.LastWriteFileTime, file.CreationFileTime)));
            if (file.IsDirectory) foreach (int link in scan.Children(index))
            {
                token.ThrowIfCancellationRequested();
                if (++scheduled > MetadataLimit) throw new IOException("This worktree exceeds the bounded inspection size; review it manually.");
                pending.Push(scan.Entry(link).FileIndex);
            }
        }
        return latest;
    }

    private static string ResolvePointer(string text, string prefix, string root)
    {
        text = text.Trim(); if (text.Length <= prefix.Length || !text.StartsWith(prefix, StringComparison.Ordinal) || text.Contains('\n') || text.Contains('\r'))
            throw new IOException("The Git storage pointer is not supported by this bounded inspection.");
        return Normalize(Path.GetFullPath(text[prefix.Length..], root));
    }
    private static string ReadSmall(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new IOException("Git metadata exceeds the bounded inspection size.");
        using var reader = new StreamReader(path, Encoding.UTF8);
        var buffer = new char[65537]; int count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > 65536) throw new IOException("Git metadata exceeds the bounded inspection size.");
        return new string(buffer, 0, count);
    }
    private static void RejectUnsafeConfig(string path)
    {
        string text = ReadSmall(path);
        // Git reads the repository-format feature before command-line config
        // overrides. Conservatively reject this feature before starting Git,
        // including false/comment occurrences, rather than trusting -c here.
        if (Regex.IsMatch(text, @"\bworktreeConfig\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new IOException("Worktree-specific Git configuration requires manual review; external code was not run.");
        if (Regex.IsMatch(text, @"^\s*\[\s*(include|includeif|filter)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new IOException("Git configuration includes or content filters require manual review; external code was not run.");
    }
    private static Dictionary<string, string>? Registered(string text, string path)
    {
        foreach (string record in text.Split("\0\0", StringSplitOptions.RemoveEmptyEntries))
        {
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string field in record.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            { int space = field.IndexOf(' '); attributes.Add(space < 0 ? field : field[..space], space < 0 ? "" : field[(space + 1)..]); }
            if (attributes.TryGetValue("worktree", out string? root) && Normalize(root).Equals(path, StringComparison.Ordinal)) return attributes;
        }
        return null;
    }
    private static DateTime? ReflogTime(GitResult result)
    {
        var match = Regex.Match(result.Output.Trim(), @"@\{([0-9]+)\}$", RegexOptions.CultureInvariant);
        if (result.Code != 0 || !match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }
    private static bool IsObjectId(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
    internal static bool ValidIntegrationReference(string? value) => value is { Length: > 11 and < 512 }
        && (value.StartsWith("refs/heads/", StringComparison.Ordinal) || value.StartsWith("refs/remotes/", StringComparison.Ordinal))
        && !value.EndsWith("/HEAD", StringComparison.Ordinal) && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("@{", StringComparison.Ordinal) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.');
    private static bool OperationActive(string gitDir, string common) => new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG",
        "BISECT_START", "rebase-merge", "rebase-apply", "sequencer", "index.lock", "HEAD.lock" }.Any(name => File.Exists(Path.Join(gitDir, name)) || Directory.Exists(Path.Join(gitDir, name)))
        || File.Exists(Path.Join(common, "packed-refs.lock")) || File.Exists(Path.Join(common, "config.lock"));
    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null || a >= b ? a : b;
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Contains(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal sealed class MetadataGuard(CancellationToken token) : IDisposable
    {
        private int _count, _scheduled;
        private const int LeaseLimit = 4096;
        private readonly Dictionary<string, SafeFileHandle> _paths = new(StringComparer.Ordinal);
        private readonly List<SafeFileHandle> _handles = [];
        public DateTime? File(string path) => Inspect(path).Modified;
        public void Ancestors(string path)
        {
            string root = Path.GetPathRoot(path)!;
            if (root.StartsWith('\\') || new DriveInfo(root).DriveType == DriveType.Network) throw new IOException("Network Git storage needs manual review; no network inspection was performed.");
            var ancestors = new Stack<string>();
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
            while (ancestors.TryPop(out string? ancestor)) Inspect(ancestor);
        }
        public DateTime? Tree(string root, bool rejectNestedGit)
        {
            DateTime? latest = null; bool complete = true; var pending = new Stack<string>(); pending.Push(root);
            if (++_scheduled > MetadataLimit) throw new IOException("Git storage exceeds the bounded metadata inspection size; review it manually.");
            while (pending.TryPop(out string? path))
            {
                var metadata = Inspect(path); latest = Max(latest, metadata.Modified);
                complete &= metadata.Modified is not null;
                if (!metadata.Directory) continue;
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                {
                    token.ThrowIfCancellationRequested();
                    if (rejectNestedGit && !path.Equals(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(child).Equals(".git", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Nested Git checkouts or submodules need manual review before removing their containing worktree.");
                    if (++_scheduled > MetadataLimit) throw new IOException("Git storage exceeds the bounded metadata inspection size; review it manually.");
                    pending.Push(child);
                }
            }
            return complete ? latest : null;
        }
        private (bool Directory, DateTime? Modified) Inspect(string path)
        {
            token.ThrowIfCancellationRequested();
            if (++_count > MetadataLimit) throw new IOException("Git storage exceeds the bounded metadata inspection size; review it manually.");
            path = Normalize(path);
            if (_paths.TryGetValue(path, out var held))
            {
                var observed = ReadMetadata(held);
                return (observed.Directory, observed.Modified);
            }
            if (_paths.Count >= LeaseLimit) throw new IOException("Git storage exceeds the bounded 4,096-path stable-read limit; review it manually.");
            var metadata = Open(path, 0x80); _handles.Add(metadata);
            var before = ReadMetadata(metadata);
            // Attribute-only handles do not enforce data-write sharing. Acquire
            // read/list access only after the no-recall metadata/provider checks,
            // then pin this exact identity against data writes and replacement.
            var data = Open(path, before.Directory ? 0x81u : 0x80000080u); _handles.Add(data);
            var after = ReadMetadata(data);
            if (before != after) throw new IOException("A Git path changed before its stable read lease could be acquired.");
            _paths.Add(path, data);
            return (after.Directory, after.Modified);
        }
        private static SafeFileHandle Open(string path, uint access)
        {
            var handle = NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), access, (access & 0x80000000u) != 0 ? 1u : 3u, 0, 3,
                NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint | 0x00100000, 0);
            if (!handle.IsInvalid) return handle;
            int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error);
        }
        private static unsafe HeldMetadata ReadMetadata(SafeFileHandle handle)
        {
            byte* info = stackalloc byte[40];
            if (!NativeMethods.GetFileInformationByHandleEx(handle, 9, info, 8)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            uint attributes = LayoutParser.U32(new(info, 8), 0), tag = LayoutParser.U32(new(info, 8), 4);
            var cloud = new byte[4200];
            if ((attributes & (0x400u | 0x1000u | 0x40000u | 0x400000u)) != 0 || tag != 0
                || RecognizesProvider(CfGetPlaceholderInfo(handle, 1, cloud, (uint)cloud.Length, out _))
                || RecognizesProvider(CfGetSyncRootInfoByHandle(handle, 2, cloud, 1028, out _)))
                throw new IOException("Provider-managed or reparse Git paths are not inspected; cloud content was not requested.");
            if (!NativeMethods.GetFileInformationByHandleEx(handle, 0, info, 40)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            long modified = LayoutParser.I64(new(info, 40), 16);
            long created = LayoutParser.I64(new(info, 40), 0);
            if (!NativeMethods.GetFileInformationByHandleEx(handle, 18, info, 24)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return new((attributes & (uint)FileAttributes.Directory) != 0, modified > 0 && created > 0 ? DateTime.FromFileTimeUtc(Math.Max(modified, created)) : null,
                created, Convert.ToHexString(new ReadOnlySpan<byte>(info, 24)));
        }
        private static bool RecognizesProvider(int result) => result >= 0 || result == unchecked((int)0x800700EA);
        private sealed record HeldMetadata(bool Directory, DateTime? Modified, long Created, string Identity);
        public void Dispose() { foreach (var handle in _handles) handle.Dispose(); }
    }

    internal sealed record GitResult(int Code, string Output, string Error = "");
    internal sealed class GitReader(string path, CancellationToken token)
    {
        public async Task<GitResult> Run(params string[] arguments)
        {
            token.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo(FindGit()) { WorkingDirectory = path, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (string key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
            info.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; info.Environment["GIT_CONFIG_SYSTEM"] = "NUL"; info.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["GIT_OPTIONAL_LOCKS"] = "0"; info.Environment["GIT_NO_LAZY_FETCH"] = "1";
            info.Environment["GIT_NO_REPLACE_OBJECTS"] = "1"; info.Environment["GIT_PAGER"] = "";
            info.Environment["GIT_ATTR_SOURCE"] = "HEAD"; info.Environment["GIT_ATTR_NOSYSTEM"] = "1";
            foreach (string option in new[] { "core.fsmonitor=false", "core.hooksPath=NUL", "core.attributesFile=NUL", "core.excludesFile=NUL",
                "core.untrackedCache=false", "core.ignoreStat=false", "diff.external=", "diff.renames=false", "status.renames=false", "gc.auto=0",
                "maintenance.auto=false", "protocol.allow=never", "extensions.worktreeConfig=false" })
            { info.ArgumentList.Add("-c"); info.ArgumentList.Add(option); }
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = info }; process.Start();
            try
            {
                var stdout = ReadBounded(process.StandardOutput, token); var stderr = ReadBounded(process.StandardError, token);
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(token)).ConfigureAwait(false);
                return new(process.ExitCode, stdout.Result, stderr.Result);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            }
        }
        private string FindGit()
        {
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (!Path.IsPathFullyQualified(folder)) continue;
                string candidate = Path.Join(folder, "git.exe");
                if (!Contains(path, candidate) && !candidate.StartsWith('\\') && File.Exists(candidate)) return candidate;
            }
            throw new IOException("Git is unavailable. Install Git or review this checkout manually.");
        }
        private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
        {
            var output = new StringBuilder(); var buffer = new char[4096]; int count; bool overflow = false;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > OutputLimit) overflow = true;
                if (!overflow) output.Append(buffer, 0, count);
            }
            if (overflow) throw new IOException("Git output exceeded the bounded inspection size.");
            return output.ToString();
        }
    }

    [LibraryImport("cldapi.dll")]
    private static partial int CfGetPlaceholderInfo(SafeFileHandle handle, int info, [Out, MarshalUsing(CountElementName = nameof(length))] byte[] buffer, uint length, out uint returned);
    [LibraryImport("cldapi.dll")]
    private static partial int CfGetSyncRootInfoByHandle(SafeFileHandle handle, int info, [Out, MarshalUsing(CountElementName = nameof(length))] byte[] buffer, uint length, out uint returned);
}
