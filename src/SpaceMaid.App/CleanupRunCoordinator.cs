using System.IO;
using System.Security.Cryptography;
using System.Text;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

// Both UI and scheduled entrypoints use this host-owned workflow. Extensions
// contribute discoveries; none receives a deletion callback or wider authority.
internal sealed class CleanupRunCoordinator(FileOperations operations, CleanupRunStore store, OpportunityDiscovery discover)
{
    internal Task<CleanupPreparedRun> PrepareAsync(CleanupDefinition definition, ScanSnapshot scan,
        IReadOnlyList<Opportunity> candidates, CancellationToken token)
        => PrepareCoreAsync(definition, scan, candidates, null, token);

    internal async Task<CleanupPreparedRun> PrepareScheduledAsync(CleanupDefinition definition, ScanSnapshot scan,
        IReadOnlyList<Opportunity> candidates, CleanupRunRecord reserved, CancellationToken token)
    {
        if (reserved.Definition.Id != definition.Id || reserved.Definition.Revision != definition.Revision)
            throw new ArgumentException("The scheduled occurrence belongs to another cleanup revision.");
        if (store.IsStopRequested(reserved.Id)) throw new OperationCanceledException("This scheduled cleanup was stopped.", token);
        var current = store.LoadDefinitions().SingleOrDefault(item => item.Id == definition.Id);
        if (current is null || current.Revision != definition.Revision || !current.Enabled)
            throw new InvalidOperationException("The scheduled cleanup was changed or paused before preparation.");
        var prepared = await PrepareCoreAsync(definition, scan, candidates, reserved, token).ConfigureAwait(false);
        var persisted = store.LoadRun(reserved.Id);
        if (persisted?.State is CleanupRunState.Stopped or CleanupRunState.Superseded || store.IsStopRequested(reserved.Id))
            throw new OperationCanceledException("This scheduled cleanup was stopped.", token);
        if (!store.TryPublishScheduledPreparation(prepared.Record))
            throw new InvalidOperationException("The scheduled cleanup changed or stopped during preparation. No obsolete review was published.");
        return prepared;
    }

    private async Task<CleanupPreparedRun> PrepareCoreAsync(CleanupDefinition input, ScanSnapshot scan,
        IReadOnlyList<Opportunity> candidates, CleanupRunRecord? reserved, CancellationToken token)
    {
        var definition = input.Validate();
        if (!string.Equals(definition.DiscoveryRootPath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(scan.RootPath)), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The cleanup scan does not match its saved discovery location.");
        bool initialCapture = definition.RootIdentity is null;
        var root = definition.RootIdentity ?? await Task.Run(() => FolderDeletionTarget.CaptureScopeIdentity(scan.RootPath, operations.Files), token).ConfigureAwait(false);
        await Task.Run(() => FolderDeletionTarget.ValidateScopeIdentity(root, operations.Files), token).ConfigureAwait(false);
        var scopes = definition.ScopePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopeIdentities = definition.ScopeIdentities.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var blockedScopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string scope in definition.ScopePaths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var identity = scopeIdentities.GetValueOrDefault(scope)
                    ?? (string.Equals(scope, root.Path, StringComparison.OrdinalIgnoreCase) ? root : null);
                if (identity is null)
                {
                    if (!initialCapture) throw new InvalidOperationException("This scope has no reviewed identity. Update the cleanup scope before using it.");
                    identity = await Task.Run(() => FolderDeletionTarget.CaptureScopeIdentity(scope, operations.Files), token).ConfigureAwait(false);
                }
                await Task.Run(() => FolderDeletionTarget.ValidateScopeIdentity(identity, operations.Files), token).ConfigureAwait(false);
                scopeIdentities[identity.Path] = identity;
            }
            catch (Exception error) when (CleanupBatch.IsExpected(error) && error is not OperationCanceledException)
            { blockedScopes[scope] = "Cleanup location could not be verified · " + error.Message; }
        }
        definition = definition with { RootIdentity = root, ScopeIdentities = scopeIdentities.Values.ToArray() };
        var captured = candidates.Where(item => CleanupDefinition.InScopes(item.Path, scopes) && definition.MatchesRule(item))
            .DistinctBy(Key).ToArray();
        var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in captured)
        {
            if (definition.Excludes(item.Path)) blocked[item.Path] = "Excluded folder or contains a folder exclusion · Kept";
            foreach (var scope in blockedScopes)
                if (OptimizationPolicy.Covers(scope.Key, item.Path) || OptimizationPolicy.Covers(item.Path, scope.Key)) blocked[item.Path] = scope.Value;
        }
        AddUncertainBlocks(captured, blocked, reserved?.Id);
        if (definition.Schedule?.Automatic == true)
        {
            var keys = definition.AutomaticRuleConsents.ToHashSet(StringComparer.Ordinal);
            if (!definition.Enabled && keys.Count == 0)
            {
                keys.UnionWith(captured.Where(item => item.IsBuildOutput && item.IsRegeneratable).Select(BuildConsentKey));
                definition = definition with { AutomaticRuleConsents = keys.Order(StringComparer.Ordinal).ToArray() };
            }
            foreach (var item in captured)
                if (!item.IsBuildOutput || !keys.Contains(BuildConsentKey(item)))
                    blocked[item.Path] = "This build rule has not been approved for automatic cleanup · Kept";
        }
        var basis = await operations.PrepareCleanupRuleCandidatesAsync(scan, captured, token, blocked, discover).ConfigureAwait(false);
        var plan = await operations.ReviewCleanupRulesAsync(basis, new(definition.InactiveDays), definition.BuildAction, discover, token).ConfigureAwait(false);
        if (definition.Worktrees)
        {
            var policies = captured.Where(item => item.IsWorktree).ToDictionary(item => item.Path, _ => definition.WorktreePolicy, StringComparer.Ordinal);
            var gitBlocked = await operations.CheckWorktreesAsync(scan, plan.Preview.Locations, policies, token).ConfigureAwait(false);
            if (gitBlocked.Count > 0)
            {
                var kept = plan.Preview.Locations.Where(item => gitBlocked.ContainsKey(item.Path)).Select(item => new CleanupBlockedLocation(item, gitBlocked[item.Path])).ToArray();
                plan = await operations.RestrictCleanupAsync(plan, plan.Preview.Locations.Where(item => !gitBlocked.ContainsKey(item.Path)).ToArray(), token).ConfigureAwait(false);
                plan = plan.WithAdditionalBlockedLocations(kept);
            }
        }
        plan = await operations.RestrictCleanupAsync(plan, plan.Preview.Locations, token).ConfigureAwait(false);
        var validTargets = new List<FolderDeletionTarget>();
        var invalidTargets = new List<CleanupBlockedLocation>();
        foreach (var target in plan.Targets)
        {
            try { validTargets.Add(FolderDeletionTarget.RestoreCapturedIdentity(target.ExportCapturedIdentity(), operations.Files)); }
            catch (Exception error) when (CleanupBatch.IsExpected(error))
            { invalidTargets.Add(new(plan.Preview.Locations.First(item => item.Path == target.Path), error.Message)); }
        }
        if (invalidTargets.Count > 0)
        {
            var validPaths = validTargets.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            plan = await operations.RestrictCleanupAsync(plan, plan.Preview.Locations.Where(item => validPaths.Contains(item.Path)).ToArray(), token).ConfigureAwait(false);
            plan = plan.WithAdditionalBlockedLocations(invalidTargets);
        }
        plan = plan.WithCapturedTargets(validTargets);
        var identities = plan.Targets.ToDictionary(item => item.Path, item => item.ExportCapturedIdentity(), StringComparer.OrdinalIgnoreCase);
        var targets = plan.Preview.Locations.Select(item => new CleanupRunTarget(item, identities[item.Path], "Ready", "Matches the selected cleanup rule.")
            { Assessment = plan.BuildAssessments.GetValueOrDefault(item.DirectoryIndex) })
            .Concat(plan.BlockedLocations.Select(item => new CleanupRunTarget(item.Opportunity, null, "Kept", item.Reason)
                { Assessment = plan.BuildAssessments.GetValueOrDefault(item.Opportunity.DirectoryIndex) })).DistinctBy(item => Key(item.Location)).ToArray();
        var record = (reserved ?? new CleanupRunRecord()) with { Definition = definition, ScanRootPath = scan.RootPath, RootIdentity = root,
            ScannedAtUtc = scan.ScannedAtUtc, UsesLogicalSize = plan.Preview.UsesLogicalSize, UpdatedUtc = DateTimeOffset.UtcNow, Targets = targets,
            State = reserved is null || definition.Schedule?.Automatic != true ? CleanupRunState.Review : CleanupRunState.Queued };
        return new(definition, plan, record);
    }

    internal async Task<CleanupPreparedRun> RestorePreparedAsync(CleanupRunRecord record, CancellationToken token)
    {
        if (record.State != CleanupRunState.Review) throw new InvalidOperationException("Only a pending review can be approved. Open its recorded results instead.");
        if (record.RootIdentity is null) throw new InvalidDataException("This review has no captured cleanup location.");
        await Task.Run(() => FolderDeletionTarget.ValidateScopeIdentity(record.RootIdentity, operations.Files), token).ConfigureAwait(false);
        var scan = await operations.MeasureFolderAsync(record.ScanRootPath, token).ConfigureAwait(false);
        var findings = discover(scan, token);
        var byKey = findings.ToLookup(Key);
        var selected = new List<Opportunity>(); var originalTargets = new Dictionary<(string Provider, string Rule, string Path), FolderDeletionTarget>();
        var kept = new List<CleanupBlockedLocation>();
        foreach (var captured in record.Targets)
        {
            token.ThrowIfCancellationRequested();
            if (captured.Decision != "Ready" || captured.Identity is null)
            { kept.Add(new(captured.Location, captured.Reason)); continue; }
            try
            {
                if (record.Receipts.Any(receipt => Key(receipt) == Key(captured.Location)))
                    throw new InvalidOperationException("This captured folder already has a recorded outcome. It cannot be retried from the old approval.");
                var target = FolderDeletionTarget.RestoreCapturedIdentity(captured.Identity, operations.Files);
                var current = byKey[Key(captured.Location)].FirstOrDefault()
                    ?? throw new InvalidOperationException("The captured folder is missing or no longer matches its original detection rule.");
                selected.Add(current); originalTargets.Add(Key(current), target);
            }
            catch (Exception error) when (CleanupBatch.IsExpected(error)) { kept.Add(new(captured.Location, error.Message)); }
        }
        // Fresh scanning supplies classification only. The deletion targets are
        // replaced below with the original persisted identities, never live ones.
        var prepared = await PrepareCoreAsync(record.Definition, scan, selected, record, token).ConfigureAwait(false);
        var plan = prepared.Plan.WithCapturedTargets(prepared.Plan.Preview.Locations.Select(item => originalTargets[Key(item)]).ToArray())
            .WithAdditionalBlockedLocations(kept);
        return new(record.Definition, plan, record);
    }

    internal async Task<CleanupBatchResult> RunAsync(CleanupPreparedRun prepared, nint owner,
        IProgress<CleanupProgress> progress, CancellationToken token)
    {
        var record = ApprovalRecord(prepared);
        var existing = store.LoadRun(record.Id);
        if (existing is not null && existing.State is not (CleanupRunState.Review or CleanupRunState.Queued))
            throw new InvalidOperationException("This cleanup run already started or finished. Open its results instead.");
        record = record with { State = CleanupRunState.Queued, ApprovedUtc = DateTimeOffset.UtcNow,
            ApprovalClaimId = Guid.NewGuid(), OwnerProcessId = Environment.ProcessId, OwnerStartedUtc = CleanupRunOwnership.CurrentStartedUtc };
        if (!store.TryQueueRun(record)) throw new InvalidOperationException("This cleanup was already handled, or the queue is full. Open its current state before trying again.");
        try { return await CleanupMutationLease.RunAsync(async () =>
        {
            using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
            bool automatic = record.Source.StartsWith("Scheduled", StringComparison.Ordinal) && record.Definition.Schedule?.Automatic == true;
            if (automatic) work.CancelAfter(TimeSpan.FromMinutes(record.Definition.Limits.MaxMinutes));
            var claimed = store.LoadRun(record.Id);
            if (claimed?.State != CleanupRunState.Queued || claimed.ApprovalClaimId != record.ApprovalClaimId)
                throw new InvalidOperationException("This cleanup run was already handled or dismissed while waiting.");
            var active = record with { State = CleanupRunState.Running, UpdatedUtc = DateTimeOffset.UtcNow };
            var plan = prepared.Plan;
            bool stopped = store.IsStopRequested(record.Id);
            string? stopReason = stopped ? "Stopped before cleanup started." : ScheduledBlockReason(record);
            if (stopReason is not null) stopped = true;
            var runtimeBlocks = new Dictionary<string, string>(StringComparer.Ordinal);
            AddUncertainBlocks(plan.Preview.Locations, runtimeBlocks, record.Id);
            if (automatic) AddAutomaticConflicts(plan, record.Definition, runtimeBlocks);
            if (runtimeBlocks.Count > 0)
            {
                var blocked = plan.Preview.Locations.Where(item => runtimeBlocks.ContainsKey(item.Path)).Select(item => new CleanupBlockedLocation(item, runtimeBlocks[item.Path])).ToArray();
                plan = await operations.RestrictCleanupAsync(plan, plan.Preview.Locations.Where(item => !runtimeBlocks.ContainsKey(item.Path)).ToArray(), work.Token).ConfigureAwait(false);
                plan = plan.WithAdditionalBlockedLocations(blocked);
            }
            if (automatic) plan = await ApplyLimitsAsync(plan, record.Definition.Limits, work.Token).ConfigureAwait(false);
            if (stopped) plan = await operations.RestrictCleanupAsync(plan, [], CancellationToken.None).ConfigureAwait(false);
            store.SaveRun(active);
            var receipts = active.Receipts.ToDictionary(Key);
            var scopeIdentities = active.Definition.ScopeIdentities.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
            bool journalFailed = false;
            void Append(CleanupTargetReceipt receipt)
            {
                try { store.AppendReceipt(active.Id, receipt); receipts[Key(receipt)] = receipt; }
                catch { journalFailed = true; work.Cancel(); throw; }
            }
            bool ShouldStop()
            {
                if (work.IsCancellationRequested || journalFailed || stopped) return true;
                try
                {
                    if (store.IsStopRequested(active.Id)) { stopped = true; work.Cancel(); return true; }
                    return false;
                }
                catch { journalFailed = true; work.Cancel(); return true; }
            }
            var journal = new CleanupExecutionJournal(location =>
            {
                if (ShouldStop()) throw new OperationCanceledException(work.Token);
                Append(new(location.Path, location.ProviderId, location.RuleId, CleanupOutcome.Uncertain,
                    "Mutation started; its outcome has not been confirmed.", true, DateTimeOffset.UtcNow));
            }, result => Append(ToReceipt(result)), ShouldStop)
            {
                ValidateTarget = location =>
                {
                    if (active.RootIdentity is not null) FolderDeletionTarget.ValidateScopeIdentity(active.RootIdentity, operations.Files);
                    for (string? path = location.Path; path is not null; path = Path.GetDirectoryName(path))
                        if (scopeIdentities.TryGetValue(path, out var scope)) FolderDeletionTarget.ValidateScopeIdentity(scope, operations.Files);
                }
            };
            using var monitorStop = new CancellationTokenSource();
            var watcher = WatchStopAsync(active.Id, work, monitorStop.Token);
            CleanupBatchResult result;
            try
            {
                var policies = prepared.Definition.Worktrees ? prepared.Plan.Preview.Locations.Where(item => item.IsWorktree)
                    .ToDictionary(item => item.Path, _ => prepared.Definition.WorktreePolicy, StringComparer.Ordinal) : new Dictionary<string, WorktreeCleanupPolicy>();
                OpportunityDiscovery runDiscovery = automatic ? (current, cancellation) => discover(current, cancellation).Select(item =>
                    item.IsBuildOutput && !record.Definition.AutomaticRuleConsents.Contains(BuildConsentKey(item), StringComparer.Ordinal)
                        ? item with { IsRegeneratable = false, CleanupBlockReason = "This automatic build rule changed and needs renewed approval · Kept" } : item).ToArray() : discover;
                result = await CleanupBatch.ExecuteJournaledAsync(plan, owner, progress, work.Token, (_, _) => { }, policies, runDiscovery, operations, journal).ConfigureAwait(false);
                // Global validation failure and targets excluded before the
                // execution loop still receive complete per-target receipts.
                foreach (var item in result.Locations)
                    if (!receipts.ContainsKey(Key(item.Location))) Append(ToReceipt(item));
                foreach (var target in active.Targets.Where(item => item.Decision == "Ready"))
                    if (!receipts.ContainsKey(Key(target.Location))) Append(new(target.Location.Path, target.Location.ProviderId, target.Location.RuleId,
                        CleanupOutcome.NotProcessed, stopReason ?? "Not started in this cleanup run.", false, DateTimeOffset.UtcNow));
                bool uncertain = receipts.Values.Any(item => item.Outcome is CleanupOutcome.Partial or CleanupOutcome.Uncertain
                    || item.Attempted && item.Outcome is CleanupOutcome.Failed or CleanupOutcome.Cancelled);
                var state = uncertain || journalFailed ? CleanupRunState.NeedsAttention
                    : work.IsCancellationRequested || stopped ? CleanupRunState.Stopped : CleanupRunState.Completed;
                store.SaveRun(active with { State = state, Receipts = receipts.Values.ToArray(), UpdatedUtc = DateTimeOffset.UtcNow,
                    Message = (stopReason ?? result.ValidationError ?? result.Summary)
                        + (active.Message?.StartsWith("Discovery: ", StringComparison.Ordinal) == true ? " " + active.Message : "") });
                var locations = active.Targets.ToDictionary(item => Key(item.Location), item => item.Location);
                var liveResults = result.Locations.GroupBy(item => Key(item.Location)).ToDictionary(group => group.Key, group => group.Last());
                return result with { Locations = receipts.Values.Where(item => locations.ContainsKey(Key(item))).Select(item =>
                    liveResults.GetValueOrDefault(Key(item)) ?? new CleanupLocationResult(locations[Key(item)], item.Outcome, item.Message, item.Attempted,
                        item.Outcome == CleanupOutcome.Recycled && item.RecycledPath is not null ? new(false, item.RecycledPath) : null)).ToArray() };
            }
            catch (Exception error) when (CleanupBatch.IsExpected(error))
            {
                if (!journalFailed)
                {
                    store.SaveRun(active with { State = receipts.Values.Any(item => item.Attempted) ? CleanupRunState.NeedsAttention : CleanupRunState.Stopped,
                        Receipts = receipts.Values.ToArray(), UpdatedUtc = DateTimeOffset.UtcNow, Message = error.Message });
                }
                throw;
            }
            finally { monitorStop.Cancel(); await watcher.ConfigureAwait(false); }
        }, token).ConfigureAwait(false); }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        {
            // The lease has ended. Retry terminal persistence from durable
            // receipts now, or on a later status refresh if storage is down.
            store.MarkWorkerEnded(record.Id, record.ApprovalClaimId!.Value,
                error is OperationCanceledException ? "Cleanup stopped. No cleanup resumed automatically." : "Cleanup stopped. " + error.Message);
            try { store.RecoverInterruptedRuns(); }
            catch (Exception persistence) when (CleanupBatch.IsExpected(persistence)) { /* Preserve the journal and deferred finalization. */ }
            throw;
        }
    }

    private CleanupRunRecord ApprovalRecord(CleanupPreparedRun prepared)
    {
        if (prepared.Plan.BuildAction != prepared.Definition.BuildAction || prepared.Plan.BuildPolicy.KeepRecentDays != prepared.Definition.InactiveDays)
            throw new InvalidOperationException("The displayed cleanup policy changed. Update its estimate before approving it.");
        var original = prepared.Record.Targets.ToDictionary(item => Key(item.Location));
        var allowed = prepared.Plan.Preview.Locations.ToDictionary(Key);
        foreach (var target in prepared.Plan.Targets)
        {
            var key = Key(allowed.Values.First(item => item.Path == target.Path));
            if (!original.TryGetValue(key, out var captured) || captured.Decision != "Ready" || captured.Identity != target.ExportCapturedIdentity())
                throw new InvalidOperationException("Approval may only narrow the captured cleanup targets.");
        }
        return prepared.Record with { Definition = prepared.Definition, Targets = prepared.Record.Targets.Select(item => item.Decision == "Ready" && !allowed.ContainsKey(Key(item.Location))
            ? item with { Decision = "Skipped", Reason = "Skipped for this run." } : item).ToArray() };
    }

    private void AddUncertainBlocks(IEnumerable<Opportunity> candidates, Dictionary<string, string> blocked, Guid? except)
    {
        // A live journal can be newer than its compact header. Active states
        // must always inspect that journal, including older cached headers that
        // did not mark a running operation as potentially uncertain.
        var uncertainPaths = store.LoadRunHeaders().Where(run => run.Id != except && (run.HasUncertainOutcome
                || run.State is CleanupRunState.Running or CleanupRunState.Queued))
            .Select(run => store.LoadRun(run.Id)).Where(run => run is not null).SelectMany(run => run!.Receipts)
            .Where(item => item.Outcome is CleanupOutcome.Partial or CleanupOutcome.Uncertain || item.Attempted && item.Outcome is CleanupOutcome.Failed or CleanupOutcome.Cancelled)
            .Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var item in candidates)
            if (uncertainPaths.Any(path => OptimizationPolicy.Covers(path, item.Path) || OptimizationPolicy.Covers(item.Path, path)))
                blocked[item.Path] = "An earlier cleanup outcome needs checking. Inspect its original receipt before retrying · Kept";
    }

    private string? ScheduledBlockReason(CleanupRunRecord record)
    {
        if (!record.Source.StartsWith("Scheduled", StringComparison.Ordinal)) return null;
        var current = store.LoadDefinitions().SingleOrDefault(item => item.Id == record.Definition.Id);
        if (current is null || current.Revision != record.Definition.Revision) return "The saved cleanup changed before this run started.";
        if (record.Definition.Schedule?.Automatic == true && (!current.Enabled || current.RegisteredRevision != current.Revision))
            return "The automatic cleanup is paused or its schedule is unavailable.";
        return null;
    }

    private void AddAutomaticConflicts(CleanupPlan plan, CleanupDefinition definition, Dictionary<string, string> blocked)
    {
        var others = store.LoadDefinitions().Where(item => item.Id != definition.Id && item.Enabled && item.Schedule?.Automatic == true
            && item.Build && item.BuildAction != definition.BuildAction).ToArray();
        foreach (var item in plan.Preview.Locations)
            if (others.Any(other => other.Includes(item) && BuildCleanupProtection.Assess(plan.Snapshot, [item], new(other.InactiveDays))[item.DirectoryIndex].State == CleanupReadiness.Ready))
                blocked[item.Path] = "Enabled automatic rules disagree about recycling or permanent deletion · Kept";
    }

    private async Task<CleanupPlan> ApplyLimitsAsync(CleanupPlan plan, CleanupRunLimits limits, CancellationToken token)
    {
        long bytes = 0; var selected = new List<Opportunity>(); var blocked = new List<CleanupBlockedLocation>();
        foreach (var item in plan.Preview.Locations)
        {
            long size = Math.Max(0, item.DisplayBytes(plan.Snapshot.HasAllocationData));
            if (selected.Count >= limits.MaxTargets || size > limits.MaxBytes - bytes)
                blocked.Add(new(item, "Outside this automatic run's folder or byte limit · Kept"));
            else { selected.Add(item); bytes += size; }
        }
        return (await operations.RestrictCleanupAsync(plan, selected, token).ConfigureAwait(false)).WithAdditionalBlockedLocations(blocked);
    }

    private async Task WatchStopAsync(Guid id, CancellationTokenSource work, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                if (store.IsStopRequested(id)) { work.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { work.Cancel(); }
    }

    internal static string BuildConsentKey(Opportunity item)
    {
        string semantics = "verified-build-folder-v1\n" + item.ProviderId + "\n" + item.RuleId + "\n" + item.Reason + "\n" + item.Caution + "\n" + item.SuggestedAction;
        return item.ProviderId + "/" + item.RuleId + "/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(semantics)))[..16];
    }
    private static CleanupTargetReceipt ToReceipt(CleanupLocationResult result)
        => new(result.Location.Path, result.Location.ProviderId, result.Location.RuleId, result.Outcome, result.Message, result.Attempted, DateTimeOffset.UtcNow)
        { RecycledPath = result.Outcome == CleanupOutcome.Recycled && result.Recycle?.Cancelled == false ? result.Recycle.RecycledPath : null };
    private static (string Provider, string Rule, string Path) Key(Opportunity item) => (item.ProviderId, item.RuleId, item.Path);
    private static (string Provider, string Rule, string Path) Key(CleanupTargetReceipt item) => (item.ProviderId, item.RuleId, item.Path);
}
