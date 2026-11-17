namespace SpaceMaid.Core.Analysis;

public sealed partial class WorktreeRetirement
{
    // Reconciliation checks the original receipt; it never replays deletion or
    // captures a replacement set of files as new authority to finalize.
    public Task<WorktreeRetirementRecord> ReconcileAsync(Guid id, CancellationToken token = default)
        => CleanupMutationLease.RunAsync(() => ReconcileCapturedAsync(id, token), token);

    private async Task<WorktreeRetirementRecord> ReconcileCapturedAsync(Guid id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var record = Find(id);
            if (record.State is WorktreeRetirementState.Completed or WorktreeRetirementState.Restored)
                return record;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                using var guard = new GitWorktreeAnalyzer.MetadataGuard(budget.Token);
                guard.Ancestors(record.CommonGitDirectory);
                string config = System.IO.Path.Join(record.CommonGitDirectory, "config");
                guard.File(config);
                GitWorktreeAnalyzer.RejectUnsafeConfig(config);
                guard.Ancestors(System.IO.Path.GetDirectoryName(record.QuarantinePath)!);
                var git = new GitWorktreeAnalyzer.GitReader(record.CommonGitDirectory, budget.Token);
                var recovery = await git.Run("rev-parse", "--verify", "--end-of-options", record.RecoveryReference + "^{commit}").ConfigureAwait(false);
                var branch = await git.Run("rev-parse", "--verify", "--end-of-options", record.Policy.Branch + "^{commit}").ConfigureAwait(false);
                if (recovery.Code != 0 || recovery.Output.Trim() != record.Head || branch.Code != 0 || branch.Output.Trim() != record.Head)
                    return Review("The retained branch or recovery reference changed or could not be verified. Review them in Git before continuing; no file operation was retried.");
                var listed = await git.Run("worktree", "list", "--porcelain", "-z").ConfigureAwait(false);
                if (listed.Code != 0) return Review("Git registration could not be read. Retry Recheck recovery when the repository is available.");
                var registration = GitWorktreeAnalyzer.Registered(listed.Output, record.QuarantinePath);
                if (!RecoveryPathExists(record.QuarantinePath))
                {
                    if (registration is not null)
                        return Review("The recorded folder is absent, but Git still lists its registration. No deletion was retried. "
                            + "In the shared repository, inspect git worktree list --porcelain and review removal of only this missing checkout's registration. "
                            + "Keep other worktrees, the branch and the recovery reference, then choose Recheck recovery.");
                    if (RecoveryPathExists(record.Policy.Path))
                        return Review("The quarantine path and its registration are absent, but the original location is occupied. Review that location in Git before closing this recovery record; it was left untouched.");
                    record = record with { State = WorktreeRetirementState.Completed,
                        Message = "Reconciled: the quarantined folder and its Git registration are absent. The exact branch and recovery reference remain verified. No deletion was retried." };
                    return Persist(record);
                }
                using (var root = RetirementLease.Open(record.QuarantinePath, false, budget.Token))
                    if (root.Entry.Identity != record.Policy.RootIdentity)
                        return Review("The quarantine path now belongs to a different object. It was left untouched. Review the recorded paths and recover the retained commit into a separate folder in Git.");
                if (registration is null || registration.GetValueOrDefault("branch") != record.Policy.Branch
                    || registration.ContainsKey("locked") || registration.ContainsKey("prunable"))
                    return Review("The surviving folder's Git registration needs review. Keep its remaining files; inspect the exact paths and retained reference in Git before restoring it.");
                var remaining = Capture(record.QuarantinePath, budget.Token);
                if (!SameContents(record.Entries, remaining))
                {
                    if (remaining.SingleOrDefault(entry => entry.RelativePath == ".git") is { } pointer && RecoveryPointerMatches(record, pointer))
                    {
                        record = record with { State = WorktreeRetirementState.RestoreOnly,
                            Message = "Some recorded files are missing or changed. All surviving files were kept; finalization remains blocked. "
                                + "The original Git pointer and registration still match. Choose Restore selected to move the remaining checkout back, then recover missing tracked files from the retained reference in Git. Automatic retirement is paused." };
                        return Persist(record);
                    }
                    return Review("Some recorded files or Git metadata are missing or changed. All surviving files were kept; Finalize and Restore remain unavailable until Git metadata is repaired. "
                        + "Preserve the surviving folder and recover missing tracked files from the retained reference in Git. Inspect the exact Git registration before repairing its pointer.");
                }
                var assessment = await AssessAsync(record.QuarantinePath, record.Policy.IntegrationReference, budget.Token).ConfigureAwait(false);
                RequireCleanIdentity(assessment, record.Policy.Branch, record.Head, record.CommonGitDirectory);
                if (!assessment.Integrated)
                    return Review("The captured HEAD is no longer contained in the approved integration target. Restore the checkout or review its integration in Git.");
                record = record with { State = WorktreeRetirementState.Quarantined,
                    Message = "Recovery rechecked: all original captured contents, identities and Git state still match. Choose Restore selected or explicitly finalize after the recovery grace period. Automatic retirement is paused." };
                return Persist(record);
            }
            catch (Exception error) when (Expected(error))
            {
                string reason = error is OperationCanceledException
                    ? token.IsCancellationRequested ? "Recovery recheck cancelled." : "Recovery recheck timed out."
                    : "Recovery could not be fully verified: " + error.Message;
                return Review(reason + " Existing files and captured contents were kept; retry Recheck recovery or inspect the recorded paths.");
            }

            WorktreeRetirementRecord Review(string message)
            {
                record = record with { State = WorktreeRetirementState.NeedsReview, Message = message };
                return Persist(record);
            }
            WorktreeRetirementRecord Persist(WorktreeRetirementRecord updated)
            {
                var state = Load();
                // Recovered state and the pause are one durable transition.
                // A failed second write must never leave automatic finalization
                // enabled after asking the user for a new explicit decision.
                Save(state with { Policies = state.Policies.Select(policy => policy.Path.Equals(record.Policy.Path, StringComparison.OrdinalIgnoreCase)
                    ? policy with { Automatic = false } : policy).ToArray(),
                    Records = state.Records.Where(item => item.Id != updated.Id).Append(updated).ToArray() });
                return updated;
            }
        }
        finally { _gate.Release(); }
    }

    private bool RecoveryPathExists(string path)
    {
        // Exists() would conflate access denial with absence. Only positive
        // not-found errors allow the registration-only reconciliation branch.
        try { _ = _files.GetAttributes(path); return true; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static bool RecoveryPointerMatches(WorktreeRetirementRecord record, WorktreeRetirementEntry current)
        => record.Entries.SingleOrDefault(entry => entry.RelativePath == ".git") is { Directory: false } captured && captured == current;
}
