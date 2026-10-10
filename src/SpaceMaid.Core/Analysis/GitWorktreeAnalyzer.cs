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
    bool VerifiedClean = false, bool Integrated = false, bool HasActivityEvidence = false)
{
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
        return Task.Run(() => AssessCoreAsync(scan, location, token), token);
    }

    private static async Task<GitWorktreeAssessment> AssessCoreAsync(ScanSnapshot scan, Opportunity location, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        string path = location.Path; string? branch = null, target = null, commonPath = null;
        DateTime? modified = null, reflog = null;
        bool verifiedClean = false, integrated = false, hasActivityEvidence = false;
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
            guard.Tree(common, rejectNestedGit: false);
            string config = Path.Join(common, "config");
            guard.File(config); // Mandatory and held before any Git invocation.
            RejectUnsafeConfig(config);
            if (File.Exists(Path.Join(common, "objects", "info", "alternates")))
                return Result("Needs review", "Alternate Git object storage cannot be verified by this bounded local check.");
            var git = new GitReader(path, budget.Token);
            var roots = await git.Run("rev-parse", "--path-format=absolute", "--show-toplevel", "--git-dir", "--git-common-dir");
            var rootLines = roots.Output.TrimEnd('\n', '\r').Split('\n');
            if (roots.Code != 0 || rootLines.Length != 3 || !Normalize(rootLines[0].TrimEnd('\r')).Equals(path, StringComparison.Ordinal)
                || !Normalize(rootLines[1].TrimEnd('\r')).Equals(gitDir, StringComparison.Ordinal)
                || !Normalize(rootLines[2].TrimEnd('\r')).Equals(common, StringComparison.Ordinal))
                return Result("Needs review", "Git did not resolve this exact registered worktree root.");
            var listed = await git.Run("worktree", "list", "--porcelain", "-z");
            if (listed.Code != 0) return Result("Needs review", "Git could not read the worktree registry.");
            var registration = Registered(listed.Output, path);
            if (registration is null || registration.ContainsKey("bare") || registration.ContainsKey("locked") || registration.ContainsKey("prunable"))
                return Result("Needs review", "The worktree is unregistered, locked or prunable. Review it in Git first.");
            branch = registration.GetValueOrDefault("branch");
            if (branch?.StartsWith("refs/heads/", StringComparison.Ordinal) != true)
                return Result("Needs review", "Detached or unresolved branches need manual review.");
            string fullBranch = branch; branch = branch["refs/heads/".Length..];
            var tracked = await git.Run("ls-files", "-v", "-z");
            if (tracked.Code != 0 || tracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(row => !row.StartsWith("H ", StringComparison.Ordinal)))
                return Result("Needs review", "Unmerged, sparse or assume-unchanged index entries need manual review.");
            var staged = await git.Run("ls-files", "--stage", "-z");
            if (staged.Code != 0 || staged.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(row => row.StartsWith("160000 ", StringComparison.Ordinal)))
                return Result("Needs review", "Submodules or unreadable index entries need manual review.");
            var status = await git.Run("status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignore-submodules=all");
            if (status.Code != 0) return Result("Needs review", "Git could not establish a clean worktree.");
            if (status.Output.Length != 0) return Result("Local changes", "Tracked changes or untracked files are present. Preserve this work before cleanup.");
            var head = await git.Run("rev-parse", "--verify", "--end-of-options", "HEAD^{commit}");
            if (head.Code != 0 || !IsObjectId(head.Output.Trim()) || registration.GetValueOrDefault("HEAD") != head.Output.Trim())
                return Result("Needs review", "The worktree HEAD changed or is not fully available locally.");
            string integrationReason = "No configured upstream or single locally cached remote default is available. No fetch was performed.";
            var upstream = await git.Run("rev-parse", "--symbolic-full-name", "@{upstream}");
            if (upstream.Code == 0) target = upstream.Output.Trim();
            else
            {
                var defaults = await git.Run("for-each-ref", "--format=%(refname)%00%(symref)", "refs/remotes/");
                var candidates = defaults.Code != 0 ? [] : defaults.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd('\r').Split('\0')).Where(parts => parts.Length == 2 && parts[0].EndsWith("/HEAD", StringComparison.Ordinal)
                        && parts[1].StartsWith("refs/remotes/", StringComparison.Ordinal)).Select(parts => parts[1]).Distinct(StringComparer.Ordinal).ToArray();
                if (candidates.Length == 1) target = candidates[0];
            }
            GitResult? destination = null;
            bool unmerged = false;
            if (target == fullBranch) integrationReason = "The upstream points to this same branch; an independent integration reference is required.";
            else if (target is not null && (target.StartsWith("refs/remotes/", StringComparison.Ordinal) || target.StartsWith("refs/heads/", StringComparison.Ordinal)))
            {
                var resolved = await git.Run("rev-parse", "--verify", "--end-of-options", target + "^{commit}");
                if (resolved.Code == 0 && IsObjectId(resolved.Output.Trim()))
                {
                    destination = resolved;
                    var ancestry = await git.Run("merge-base", "--is-ancestor", head.Output.Trim(), destination.Output.Trim());
                    integrated = ancestry.Code == 0; unmerged = ancestry.Code == 1;
                    integrationReason = unmerged ? "HEAD is not fully contained in the selected local integration reference. Its branch and shared Git storage will be retained."
                        : "Git could not verify local ancestry. Remote freshness is unknown.";
                }
                else integrationReason = "The integration reference is not fully available locally. No fetch was performed.";
            }
            string headLogPath = Path.Join(gitDir, "logs", "HEAD"), branchLogPath = Normalize(Path.Join(common, "logs", fullBranch));
            if (File.Exists(headLogPath) && File.Exists(branchLogPath) && new FileInfo(headLogPath).Length != 0 && new FileInfo(branchLogPath).Length != 0)
            {
                DateTime? headLogModified = guard.File(headLogPath), branchLogModified = guard.File(branchLogPath);
                var headLog = await git.Run("reflog", "show", "-1", "--date=unix", "--format=%gD", "HEAD");
                var branchLog = await git.Run("reflog", "show", "-1", "--date=unix", "--format=%gD", fullBranch);
                DateTime? headActivity = ReflogTime(headLog), branchActivity = ReflogTime(branchLog);
                if (headActivity is not null && branchActivity is not null && headLogModified is not null && branchLogModified is not null)
                    reflog = Max(Max(headActivity, branchActivity), Max(headLogModified, branchLogModified));
            }
            captured.ValidateCurrent();
            guard.Tree(common, rejectNestedGit: false);
            liveModified = guard.Tree(path, rejectNestedGit: true);
            completeModificationEvidence &= liveModified is not null;
            modified = Max(modified, liveModified);
            // Age is now a selectable filter, so recent namespace changes no
            // longer reject a checkout automatically. Re-establish clean status
            // after the final live walk before publishing usable facts.
            var finalStatus = await git.Run("status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignore-submodules=all");
            if (finalStatus.Code != 0) return Result("Needs review", "Git could not re-establish a clean worktree after inspection.");
            if (finalStatus.Output.Length != 0) return Result("Local changes", "Tracked changes or untracked files are present. Preserve this work before cleanup.");
            var finalHead = await git.Run("rev-parse", "--verify", "--end-of-options", "HEAD^{commit}");
            var finalBranch = await git.Run("symbolic-ref", "--quiet", "HEAD");
            var finalTarget = destination is null ? null : await git.Run("rev-parse", "--verify", "--end-of-options", target + "^{commit}");
            var finalRegistration = await git.Run("worktree", "list", "--porcelain", "-z");
            var finalEntry = finalRegistration.Code == 0 ? Registered(finalRegistration.Output, path) : null;
            if (finalHead.Code != 0 || finalHead.Output != head.Output || finalBranch.Code != 0 || finalBranch.Output.Trim() != fullBranch
                || (destination is not null && (finalTarget is null || finalTarget.Code != 0 || finalTarget.Output != destination.Output)) || finalEntry is null || finalEntry.ContainsKey("locked")
                || finalEntry.ContainsKey("prunable") || finalEntry.GetValueOrDefault("HEAD") != head.Output.Trim() || finalEntry.GetValueOrDefault("branch") != fullBranch)
                return Result("Needs review", "Git references or worktree registration changed during inspection. Check again.");
            captured.ValidateCurrent();
            verifiedClean = true;
            hasActivityEvidence = completeModificationEvidence && reflog is not null;
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
                verifiedClean, integrated, hasActivityEvidence);
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
            if (file.LastWriteFileTime <= 0) return null;
            latest = Max(latest, DateTime.FromFileTimeUtc(file.LastWriteFileTime));
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
            var handle = NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), access, 1, 0, 3,
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
            return new((attributes & (uint)FileAttributes.Directory) != 0, modified > 0 ? DateTime.FromFileTimeUtc(modified) : null,
                created, Convert.ToHexString(new ReadOnlySpan<byte>(info, 24)));
        }
        private static bool RecognizesProvider(int result) => result >= 0 || result == unchecked((int)0x800700EA);
        private sealed record HeldMetadata(bool Directory, DateTime? Modified, long Created, string Identity);
        public void Dispose() { foreach (var handle in _handles) handle.Dispose(); }
    }

    private sealed record GitResult(int Code, string Output);
    private sealed class GitReader(string path, CancellationToken token)
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
                return new(process.ExitCode, stdout.Result);
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
