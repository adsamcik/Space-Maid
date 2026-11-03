using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

// Saving is deliberately two-stage: the stored inactive revision immediately
// invalidates old invocations; only a verified matching task enables the rule.
internal sealed class CleanupScheduleService
{
    internal static readonly TimeSpan LaunchTolerance = TimeSpan.FromMinutes(5);
    private readonly CleanupRunStore _store;
    private readonly ICleanupTaskScheduler _scheduler;
    private readonly Func<CleanupTaskLaunch> _launcher;
    private readonly TimeProvider _clock;
    private readonly string _registrationMutex;

    internal CleanupScheduleService(FileOperations operations, CleanupRunStore store)
        : this(store, new WindowsCleanupTaskScheduler(store.DirectoryPath), () => CleanupTaskLaunch.Resolve(operations.Files), TimeProvider.System) { }

    internal CleanupScheduleService(CleanupRunStore store, ICleanupTaskScheduler scheduler,
        Func<CleanupTaskLaunch> launcher, TimeProvider clock)
    {
        _store = store; _scheduler = scheduler; _launcher = launcher; _clock = clock;
        _registrationMutex = @"Global\SpaceMaid.CleanupSchedules." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(store.DirectoryPath.ToUpperInvariant())))[..24];
    }

    internal Task<CleanupDefinition> SaveAsync(CleanupDefinition definition, CancellationToken token = default)
        => Task.Run(() => RegistrationLocked(() => Save(definition, token), token), token);

    private CleanupDefinition Save(CleanupDefinition definition, CancellationToken token, bool preserveRevision = false)
    {
        token.ThrowIfCancellationRequested();
        definition = definition.Validate();
        var existing = _store.LoadDefinitions().FirstOrDefault(item => item.Id == definition.Id);
        if (existing is not null && definition.Revision < existing.Revision)
            throw new InvalidOperationException("A newer cleanup is saved. Reload it before saving changes.");
        int revision = existing is null ? definition.Revision : preserveRevision ? existing.Revision : Math.Max(definition.Revision, checked(existing.Revision + 1));
        bool enable = definition.Enabled && definition.Schedule is not null;
        var inactive = definition with { Revision = revision, Enabled = false, RegisteredRevision = null,
            ScheduleError = null, ScheduleActivatedUtc = enable ? _clock.GetUtcNow() : null };
        _store.SaveDefinition(inactive);
        foreach (var review in _store.LoadRunHeaders().Where(run => !preserveRevision && run.DefinitionId == definition.Id && run.State == CleanupRunState.Review))
            _store.TryTransitionReview(review.Id, CleanupRunState.Superseded,
                "The saved cleanup changed. Open the current revision for a fresh review.");
        try
        {
            token.ThrowIfCancellationRequested();
            if (!enable)
            {
                if (existing?.Schedule is not null || existing?.RegisteredRevision is not null) _scheduler.Remove(inactive.Id);
                return inactive;
            }
            if (inactive.RootIdentity is null) throw new InvalidOperationException("Review a scanned location before enabling its schedule.");
            if (inactive.Schedule!.TimeZoneId != TimeZoneInfo.Local.Id)
                throw new InvalidOperationException("The PC time zone changed. Review this schedule before enabling it again.");
            var launcher = _launcher();
            _scheduler.Register(inactive, launcher);
            if (!_scheduler.Matches(inactive, launcher)) throw new InvalidOperationException("Windows could not verify the saved cleanup task.");
            token.ThrowIfCancellationRequested();
            // A different app instance may have paused or changed this rule
            // while COM registration was in progress. Never revive it.
            var current = _store.LoadDefinitions().FirstOrDefault(item => item.Id == inactive.Id);
            if (current is null || current.Revision != inactive.Revision || current.ScheduleActivatedUtc != inactive.ScheduleActivatedUtc)
                throw new InvalidOperationException("This cleanup changed while its schedule was being saved. Reload it.");
            var active = inactive with { Enabled = true, RegisteredRevision = inactive.Revision };
            _store.SaveDefinition(active);
            return active;
        }
        catch (Exception error) when (ScheduleError(error))
        {
            var current = _store.LoadDefinitions().FirstOrDefault(item => item.Id == inactive.Id);
            if (current is null || current.Revision != inactive.Revision) throw;
            var failed = inactive with { ScheduleError = enable ? "Saved, but scheduling is off. " + error.Message
                : "Future runs are off. Windows task cleanup needs attention: " + error.Message };
            _store.SaveDefinition(failed);
            return failed;
        }
    }

    internal async Task<CleanupDefinition> PauseAsync(Guid definitionId, bool paused, CancellationToken token = default)
    {
        var definition = _store.LoadDefinitions().FirstOrDefault(item => item.Id == definitionId)
            ?? throw new InvalidOperationException("This saved cleanup no longer exists.");
        if (definition.Schedule is null) throw new InvalidOperationException("This cleanup has no weekly schedule.");
        return await Task.Run(() => RegistrationLocked(() => Save(definition with { Enabled = !paused }, token, preserveRevision: true), token), token).ConfigureAwait(false);
    }

    internal Task DeleteAsync(Guid definitionId, CancellationToken token = default) => Task.Run(() => RegistrationLocked(() =>
    {
        token.ThrowIfCancellationRequested();
        var existing = _store.LoadDefinitions().FirstOrDefault(item => item.Id == definitionId);
        _store.DeleteDefinition(definitionId); // Old native invocations lose authority first.
        foreach (var review in _store.LoadRunHeaders().Where(run => run.DefinitionId == definitionId && run.State == CleanupRunState.Review))
            _store.TryTransitionReview(review.Id, CleanupRunState.Superseded,
                "The saved cleanup was deleted. Its captured review was not run.");
        try { if (existing?.Schedule is not null || existing?.RegisteredRevision is not null) _scheduler.Remove(definitionId); }
        catch (Exception error) when (ScheduleError(error))
        { throw new IOException("Cleanup deleted. Windows could not remove its task; it cannot run without its saved definition. " + error.Message, error); }
        return true;
    }, token), token);

    internal Task ReconcileAsync(CancellationToken token = default) => Task.Run(() => RegistrationLocked(() =>
    {
        foreach (var definition in _store.LoadDefinitions().Where(item => item.Enabled && item.Schedule is not null))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (definition.RegisteredRevision != definition.Revision || !_scheduler.Matches(definition, _launcher()))
                    throw new InvalidOperationException("The Windows task no longer matches this cleanup. Review and enable its schedule again.");
                RecordMissedOccurrences(definition, _clock.GetUtcNow(), token);
            }
            catch (Exception error) when (ScheduleError(error))
            {
                var current = _store.LoadDefinitions().FirstOrDefault(item => item.Id == definition.Id);
                if (current?.Revision == definition.Revision)
                    _store.SaveDefinition(current with { Enabled = false, RegisteredRevision = null, ScheduleError = error.Message });
            }
        }
        return true;
    }, token), token);

    private T RegistrationLocked<T>(Func<T> action, CancellationToken token)
    {
        using var mutex = new Mutex(false, _registrationMutex);
        bool owned = false;
        try
        {
            int result;
            try { result = WaitHandle.WaitAny([mutex, token.WaitHandle], TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { result = 0; }
            if (result == 1) throw new OperationCanceledException(token);
            if (result == WaitHandle.WaitTimeout) throw new InvalidOperationException("Another app instance is saving schedules. Try again shortly.");
            owned = true; token.ThrowIfCancellationRequested(); return action();
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }

    private void RecordMissedOccurrences(CleanupDefinition definition, DateTimeOffset now, CancellationToken token)
    {
        if (definition.ScheduleActivatedUtc is not { } activated || definition.Schedule is not { } schedule) return;
        var next = CleanupScheduleClock.Next(schedule, activated);
        // A damaged timestamp or a long-unused profile must not create an
        // unbounded startup loop. Older missed weeks are one explicit receipt.
        var oldestDetailed = now.AddYears(-2);
        if (next.ScheduledForUtc < oldestDetailed)
        {
            var firstDetailed = CleanupScheduleClock.Next(schedule, oldestDetailed);
            var older = Missed(definition, next, now) with { OccurrenceId = "older-missed",
                Message = "Older scheduled times before " + firstDetailed.Id + " were missed. No cleanup ran and no catch-up was started." };
            _store.TrySaveOccurrence(older);
            next = firstDetailed;
        }
        var known = _store.LoadRunHeaders().Where(run => run.DefinitionId == definition.Id && run.DefinitionRevision == definition.Revision)
            .Select(run => run.OccurrenceId).ToHashSet(StringComparer.Ordinal);
        while (next.ScheduledForUtc + LaunchTolerance < now)
        {
            token.ThrowIfCancellationRequested();
            if (!known.Contains(next.Id)) _store.TrySaveOccurrence(Missed(definition, next, now));
            next = CleanupScheduleClock.OnDate(schedule, DateOnly.FromDateTime(next.NominalLocalTime).AddDays(7));
        }
    }

    internal static CleanupRunRecord Missed(CleanupDefinition definition, CleanupOccurrence occurrence, DateTimeOffset now)
        => new() { Definition = definition, ScanRootPath = definition.DiscoveryRootPath, Source = "Scheduled", State = CleanupRunState.Missed,
            OccurrenceId = occurrence.Id, ScheduledForUtc = occurrence.ScheduledForUtc, CreatedUtc = now, UpdatedUtc = now,
            Message = "This scheduled time was missed. No cleanup ran and it will not catch up later." };

    internal static bool ScheduleError(Exception error) => error is IOException or UnauthorizedAccessException or InvalidOperationException
        or ArgumentException or COMException or Win32Exception or NotSupportedException or OperationCanceledException
        or TimeZoneNotFoundException or InvalidTimeZoneException;
}
