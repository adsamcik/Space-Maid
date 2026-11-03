using System.Globalization;
using System.IO;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal static class CleanupScheduledEntrypoint
{
    internal static bool TryParse(IReadOnlyList<string> arguments, out Guid definitionId, out int revision)
    {
        definitionId = Guid.Empty; revision = 0;
        return arguments.Count == 3 && arguments[0] == "--cleanup-scheduled"
            && Guid.TryParseExact(arguments[1], "D", out definitionId) && definitionId != Guid.Empty
            && int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision > 0;
    }

    internal static Task<int> RunAsync(Guid definitionId, int revision, CancellationToken token)
    {
        var operations = new FileOperations();
        var store = new CleanupRunStore(operations.Files, AppData.File("Cleanup"));
        return RunOwnedProfileAsync(definitionId, revision, operations, store, new WindowsCleanupTaskScheduler(store.DirectoryPath),
            () => CleanupTaskLaunch.Resolve(operations.Files), TimeProvider.System, AppData.File("Extensions"),
            AppData.File(Path.Join("Logs", "ScheduledDiscovery")), token);
    }

    // Composition injection is internal only. The public process entrypoint
    // still accepts just a saved rule ID/revision, never caller-supplied paths.
    internal static async Task<int> RunOwnedProfileAsync(Guid definitionId, int revision, FileOperations operations,
        CleanupRunStore store, ICleanupTaskScheduler scheduler, Func<CleanupTaskLaunch> launcher, TimeProvider clock,
        string extensionDataRoot, string extensionLogDirectory, CancellationToken token)
    {
        DateTimeOffset arrived = clock.GetUtcNow();
        CleanupRunRecord? reserved = null;
        try
        {
            // Recovery uses exact dead-owner proof under the store's short
            // metadata CAS lock. Waiting for the mutation lane here could turn
            // an on-time invocation into a late/missed occurrence.
            store.RecoverInterruptedRuns();
            var definition = store.LoadDefinitions().FirstOrDefault(item => item.Id == definitionId && item.Revision == revision);
            if (definition is not { Enabled: true, Schedule: { } schedule } || definition.RegisteredRevision != revision) return 0;
            if (schedule.TimeZoneId != TimeZoneInfo.Local.Id)
            {
                store.SaveDefinition(definition with { Enabled = false, RegisteredRevision = null,
                    ScheduleError = "The PC time zone changed. Review this schedule before enabling it again." });
                return 0;
            }
            var now = arrived;
            var occurrence = CleanupScheduleClock.Latest(schedule, now);
            if (definition.ScheduleActivatedUtc is not { } activated || occurrence.ScheduledForUtc <= activated) return 0;
            if (now - occurrence.ScheduledForUtc > CleanupScheduleService.LaunchTolerance)
            { store.TrySaveOccurrence(CleanupScheduleService.Missed(definition, occurrence, now)); return 0; }
            reserved = new() { Definition = definition, ScanRootPath = definition.DiscoveryRootPath, Source = "Scheduled", State = CleanupRunState.Queued,
                OccurrenceId = occurrence.Id, ScheduledForUtc = occurrence.ScheduledForUtc, CreatedUtc = now, UpdatedUtc = now,
                OwnerProcessId = Environment.ProcessId, OwnerStartedUtc = CleanupRunOwnership.CurrentStartedUtc,
                Message = "Preparing the scheduled cleanup." };
            if (!store.TrySaveOccurrence(reserved)) return 0;

            using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
            work.CancelAfter(TimeSpan.FromMinutes(definition.Limits.MaxMinutes));
            await using var stopWatch = new Timer(_ =>
            {
                try { if (store.IsStopRequested(reserved.Id)) work.Cancel(); }
                catch (Exception error) when (CleanupBatch.IsExpected(error)) { work.Cancel(); }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            // Reserve the actual arrival before native verification or other
            // work can queue. Reconciliation sees that occurrence as admitted.
            await new CleanupScheduleService(store, scheduler, launcher, clock).ReconcileAsync(work.Token);
            if (definition.RootIdentity is null) throw new InvalidOperationException("The saved scope has no captured identity. Review it again.");
            await Task.Run(() => FolderDeletionTarget.ValidateScopeIdentity(definition.RootIdentity, operations.Files), work.Token);
            ScanSnapshot? scan = null;
            await using var extensions = new ExtensionRuntime(() => scan, () => false,
                _ => throw new InvalidOperationException("Navigation is unavailable during scheduled cleanup."),
                dataRoot: extensionDataRoot, logDirectory: extensionLogDirectory, operations: operations);
            await extensions.InitializeDiscoveryAsync(work.Token);
            if (extensions.SettingsError is not null) throw new InvalidOperationException(extensions.SettingsError);
            await Task.Run(FileTypeClassifier.Prepare, work.Token);
            scan = await operations.ScanAsync(definition.DiscoveryRootPath, cancellationToken: work.Token);
            var candidates = await Task.Run(() => extensions.Opportunities.Find(scan, work.Token), work.Token);
            string? discoveryNote = DiscoveryNote(definition, extensions.Entries, extensions.Opportunities.Status(scan));
            reserved = reserved with { Message = schedule.Automatic ? discoveryNote is null ? null : "Discovery: " + discoveryNote
                : (candidates.Any(definition.Includes) ? "Review ready. Approve the captured folders to start cleanup." : "No matching folders are ready for review.")
                    + (discoveryNote is null ? "" : " " + discoveryNote) };
            var coordinator = new CleanupRunCoordinator(operations, store, extensions.Opportunities.Find);
            var prepared = await coordinator.PrepareScheduledAsync(definition, scan, candidates, reserved, work.Token);
            if (!schedule.Automatic) return 0;
            await coordinator.RunAsync(prepared, 0, new QuietProgress(), work.Token);
            return 0;
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || CleanupScheduleService.ScheduleError(error))
        {
            if (reserved is not null)
            {
                try
                {
                    var current = store.LoadRun(reserved.Id);
                    if (current is null)
                        store.TrySaveOccurrence(reserved with { State = CleanupRunState.Stopped, UpdatedUtc = DateTimeOffset.UtcNow,
                            Message = "Scheduled cleanup was not started. " + error.Message });
                    else if (current.State == CleanupRunState.Queued && current.ApprovalClaimId is null
                        && current.OwnerProcessId == Environment.ProcessId && current.OwnerStartedUtc == CleanupRunOwnership.CurrentStartedUtc)
                        store.SaveRun(current with { State = CleanupRunState.Stopped,
                            UpdatedUtc = DateTimeOffset.UtcNow, Message = "Scheduled preparation stopped. " + error.Message });
                }
                catch (Exception persistence) when (CleanupBatch.IsExpected(persistence)) { /* Existing journal remains the recovery authority. */ }
            }
            return 1;
        }
    }

    internal static string? DiscoveryNote(CleanupDefinition definition, IReadOnlyList<LoadedExtension> entries, string catalogStatus)
    {
        var notes = new List<string>();
        if ((definition.Build || definition.Worktrees) && !entries.Any(entry => entry.Id == "space-maid.developer" && entry.Status == "Loaded"))
            notes.Add("The Developer cleanup provider is unavailable; its folders were not included.");
        var failed = entries.Where(entry => entry.Status is "Failed to load" or "Unavailable").Take(6).Select(entry => entry.Name).ToArray();
        if (failed.Length > 0) notes.Add("Some providers could not load: " + string.Join(", ", failed) + ". Open Extensions for details.");
        if (!string.IsNullOrWhiteSpace(catalogStatus)) notes.Add(catalogStatus + ".");
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    private sealed class QuietProgress : IProgress<CleanupProgress> { public void Report(CleanupProgress value) { } }
}
