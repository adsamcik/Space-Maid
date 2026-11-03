using System.IO;
using System.Text.Json.Nodes;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.IO;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

[Trait("Stage", "System")]
public sealed class CleanupRunCoordinatorTests
{
    [Theory]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("pause")]
    public async Task ScheduledPreparationPublishesOnlyTheCurrentRevisionAndPreservesAdmittedAskFirstOnPause(string change)
    {
        using var fixture = new TestDirectory(); var project = CleanupRulePlanTests.Project(fixture.Path, "project", 60);
        var operations = new FileOperations(); var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var scan = await operations.ScanAsync(fixture.Path); var findings = DeveloperOpportunityFixture.Find(scan);
        var initial = await new CleanupRunCoordinator(operations, store, DeveloperOpportunityFixture.Find)
            .PrepareAsync(CleanupRunStoreTests.Definition(fixture.Path), scan, findings, default);
        var definition = initial.Definition with { Enabled = true, RegisteredRevision = 1,
            Schedule = new(DayOfWeek.Monday, new(9, 0), TimeZoneInfo.Local.Id, false) };
        store.SaveDefinition(definition);
        var reserved = new CleanupRunRecord { Definition = definition, ScanRootPath = fixture.Path, Source = "Scheduled",
            State = CleanupRunState.Queued, OccurrenceId = "owned-occurrence" };
        Assert.True(store.TrySaveOccurrence(reserved));
        bool changed = false;
        var coordinator = new CleanupRunCoordinator(operations, store, (current, token) =>
        {
            if (!changed)
            {
                changed = true;
                if (change == "delete") store.DeleteDefinition(definition.Id);
                else store.SaveDefinition(definition with { Revision = change == "edit" ? 2 : 1, Enabled = false });
            }
            return DeveloperOpportunityFixture.Find(current, token);
        });
        var preparing = coordinator.PrepareScheduledAsync(definition, scan, findings, reserved, default);
        if (change == "pause")
        {
            var ready = await preparing;
            Assert.Equal(CleanupRunState.Review, store.LoadRun(ready.Record.Id)!.State);
            Assert.Single(ready.Plan.Targets);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => preparing);
            var stopped = store.LoadRun(reserved.Id)!;
            Assert.Equal(CleanupRunState.Stopped, stopped.State); Assert.Empty(stopped.Targets);
            Assert.Contains("during preparation", stopped.Message);
            Assert.DoesNotContain(store.LoadRunHeaders(), run => run.State == CleanupRunState.Review);
        }
        Assert.True(changed); Assert.Equal("generated output", File.ReadAllText(project.File));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task JournalFailureEndsTheLiveOwnersRunAndRetainsDurableCompletedOutcomes(bool afterFirstTarget, bool deferFinalization)
    {
        using var fixture = new TestDirectory(); _ = CleanupRulePlanTests.Project(fixture.Path, "first", 60);
        _ = CleanupRulePlanTests.Project(fixture.Path, "second", 60);
        var operations = new FileOperations(); string data = Path.Join(fixture.Path, "state");
        var store = new CleanupRunStore(operations.Files, data);
        var coordinator = new CleanupRunCoordinator(operations, store, DeveloperOpportunityFixture.Find);
        var scan = await operations.ScanAsync(fixture.Path);
        var prepared = await coordinator.PrepareAsync(CleanupRunStoreTests.Definition(fixture.Path) with { BuildAction = BuildCleanupAction.PermanentlyDelete },
            scan, DeveloperOpportunityFixture.Find(scan), default);
        store.SaveRun(prepared.Record);
        string journal = Path.Join(data, "Runs", prepared.Record.Id.ToString("N") + ".journal");
        FileStream? readLease = null, manifestLease = null;
        if (!afterFirstTarget) Directory.CreateDirectory(journal);
        try
        {
            var progress = new CallbackProgress(update =>
            {
                if (afterFirstTarget && readLease is null && update.Result?.Outcome == CleanupOutcome.PermanentlyDeleted)
                {
                    readLease = File.Open(journal, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (deferFinalization) manifestLease = File.Open(Path.ChangeExtension(journal, ".json"), FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            });
            var error = await Record.ExceptionAsync(() => coordinator.RunAsync(prepared, 0, progress, default));
            Assert.NotNull(error); Assert.True(CleanupBatch.IsExpected(error));
            if (deferFinalization)
            {
                Assert.Equal(CleanupRunState.Running, store.LoadRun(prepared.Record.Id)!.State);
                manifestLease!.Dispose(); manifestLease = null; readLease!.Dispose(); readLease = null;
                store.RecoverInterruptedRuns();
            }
            var stopped = store.LoadRun(prepared.Record.Id)!;
            Assert.Equal(CleanupRunState.Stopped, stopped.State);
            Assert.True(CleanupRunOwnership.IsAlive(stopped));
            Assert.Equal(afterFirstTarget ? 1 : 0, stopped.Receipts.Count(item => item.Outcome == CleanupOutcome.PermanentlyDeleted));
            Assert.Equal(afterFirstTarget ? 1 : 2, prepared.Record.Targets.Count(item => Directory.Exists(item.Location.Path)));
        }
        finally { manifestLease?.Dispose(); readLease?.Dispose(); }
    }

    [Fact]
    public async Task AReplacementNeverInheritsTheOriginalCapturedApproval()
    {
        using var fixture = new TestDirectory(); var project = CleanupRulePlanTests.Project(fixture.Path, "project", 60);
        var operations = new FileOperations(); var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var coordinator = new CleanupRunCoordinator(operations, store, DeveloperOpportunityFixture.Find);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var prepared = await coordinator.PrepareAsync(CleanupRunStoreTests.Definition(fixture.Path) with { BuildAction = BuildCleanupAction.PermanentlyDelete },
            scan, DeveloperOpportunityFixture.Find(scan), default);
        store.SaveRun(prepared.Record);
        Directory.Move(project.Output, project.Output + "-original"); Directory.CreateDirectory(project.Output);
        File.WriteAllText(project.File, "replacement must survive");
        var restored = await coordinator.RestorePreparedAsync(store.LoadRun(prepared.Record.Id)!, default);
        Assert.Empty(restored.Plan.Targets); Assert.NotEmpty(restored.Plan.BlockedLocations);
        Assert.Equal("replacement must survive", File.ReadAllText(project.File));
        Assert.Equal(prepared.Record.Targets.Single(item => item.Decision == "Ready").Identity,
            store.LoadRun(prepared.Record.Id)!.Targets.Single(item => item.Decision == "Ready").Identity);
    }

    [Fact]
    public async Task ANewPreviewCannotClearAnEarlierUnknownOutcome()
    {
        using var fixture = new TestDirectory(); var project = CleanupRulePlanTests.Project(fixture.Path, "project", 60);
        var operations = new FileOperations(); var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var coordinator = new CleanupRunCoordinator(operations, store, DeveloperOpportunityFixture.Find);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var definition = CleanupRunStoreTests.Definition(fixture.Path);
        var prepared = await coordinator.PrepareAsync(definition, scan, DeveloperOpportunityFixture.Find(scan), default);
        var target = Assert.Single(prepared.Record.Targets, item => item.Decision == "Ready").Location;
        store.SaveRun(prepared.Record with { State = CleanupRunState.NeedsAttention, Receipts = [new(target.Path, target.ProviderId, target.RuleId,
            CleanupOutcome.Uncertain, "Worker response lost", true, DateTimeOffset.UtcNow)] });
        var next = await coordinator.PrepareAsync(definition with { Id = Guid.NewGuid() }, scan, DeveloperOpportunityFixture.Find(scan), default);
        Assert.Empty(next.Plan.Targets); Assert.Contains(next.Plan.BlockedLocations, item => item.Reason.Contains("earlier cleanup outcome"));
        Assert.Equal("generated output", File.ReadAllText(project.File));
    }

    [Fact]
    public async Task ALiveStartingJournalProtectsItsFolderWhenTheRunningHeaderHasNoUncertaintyFlag()
    {
        using var fixture = new TestDirectory(); var project = CleanupRulePlanTests.Project(fixture.Path, "project", 60);
        var operations = new FileOperations(); string state = Path.Join(fixture.Path, "state");
        var store = new CleanupRunStore(operations.Files, state);
        var coordinator = new CleanupRunCoordinator(operations, store, DeveloperOpportunityFixture.Find);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var definition = CleanupRunStoreTests.Definition(fixture.Path);
        var prepared = await coordinator.PrepareAsync(definition, scan, DeveloperOpportunityFixture.Find(scan), default);
        var target = Assert.Single(prepared.Record.Targets, item => item.Decision == "Ready").Location;
        store.SaveRun(prepared.Record with { State = CleanupRunState.Running });
        string indexPath = Path.Join(state, "run-index.json");
        var index = JsonNode.Parse(File.ReadAllText(indexPath))!;
        index["Runs"]![0]!["HasUncertainOutcome"] = false; // An older active header, before the starting append.
        File.WriteAllText(indexPath, index.ToJsonString());
        store.AppendReceipt(prepared.Record.Id, new(target.Path, target.ProviderId, target.RuleId,
            CleanupOutcome.Uncertain, "Mutation started; confirmation was lost", true, DateTimeOffset.UtcNow));
        Assert.False(Assert.Single(store.LoadRunHeaders()).HasUncertainOutcome);
        Assert.True(CleanupRunOwnership.IsAlive(store.LoadRun(prepared.Record.Id)!));
        var next = await coordinator.PrepareAsync(definition with { Id = Guid.NewGuid() }, scan, DeveloperOpportunityFixture.Find(scan), default);
        Assert.Empty(next.Plan.Targets);
        Assert.Contains(next.Plan.BlockedLocations, item => item.Opportunity.Path == target.Path && item.Reason.Contains("earlier cleanup outcome"));
        Assert.Equal("generated output", File.ReadAllText(project.File));
    }

    [Fact]
    public async Task PermanentExecutionRecordsConfirmedResultsAndContinuesPastALockedBuild()
    {
        using var fixture = new TestDirectory(); var good = CleanupRulePlanTests.Project(fixture.Path, "good", 60);
        var locked = CleanupRulePlanTests.Project(fixture.Path, "locked", 60);
        var operations = new FileOperations(); var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var coordinator = new CleanupRunCoordinator(operations, store, DeveloperOpportunityFixture.Find);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var prepared = await coordinator.PrepareAsync(CleanupRunStoreTests.Definition(fixture.Path) with { BuildAction = BuildCleanupAction.PermanentlyDelete },
            scan, DeveloperOpportunityFixture.Find(scan), default);
        CleanupBatchResult result;
        using (File.Open(locked.File, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            result = await coordinator.RunAsync(prepared, 0, new Progress(), default);
        Assert.Equal(1, result.DeletedCount); Assert.False(Directory.Exists(good.Output));
        Assert.Equal("generated output", File.ReadAllText(locked.File));
        var receipt = store.LoadRun(prepared.Record.Id)!;
        Assert.Equal(CleanupRunState.Completed, receipt.State);
        Assert.Contains(receipt.Receipts, item => item.Path == good.Output && item.Outcome == CleanupOutcome.PermanentlyDeleted && item.Attempted);
        Assert.Contains(receipt.Receipts, item => item.Path == locked.Output && item.Outcome == CleanupOutcome.Skipped && !item.Attempted);
    }

    [Fact]
    public async Task AsyncMutationLeaseSerializesAndCancellationDoesNotReleaseAnotherOwnersLease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CleanupMutationLease.RunAsync(async () => { entered.SetResult(); await release.Task; return 1; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource(); bool secondEntered = false;
        var second = CleanupMutationLease.RunAsync(() => { secondEntered = true; return Task.FromResult(2); }, cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(secondEntered); release.SetResult(); Assert.Equal(1, await first);
        Assert.Equal(3, await CleanupMutationLease.RunAsync(() => Task.FromResult(3)));
        var releaseInherited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? inherited = null;
        await CleanupMutationLease.RunAsync(() =>
        {
            inherited = Task.Run(async () => { await releaseInherited.Task; return CleanupMutationLease.IsHeldByCurrentContext; });
            return Task.CompletedTask;
        });
        releaseInherited.SetResult(); Assert.False(await inherited!);
    }

    [Fact]
    public async Task AWriteAheadFailureStopsBeforeAnyMutationAndKeepsRemainingBuilds()
    {
        using var fixture = new TestDirectory(); var first = CleanupRulePlanTests.Project(fixture.Path, "first", 60);
        var second = CleanupRulePlanTests.Project(fixture.Path, "second", 60);
        var operations = new FileOperations(); var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var coordinator = new CleanupRunCoordinator(operations, store, DeveloperOpportunityFixture.Find);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var prepared = await coordinator.PrepareAsync(CleanupRunStoreTests.Definition(fixture.Path) with { BuildAction = BuildCleanupAction.PermanentlyDelete },
            scan, DeveloperOpportunityFixture.Find(scan), default);
        var journal = new CleanupExecutionJournal(_ => throw new IOException("Owned journal failure"), _ => { }, () => false);
        var result = await CleanupBatch.ExecuteJournaledAsync(prepared.Plan, 0, new Progress(), default, (_, _) => { }, null,
            DeveloperOpportunityFixture.Find, operations, journal);
        Assert.Equal(0, result.DeletedCount); Assert.All(result.Locations, item => Assert.False(item.Attempted));
        Assert.Contains(result.Locations, item => item.Outcome == CleanupOutcome.NotProcessed);
        Assert.Equal("generated output", File.ReadAllText(first.File)); Assert.Equal("generated output", File.ReadAllText(second.File));
    }

    private sealed class Progress : IProgress<CleanupProgress> { public void Report(CleanupProgress value) { } }
    private sealed class CallbackProgress(Action<CleanupProgress> callback) : IProgress<CleanupProgress> { public void Report(CleanupProgress value) => callback(value); }
}
