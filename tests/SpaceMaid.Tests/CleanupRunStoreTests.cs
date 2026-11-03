using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.IO;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CleanupRunStoreTests
{
    [Fact]
    public void LargeExactScopeSelectionRoundTripsWithoutBroadeningToItsParent()
    {
        using var fixture = new TestDirectory();
        var store = new CleanupRunStore(DiskAccess.Default, Path.Join(fixture.Path, "state"));
        string[] scopes = Enumerable.Range(0, 600).Select(index => Path.Join(fixture.Path, "project-" + index, "bin")).ToArray();
        var definition = Definition(fixture.Path) with { ScopePaths = scopes,
            ScopeIdentities = scopes.Select((path, index) => new FolderDeletionIdentity(path, new FileIdentity((ulong)index + 1), 1, 10, 10, true)).ToArray() };
        store.SaveDefinition(definition);
        var restored = Assert.Single(store.LoadDefinitions());
        Assert.Equal(scopes, restored.ScopePaths); Assert.Equal(definition.ScopeIdentities, restored.ScopeIdentities);
        var selected = restored.ScopePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(CleanupDefinition.InScopes(Path.Join(scopes[^1], "nested"), selected));
        Assert.False(CleanupDefinition.InScopes(Path.Join(fixture.Path, "project-600", "bin"), selected));
        Assert.False(CleanupDefinition.InScopes(fixture.Path, selected));
        var source = Record(fixture.Path);
        var record = source with { Definition = restored, Targets = scopes.Select((path, index) =>
            new CleanupRunTarget(source.Targets[0].Location with { Path = path, DirectoryIndex = index }, restored.ScopeIdentities[index], "Ready", "Captured")).ToArray() };
        store.SaveRun(record);
        Assert.Equal(scopes, store.LoadRun(record.Id)!.Targets.Select(target => target.Location.Path));
    }

    [Fact]
    public void SavedDefinitionsPreserveScopePoliciesAndRequireReviewForCacheAndWorktreeAutomation()
    {
        using var fixture = new TestDirectory();
        var store = new CleanupRunStore(DiskAccess.Default, Path.Join(fixture.Path, "state"));
        var definition = Definition(fixture.Path) with { InactiveDays = 45, BuildAction = BuildCleanupAction.PermanentlyDelete,
            ExcludedPaths = [Path.Join(fixture.Path, "keep")], Schedule = new(DayOfWeek.Monday, new(9, 30), TimeZoneInfo.Local.Id, true),
            Limits = new() { MaxBytes = 123456, MaxTargets = 12, MaxMinutes = 5 } };
        store.SaveDefinition(definition);
        var restored = Assert.Single(store.LoadDefinitions());
        Assert.Equal(definition.Id, restored.Id); Assert.Equal(definition.InactiveDays, restored.InactiveDays);
        Assert.Equal(definition.ScopePaths, restored.ScopePaths); Assert.Equal(definition.ExcludedPaths, restored.ExcludedPaths);
        Assert.Equal(definition.Schedule, restored.Schedule); Assert.Equal(definition.Limits, restored.Limits);
        Assert.Equal(BuildCleanupAction.PermanentlyDelete, restored.BuildAction);
        Assert.Throws<ArgumentException>(() => (definition with { Cache = true }).Validate());
        Assert.Throws<ArgumentException>(() => (definition with { Worktrees = true }).Validate());
        store.DeleteDefinition(definition.Id); Assert.Empty(store.LoadDefinitions());
    }

    [Fact]
    public void NestedExclusionsProtectTheirContainingFolderAndRejectOutOfScopeDefinitions()
    {
        using var fixture = new TestDirectory();
        string container = Path.Join(fixture.Path, "generated"), keep = Path.Join(container, "keep");
        var definition = Definition(fixture.Path) with { ExcludedPaths = [keep] };
        Assert.True(definition.Excludes(container)); Assert.True(definition.Excludes(keep));
        Assert.True(definition.Excludes(Path.Join(keep, "child"))); Assert.False(definition.Excludes(Path.Join(fixture.Path, "other")));
        Assert.Throws<ArgumentException>(() => (definition with { ScopePaths = [Path.GetPathRoot(fixture.Path)!] }).Validate());
    }

    [Fact]
    public void AttemptJournalRoundTripsTheOriginalManifestAndLatestPerTargetOutcome()
    {
        using var fixture = new TestDirectory(); var store = new CleanupRunStore(DiskAccess.Default, Path.Join(fixture.Path, "state"));
        var run = Record(fixture.Path) with { State = CleanupRunState.Running };
        store.SaveRun(run); var target = run.Targets[0].Location;
        store.AppendReceipt(run.Id, new(target.Path, target.ProviderId, target.RuleId, CleanupOutcome.Uncertain, "Started", true, DateTimeOffset.UtcNow));
        var unknown = store.LoadRun(run.Id)!;
        Assert.True(unknown.HasUncertainOutcome); Assert.Equal(run.Targets[0].Identity, unknown.Targets[0].Identity);
        store.AppendReceipt(run.Id, new(target.Path, target.ProviderId, target.RuleId, CleanupOutcome.PermanentlyDeleted, "Confirmed", true, DateTimeOffset.UtcNow));
        var completed = store.LoadRun(run.Id)!;
        Assert.False(completed.HasUncertainOutcome); Assert.Equal(CleanupOutcome.PermanentlyDeleted, Assert.Single(completed.Receipts).Outcome);
        store.SaveRun(completed with { State = CleanupRunState.Completed });
        Assert.Equal(CleanupRunState.Completed, store.LoadRun(run.Id)!.State);
        Assert.Equal(CleanupOutcome.PermanentlyDeleted, Assert.Single(store.LoadRun(run.Id)!.Receipts).Outcome);
    }

    [Fact]
    public void ManifestAndReceiptRetainTheCompleteGeneratedFileProducerContract()
    {
        using var fixture = new TestDirectory(); var store = new CleanupRunStore(DiskAccess.Default, Path.Join(fixture.Path, "state"));
        var producer = new AutomaticCleanupRule("python-bytecode", "Python bytecode", "Recreated by Python from the retained source.",
            [".pyc", ".pyo"], 14, requiresPythonSource: true, resourceKind: GeneratedResourceKind.PythonBytecode);
        var original = Record(fixture.Path);
        var target = original.Targets[0] with { Location = original.Targets[0].Location with { AutomaticCleanup = producer } };
        var run = original with { Targets = [target] };
        store.SaveRun(run);
        var restored = store.LoadRun(run.Id)!;
        var contract = Assert.IsType<AutomaticCleanupRule>(Assert.Single(restored.Targets).Location.AutomaticCleanup);
        Assert.Equal(producer.Id, contract.Id); Assert.Equal(producer.Name, contract.Name); Assert.Equal(producer.Regeneration, contract.Regeneration);
        Assert.Equal(producer.FileSuffixes, contract.FileSuffixes); Assert.Equal(producer.MinimumAgeDays, contract.MinimumAgeDays);
        Assert.True(contract.RequiresPythonSource); Assert.Equal(GeneratedResourceKind.PythonBytecode, contract.ResourceKind);
        Assert.Equal(producer.ConsentKey(target.Location.ProviderId), contract.ConsentKey(target.Location.ProviderId));
        var queued = restored with { State = CleanupRunState.Queued, ApprovalClaimId = Guid.NewGuid() };
        Assert.True(store.TryQueueRun(queued)); store.SaveRun(queued with { State = CleanupRunState.Running });
        store.AppendReceipt(run.Id, new(target.Location.Path, target.Location.ProviderId, target.Location.RuleId,
            CleanupOutcome.Recycled, "Confirmed recycled", true, DateTimeOffset.UtcNow) { RecycledPath = "G:\\$Recycle.Bin\\S-1-fixture\\$Rfixture" });
        var received = store.LoadRun(run.Id)!;
        Assert.Equal(producer.ConsentKey(target.Location.ProviderId), received.Targets[0].Location.AutomaticCleanup!.ConsentKey(target.Location.ProviderId));
        Assert.Equal("G:\\$Recycle.Bin\\S-1-fixture\\$Rfixture", Assert.Single(received.Receipts).RecycledPath);
        store.SaveRun(received with { State = CleanupRunState.Completed });
        Assert.Equal(CleanupRunState.Completed, store.LoadRun(run.Id)!.State);
    }

    [Fact]
    public void InterruptedTerminalAppendCannotEraseTheDurableUnknownStartingRecord()
    {
        using var fixture = new TestDirectory(); string state = Path.Join(fixture.Path, "state");
        var store = new CleanupRunStore(DiskAccess.Default, state); var run = Record(fixture.Path) with { State = CleanupRunState.Running };
        store.SaveRun(run); var target = run.Targets[0].Location;
        store.AppendReceipt(run.Id, new(target.Path, target.ProviderId, target.RuleId, CleanupOutcome.Uncertain, "Started", true, DateTimeOffset.UtcNow));
        File.AppendAllText(Path.Join(state, "Runs", run.Id.ToString("N") + ".journal"), "{\"Path\":");
        Assert.Equal(CleanupOutcome.Uncertain, Assert.Single(store.LoadRun(run.Id)!.Receipts).Outcome);
    }

    [Fact]
    public void PendingReviewsAreNotEvictedByHistoryAndStopDoesNotDismissThem()
    {
        using var fixture = new TestDirectory(); var store = new CleanupRunStore(DiskAccess.Default, Path.Join(fixture.Path, "state"));
        var pending = Record(fixture.Path); store.SaveRun(pending);
        for (int index = 0; index < 10; index++) store.SaveRun(Record(fixture.Path) with { State = CleanupRunState.Completed });
        Assert.Equal(CleanupRunState.Review, store.LoadRun(pending.Id)!.State);
        Assert.False(store.RequestStop(pending.Id));
        var active = Record(fixture.Path) with { State = CleanupRunState.Running }; store.SaveRun(active);
        Assert.True(store.RequestStop(active.Id)); Assert.True(store.IsStopRequested(active.Id));
        Assert.False(store.IsStopRequested(pending.Id));
    }

    [Fact]
    public void OccurrenceReservationDeduplicatesWithoutReplacingTheOriginalReceipt()
    {
        using var fixture = new TestDirectory(); var store = new CleanupRunStore(DiskAccess.Default, Path.Join(fixture.Path, "state"));
        var occurrence = Record(fixture.Path) with { Source = "Scheduled", OccurrenceId = "2030-01-07", State = CleanupRunState.Queued };
        Assert.True(store.TrySaveOccurrence(occurrence));
        Assert.False(store.TrySaveOccurrence(occurrence with { Id = Guid.NewGuid() }));
        Assert.Equal(occurrence.Id, Assert.Single(store.LoadRuns()).Id);
    }

    [Fact]
    public void RecoveryKeepsLivePreparationAndMarksDeadAttemptedWorkUncertain()
    {
        using var fixture = new TestDirectory(); var store = new CleanupRunStore(DiskAccess.Default, Path.Join(fixture.Path, "state"));
        var live = Record(fixture.Path) with { State = CleanupRunState.Queued }; store.SaveRun(live);
        var dead = Record(fixture.Path) with { State = CleanupRunState.Running, OwnerProcessId = int.MaxValue, OwnerStartedUtc = DateTimeOffset.UnixEpoch };
        store.SaveRun(dead); var target = dead.Targets[0].Location;
        store.AppendReceipt(dead.Id, new(target.Path, target.ProviderId, target.RuleId, CleanupOutcome.Uncertain, "Started", true, DateTimeOffset.UtcNow));
        store.RecoverInterruptedRuns();
        Assert.Equal(CleanupRunState.Queued, store.LoadRun(live.Id)!.State);
        Assert.Equal(CleanupRunState.NeedsAttention, store.LoadRun(dead.Id)!.State);
        Assert.True(store.LoadRun(dead.Id)!.HasUncertainOutcome);
    }

    [Fact]
    public async Task OnlyOneApprovalCanClaimAReviewAndStaleDismissCannotEraseItsJournal()
    {
        using var fixture = new TestDirectory(); var store = new CleanupRunStore(DiskAccess.Default, Path.Join(fixture.Path, "state"));
        var review = Record(fixture.Path); store.SaveRun(review);
        var first = review with { State = CleanupRunState.Queued, ApprovalClaimId = Guid.NewGuid() };
        var second = review with { State = CleanupRunState.Queued, ApprovalClaimId = Guid.NewGuid() };
        var claims = await Task.WhenAll(Task.Run(() => store.TryQueueRun(first)), Task.Run(() => store.TryQueueRun(second)));
        Assert.Single(claims, value => value);
        var queued = store.LoadRun(review.Id)!; store.SaveRun(queued with { State = CleanupRunState.Running });
        var target = review.Targets[0].Location;
        store.AppendReceipt(review.Id, new(target.Path, target.ProviderId, target.RuleId, CleanupOutcome.Uncertain, "Started", true, DateTimeOffset.UtcNow));
        Assert.False(store.TryTransitionReview(review.Id, CleanupRunState.Dismissed));
        Assert.Throws<InvalidOperationException>(() => store.SaveRun(review with { State = CleanupRunState.Dismissed }));
        var active = store.LoadRun(review.Id)!;
        Assert.Equal(CleanupRunState.Running, active.State); Assert.Equal(queued.ApprovalClaimId, active.ApprovalClaimId);
        Assert.Equal(CleanupOutcome.Uncertain, Assert.Single(active.Receipts).Outcome);
    }

    [Fact]
    public void HistoryHeadersStayConsistentAndRepairAnInterruptedIndexCommit()
    {
        using var fixture = new TestDirectory(); string state = Path.Join(fixture.Path, "state");
        var store = new CleanupRunStore(DiskAccess.Default, state); var review = Record(fixture.Path); store.SaveRun(review);
        var header = Assert.Single(store.LoadRunHeaders());
        Assert.Equal(review.Id, header.Id); Assert.Equal(review.Definition.Name, header.Name); Assert.Equal(1, header.ReadyCount);
        var queued = review with { State = CleanupRunState.Queued, ApprovalClaimId = Guid.NewGuid() };
        Assert.True(store.TryQueueRun(queued)); store.SaveRun(queued with { State = CleanupRunState.Running });
        Assert.True(Assert.Single(store.LoadRunHeaders()).HasUncertainOutcome);
        var target = review.Targets[0].Location;
        store.AppendReceipt(review.Id, new(target.Path, target.ProviderId, target.RuleId, CleanupOutcome.Uncertain, "Started", true, DateTimeOffset.UtcNow));
        File.WriteAllText(Path.Join(state, "run-index.dirty"), "interrupted commit");
        File.WriteAllText(Path.Join(state, "run-index.json"), "older interrupted index");
        var rebuilt = Assert.Single(store.LoadRunHeaders());
        Assert.Equal(CleanupRunState.Running, rebuilt.State); Assert.Equal(1, rebuilt.NeedsCheckingCount);
        var complete = store.LoadRun(review.Id)! with { State = CleanupRunState.NeedsAttention };
        store.SaveRun(complete); Assert.Equal(CleanupRunState.NeedsAttention, Assert.Single(store.LoadRunHeaders()).State);
    }

    internal static CleanupDefinition Definition(string root) => new() { DiscoveryRootPath = root, ScopePaths = [root] };
    internal static CleanupRunRecord Record(string root)
    {
        string path = Path.Join(root, "project", "bin");
        var target = new Opportunity(1, "Build outputs", "Build outputs", path, 1024, 4096, "Generated output", "Rebuild", "Rebuild", false)
        { ProviderId = "fixture", RuleId = "build", IsBuildOutput = true, IsRegeneratable = true };
        return new() { Definition = Definition(root), ScanRootPath = root, Targets = [new(target,
            new(path, new FileIdentity(42), 123, 1024, 4096, true), "Ready", "Ready")] };
    }
}
