using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

public sealed record ManagedWorktreePolicy(string Path, string IntegrationReference, string Branch, string RootIdentity,
    int InactiveDays = 30, int GraceDays = 7, bool Automatic = false)
{
    public void Validate()
    {
        FolderDeletionTarget.ValidatePath(Path);
        if (!GitWorktreeAnalyzer.ValidIntegrationReference(IntegrationReference) || Branch is null || !Branch.StartsWith("refs/heads/", StringComparison.Ordinal)
            || RootIdentity is null || RootIdentity.Length != 48 || !RootIdentity.All(char.IsAsciiHexDigit) || InactiveDays is < 1 or > 365 || GraceDays is < 1 or > 365)
            throw new ArgumentException("Invalid managed worktree policy.");
    }
}

public enum WorktreeRetirementState { Prepared, Quarantined, Finalizing, Completed, Restored, NeedsReview }
public sealed record WorktreeRetirementEntry(string RelativePath, bool Directory, string Identity, long Length, long Written, long Created, string? Hash);
public sealed record WorktreeRetirementRecord(Guid Id, ManagedWorktreePolicy Policy, string QuarantinePath, string Head,
    string CommonGitDirectory, DateTime QuarantinedUtc, WorktreeRetirementState State, IReadOnlyList<WorktreeRetirementEntry> Entries,
    string Message = "")
{
    public DateTime FinalizeAfterUtc => QuarantinedUtc.AddDays(Policy.GraceDays);
    public string RecoveryReference => "refs/space-maid/retired/" + Id.ToString("N");
}
public sealed record WorktreeRetirementSettings(IReadOnlyList<ManagedWorktreePolicy> Policies, IReadOnlyList<WorktreeRetirementRecord> Records);

// Managed worktrees are explicitly enrolled. Git-aware moves retain registration;
// finalization deletes only the captured unchanged objects, never recursively.
public sealed partial class WorktreeRetirement(string settingsPath)
{
    private const int EntryLimit = 4000;
    private readonly SemaphoreSlim _gate = new(1);
    public WorktreeRetirementSettings Load()
    {
        try
        {
            using var input = File.OpenRead(settingsPath);
            if (input.Length > 4 * 1024 * 1024) throw new IOException("Managed worktree records exceed their bounded size.");
            var state = JsonSerializer.Deserialize<WorktreeRetirementSettings>(input) ?? throw new IOException("Managed worktree settings are invalid.");
            if (state.Policies is null || state.Records is null || state.Policies.Count > 32 || state.Records.Count > 128)
                throw new IOException("Managed worktree settings are invalid.");
            foreach (var policy in state.Policies) { if (policy is null) throw new IOException("Managed worktree policy is invalid."); policy.Validate(); }
            foreach (var record in state.Records)
            {
                if (record?.Policy is null || record.Head is null) throw new IOException("Managed worktree recovery record is invalid.");
                record.Policy.Validate(); FolderDeletionTarget.ValidatePath(record.QuarantinePath);
                if (record.Entries is null || record.Entries.Count > EntryLimit || !Enum.IsDefined(record.State)
                    || record.Head.Length is not (40 or 64) || !record.Head.All(char.IsAsciiHexDigit)
                    || !string.Equals(System.IO.Path.GetPathRoot(record.Policy.Path), System.IO.Path.GetPathRoot(record.QuarantinePath), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Managed worktree recovery record is invalid.");
                foreach (var entry in record.Entries)
                {
                    if (entry is null || string.IsNullOrEmpty(entry.RelativePath) || entry.Identity is null || entry.Identity.Length != 48
                        || !entry.Identity.All(char.IsAsciiHexDigit) || entry.Length < 0 || !entry.Directory && (entry.Hash is null || entry.Hash.Length != 64))
                        throw new IOException("Managed worktree content record is invalid.");
                    Resolve(record.QuarantinePath, entry.RelativePath);
                }
            }
            return state;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new([], []); }
    }

    public async Task<GitWorktreeAssessment> AssessAsync(string path, string integrationReference, CancellationToken token = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        token = budget.Token;
        // Reject a checkout that grew beyond the stable metadata budget before
        // building a full inventory, including for manual enrollment checks.
        using var preflight = new GitWorktreeAnalyzer.MetadataGuard(token);
        await Task.Run(() => { preflight.Ancestors(path); preflight.Tree(path, rejectNestedGit: true); }, token).ConfigureAwait(false);
        var scan = await new VolumeScanner().ScanAsync(path, cancellationToken: token, preferFast: false).ConfigureAwait(false);
        var location = new Opportunity(scan.RootIndex, "Linked Git checkout", "Git worktrees", scan.RootPath,
            scan.LogicalBytes, scan.AllocatedBytes, "", "", "", true) { IsWorktree = true };
        return await GitWorktreeAnalyzer.AssessAsync(scan, location, integrationReference, token).ConfigureAwait(false);
    }

    public async Task<ManagedWorktreePolicy> EnrollAsync(string path, string integrationReference, int inactiveDays, int graceDays,
        bool automatic, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var assessment = await AssessAsync(path, integrationReference, token).ConfigureAwait(false);
            RequireCleanIdentity(assessment, "refs/heads/" + assessment.Branch, assessment.Head, assessment.CommonGitDirectory);
            if (!assessment.IntegrationTargetConfirmed || !assessment.Integrated) throw new InvalidOperationException("Choose an integration target that retains this exact HEAD.");
            using var root = RetirementLease.Open(path, false);
            var policy = new ManagedWorktreePolicy(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)), integrationReference, "refs/heads/" + assessment.Branch,
                root.Entry.Identity, inactiveDays, graceDays, automatic);
            policy.Validate(); var state = Load();
            if (state.Records.Any(r => r.Policy.Path.Equals(policy.Path, StringComparison.OrdinalIgnoreCase)
                && r.State is not (WorktreeRetirementState.Completed or WorktreeRetirementState.Restored)))
                throw new InvalidOperationException("This checkout already has a pending retirement record.");
            Save(state with { Policies = state.Policies.Where(p => !p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Append(policy).ToArray() });
            return policy;
        }
        finally { _gate.Release(); }
    }

    public async Task SetAutomaticAsync(string path, bool automatic, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { var state = Load(); Save(state with { Policies = state.Policies.Select(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ? p with { Automatic = automatic } : p).ToArray() }); }
        finally { _gate.Release(); }
    }

    public async Task<WorktreeRetirementRecord> QuarantineAsync(ManagedWorktreePolicy policy, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            policy.Validate(); var state = Load();
            if (!state.Policies.Contains(policy)) throw new InvalidOperationException("Enroll this exact checkout policy before retiring it.");
            if (state.Records.Any(r => r.Policy.Path == policy.Path && r.State is not (WorktreeRetirementState.Completed or WorktreeRetirementState.Restored)))
                throw new InvalidOperationException("Resolve this checkout's existing recovery record before another retirement.");
            var assessment = await AssessAsync(policy.Path, policy.IntegrationReference, token).ConfigureAwait(false);
            RequireEligible(assessment, policy.InactiveDays);
            if ("refs/heads/" + assessment.Branch != policy.Branch) throw new InvalidOperationException("The enrolled checkout branch changed.");
            using (var root = RetirementLease.Open(policy.Path, false))
                if (root.Entry.Identity != policy.RootIdentity) throw new InvalidOperationException("The enrolled checkout was replaced.");
            string destination = policy.Path + ".space-maid-retired-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The quarantine destination already exists.");
            // Capture complete known contents while the analyzer's positive Git
            // state still applies. Unknown/new data after the move blocks finalization.
            _ = Capture(policy.Path, token);
            var record = new WorktreeRetirementRecord(Guid.NewGuid(), policy, destination, assessment.Head!, assessment.CommonGitDirectory!,
                DateTime.UtcNow, WorktreeRetirementState.Prepared, []);
            Put(record); // Recovery intent must be durable before moving the checkout.
            var git = new GitWorktreeAnalyzer.GitReader(assessment.CommonGitDirectory!, token);
            var retained = await git.Run("update-ref", record.RecoveryReference, record.Head, new string('0', record.Head.Length)).ConfigureAwait(false);
            if (retained.Code != 0) throw new IOException("Git could not create the exact recovery reference. No checkout was moved.");
            var moved = await git.Run("worktree", "move", "--", policy.Path, destination).ConfigureAwait(false);
            if (moved.Code != 0)
            {
                bool originalUnmoved = Directory.Exists(policy.Path) && !Directory.Exists(destination);
                record = record with { State = originalUnmoved ? WorktreeRetirementState.Restored : WorktreeRetirementState.NeedsReview,
                    Message = "Git refused the move; files and recovery intent were retained. " + Detail(moved.Error) };
                Put(record); return record;
            }
            try
            {
                var current = await AssessAsync(destination, policy.IntegrationReference, token).ConfigureAwait(false);
                RequireCleanIdentity(current, policy.Branch, record.Head, record.CommonGitDirectory);
                var entries = Capture(destination, token);
                record = record with { State = WorktreeRetirementState.Quarantined, Entries = entries, Message = "Checkout quarantined; branches and Git registration retained. Space is released only after finalization." };
            }
            catch (Exception error) when (Expected(error))
            { record = record with { State = WorktreeRetirementState.NeedsReview, Message = "Checkout moved, but its final state needs review: " + error.Message }; }
            Put(record); return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<WorktreeRetirementRecord> RestoreAsync(Guid id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var record = Find(id);
            if (record.State is WorktreeRetirementState.Completed or WorktreeRetirementState.Restored) throw new InvalidOperationException("This record has no quarantined checkout to restore.");
            if (record.State == WorktreeRetirementState.Prepared && Directory.Exists(record.Policy.Path) && !Directory.Exists(record.QuarantinePath))
            { record = record with { State = WorktreeRetirementState.Restored, Message = "The move had not started. The original checkout remains in place." }; Put(record); return record; }
            if (Directory.Exists(record.Policy.Path) || File.Exists(record.Policy.Path)) throw new IOException("The original location is occupied. Nothing was overwritten.");
            using (var root = RetirementLease.Open(record.QuarantinePath, false))
                if (root.Entry.Identity != record.Policy.RootIdentity) throw new IOException("The quarantined root was replaced.");
            var result = await new GitWorktreeAnalyzer.GitReader(record.CommonGitDirectory, token)
                .Run("worktree", "move", "--", record.QuarantinePath, record.Policy.Path).ConfigureAwait(false);
            if (result.Code != 0) throw new IOException("Git could not restore this checkout. Its files and recovery record were kept. " + Detail(result.Error));
            record = record with { State = WorktreeRetirementState.Restored, Message = "Checkout restored to its original location. Automatic retirement paused; no branch was deleted." };
            var state = Load(); Save(state with { Policies = state.Policies.Select(p => p.Path == record.Policy.Path ? p with { Automatic = false } : p).ToArray(),
                Records = state.Records.Where(r => r.Id != record.Id).Append(record).ToArray() });
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<WorktreeRetirementRecord> FinalizeAsync(Guid id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var record = Find(id);
            if (record.State is not (WorktreeRetirementState.Quarantined or WorktreeRetirementState.Finalizing))
                throw new InvalidOperationException("Only a verified quarantined checkout can be finalized.");
            if (DateTime.UtcNow < record.FinalizeAfterUtc) throw new InvalidOperationException("The recovery grace period has not ended.");
            var assessment = await AssessAsync(record.QuarantinePath, record.Policy.IntegrationReference, token).ConfigureAwait(false);
            RequireCleanIdentity(assessment, record.Policy.Branch, record.Head, record.CommonGitDirectory);
            if (!assessment.Integrated) throw new InvalidOperationException("The captured HEAD is no longer contained in the approved integration target.");
            var recovery = await new GitWorktreeAnalyzer.GitReader(record.CommonGitDirectory, token)
                .Run("rev-parse", "--verify", "--end-of-options", record.RecoveryReference + "^{commit}").ConfigureAwait(false);
            if (recovery.Code != 0 || recovery.Output.Trim() != record.Head) throw new IOException("The captured recovery reference changed. Nothing was deleted.");
            using var ancestors = new GitWorktreeAnalyzer.MetadataGuard(token);
            ancestors.Ancestors(System.IO.Path.GetDirectoryName(record.QuarantinePath)!);
            var leases = new List<RetirementLease>();
            try
            {
                var current = Capture(record.QuarantinePath, token);
                if (!SameContents(record.Entries, current)) throw new IOException("Quarantined files changed or new files appeared. Restore or review this checkout; nothing was deleted.");
                // Acquire every exact file before mutation. Exclusive data leases
                // stop changes; directories may receive new children, which are
                // never recursively removed and make finalization partial.
                foreach (var entry in record.Entries.OrderBy(e => e.RelativePath.Length))
                {
                    token.ThrowIfCancellationRequested();
                    var lease = RetirementLease.Open(Resolve(record.QuarantinePath, entry.RelativePath), true, token); leases.Add(lease);
                    if (!SameEntry(entry, lease.Entry with { RelativePath = entry.RelativePath })) throw new IOException("A quarantined object changed before its deletion lease.");
                }
                record = record with { State = WorktreeRetirementState.Finalizing, Message = "Finalization started; committed files remain recoverable through the retained branch." }; Put(record);
                var root = leases.Single(l => l.Path.Equals(record.QuarantinePath, StringComparison.OrdinalIgnoreCase));
                foreach (var lease in leases.Where(l => !ReferenceEquals(l, root)).OrderBy(l => l.Entry.Directory).ThenByDescending(l => l.Path.Length))
                { token.ThrowIfCancellationRequested(); lease.Delete(); lease.Dispose(); }
                token.ThrowIfCancellationRequested(); root.Delete();
                // Keep the exact root delete-pending and leased while Git removes
                // its registration. The name cannot be reused as a populated
                // replacement tree between a missing-path check and Git's call.
                var removed = await new GitWorktreeAnalyzer.GitReader(record.CommonGitDirectory, token)
                    .Run("worktree", "remove", "--", record.QuarantinePath).ConfigureAwait(false);
                record = record with { State = removed.Code == 0 ? WorktreeRetirementState.Completed : WorktreeRetirementState.NeedsReview,
                    Message = removed.Code == 0 ? "Captured checkout removed; branch and recovery reference retained."
                        : "Captured files removed; Git registration needs manual repair. Branch and recovery reference retained. " + Detail(removed.Error) };
            }
            catch (Exception error) when (Expected(error))
            {
                record = record with { State = WorktreeRetirementState.NeedsReview, Message = "Finalization stopped; some captured files may have been removed. Retained recovery ref: " + record.RecoveryReference + ". " + error.Message };
                Put(record); return record;
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
            Put(record); return record;
        }
        finally { _gate.Release(); }
    }

    private static void RequireEligible(GitWorktreeAssessment assessment, int days)
    {
        RequireCleanIdentity(assessment, "refs/heads/" + assessment.Branch, assessment.Head, assessment.CommonGitDirectory);
        if (!assessment.IntegrationTargetConfirmed || !new WorktreeCleanupPolicy(WorktreeCleanupMode.OldAndIntegrated, days).Matches(assessment, DateTime.UtcNow))
            throw new InvalidOperationException("This managed checkout does not meet the explicit integration and inactivity policy. " + assessment.Reason);
    }
    private static void RequireCleanIdentity(GitWorktreeAssessment value, string branch, string? head, string? common)
    {
        if (!value.VerifiedClean || value.HasIgnoredFiles || value.OperationInProgress || value.IsDetached || value.Head is null
            || "refs/heads/" + value.Branch != branch || value.Head != head || common is null || value.CommonGitDirectory != common)
            throw new InvalidOperationException("Checkout identity, branch or local contents require review. " + value.Reason);
    }
    private static IReadOnlyList<WorktreeRetirementEntry> Capture(string root, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(10));
        token = budget.Token;
        using var guard = new GitWorktreeAnalyzer.MetadataGuard(token); guard.Ancestors(root); guard.Tree(root, true);
        var result = new List<WorktreeRetirementEntry>(); var pending = new Stack<string>(); pending.Push(root);
        long bytes = 0;
        while (pending.TryPop(out string? path))
        {
            token.ThrowIfCancellationRequested(); if (result.Count >= EntryLimit) throw new IOException("The checkout exceeds the retirement inspection budget.");
            using var lease = RetirementLease.Open(path, false, token, 256L * 1024 * 1024 - bytes);
            result.Add(lease.Entry with { RelativePath = System.IO.Path.GetRelativePath(root, path) });
            if (!lease.Entry.Directory) bytes += lease.Entry.Length;
            if (lease.Entry.Directory) foreach (string child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
        }
        return result.AsReadOnly();
    }
    private static bool SameContents(IReadOnlyList<WorktreeRetirementEntry> left, IReadOnlyList<WorktreeRetirementEntry> right)
        => left.Count == right.Count && left.OrderBy(e => e.RelativePath, StringComparer.Ordinal).Zip(right.OrderBy(e => e.RelativePath, StringComparer.Ordinal)).All(pair => SameEntry(pair.First, pair.Second));
    private static bool SameEntry(WorktreeRetirementEntry a, WorktreeRetirementEntry b) => a == b;
    private static string Resolve(string root, string relative)
    {
        if (System.IO.Path.IsPathRooted(relative) || relative.Split(['\\', '/']).Any(p => p == "..")) throw new IOException("A recovery path escapes its recorded checkout.");
        string value = System.IO.Path.GetFullPath(relative, root);
        if (value != root && !value.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("A recovery path escapes its recorded checkout.");
        return value;
    }
    private WorktreeRetirementRecord Find(Guid id) => Load().Records.SingleOrDefault(r => r.Id == id) ?? throw new IOException("The recovery record is missing.");
    private void Put(WorktreeRetirementRecord record)
    { var state = Load(); Save(state with { Records = state.Records.Where(r => r.Id != record.Id).Append(record).ToArray() }); }
    private void Save(WorktreeRetirementSettings state)
    {
        if (state.Policies.Count > 32 || state.Records.Count > 128) throw new IOException("Managed worktree record limit reached. Keep recovery records before enrolling more checkouts.");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(state);
        if (json.Length > 4 * 1024 * 1024) throw new IOException("Managed worktree recovery records exceed 4 MiB. Preserve existing records before enrolling more checkouts.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(settingsPath))!);
        string temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, json); File.Move(temporary, settingsPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool Expected(Exception error) => error is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException;
    private static string Detail(string text) => text.Trim().Length > 4096 ? text.Trim()[..4096] : text.Trim();

    private sealed partial class RetirementLease : IDisposable
    {
        private readonly SafeFileHandle _handle;
        public string Path { get; }
        public WorktreeRetirementEntry Entry { get; }
        private RetirementLease(string path, SafeFileHandle handle, WorktreeRetirementEntry entry) { Path = path; _handle = handle; Entry = entry; }
        public static unsafe RetirementLease Open(string path, bool delete, CancellationToken token = default, long byteBudget = 256L * 1024 * 1024)
        {
            using var metadata = NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), 0x80, 7, 0, 3, 0x02300000, 0);
            if (metadata.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            byte* info = stackalloc byte[1024]; Read(metadata, 0, info, 40);
            var data = new ReadOnlySpan<byte>(info, 40); var attributes = (FileAttributes)LayoutParser.U32(data, 32);
            bool directory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.ReadOnly | (FileAttributes)(0x40000 | 0x400000))) != 0)
                throw new IOException("Linked, offline or read-only checkout content requires review.");
            long written = LayoutParser.I64(data, 16), created = LayoutParser.I64(data, 0);
            Read(metadata, 18, info, 24); string identity = Convert.ToHexString(new ReadOnlySpan<byte>(info, 24));
            uint access = (directory ? 0x81u : 0x80000080u) | (delete ? 0x10000u : 0);
            var handle = NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), access, delete ? directory ? 1u : 0u : 1u, 0, 3, 0x02300000, 0);
            if (handle.IsInvalid) { int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error); }
            try
            {
                Read(handle, 18, info, 24);
                if (Convert.ToHexString(new ReadOnlySpan<byte>(info, 24)) != identity) throw new IOException("Checkout object changed before its read lease.");
                Read(handle, 1, info, 24); long length = LayoutParser.I64(new(info, 24), 8);
                if (!directory && LayoutParser.U32(new(info, 24), 16) != 1) throw new IOException("Multiply linked checkout data requires review.");
                string? hash = null;
                if (!directory)
                {
                    if (length > byteBudget) throw new IOException("The checkout exceeds the 256 MiB content-verification budget; review it manually.");
                    Read(handle, 7, info, 1024);
                    if (LayoutParser.U32(new(info, 1024), 0) != 0 || LayoutParser.U32(new(info, 1024), 4) != 14
                        || !new ReadOnlySpan<char>(info + 24, 7).SequenceEqual("::$DATA")) throw new IOException("Additional file streams require review.");
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long offset = 0;
                    int read; while ((read = RandomAccess.Read(handle, buffer, offset)) > 0) { token.ThrowIfCancellationRequested(); digest.AppendData(buffer, 0, read); offset += read; }
                    if (offset != length) throw new IOException("Checkout data changed while hashing."); hash = Convert.ToHexString(digest.GetHashAndReset());
                }
                return new(path, handle, new("", directory, identity, length, written, created, hash));
            }
            catch { handle.Dispose(); throw; }
        }
        public unsafe void Delete()
        {
            if (!Entry.Directory)
            {
                byte* buffer = stackalloc byte[1024]; Read(_handle, 7, buffer, 1024);
                if (LayoutParser.U32(new(buffer, 1024), 0) != 0 || LayoutParser.U32(new(buffer, 1024), 4) != 14
                    || !new ReadOnlySpan<char>(buffer + 24, 7).SequenceEqual("::$DATA"))
                    throw new IOException("New additional streams were found; this object was kept.");
            }
            byte value = 1; if (!SetFileInformationByHandle(_handle, 4, &value, 1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        private static unsafe void Read(SafeFileHandle handle, int kind, byte* buffer, uint length)
        { if (!NativeMethods.GetFileInformationByHandleEx(handle, kind, buffer, length)) throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        public void Dispose() => _handle.Dispose();
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte* buffer, uint length);
    }
}
