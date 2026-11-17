using System.IO;
using System.Text.Json;
using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

[Trait("Stage", "System")]
public sealed class WorktreeHistoryTests
{
    [Theory]
    [InlineData("history")]
    [InlineData("unmanage")]
    [InlineData("automatic")]
    public async Task MetadataEditsAcrossInstancesWaitForAndPreserveNewRecoveryIntent(string edit)
    {
        using var fixture = new TestDirectory(); string settings = Path.Join(fixture.Path, "retirement.json");
        var policy = Policy(fixture.Path, "checkout");
        var terminal = Record(fixture.Path, policy, WorktreeRetirementState.Restored);
        var prepared = Record(fixture.Path, policy, WorktreeRetirementState.Prepared);
        File.WriteAllText(settings, JsonSerializer.Serialize(new WorktreeRetirementSettings([policy], [terminal])));
        var firstService = new WorktreeRetirement(settings); var secondService = new WorktreeRetirement(settings);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new CancellationTokenSource();
        var first = CleanupMutationLease.RunAsync(async () =>
        {
            // Exercise reentrancy through the first instance while holding the
            // same lease as a checkout operation at its durable intent boundary.
            await firstService.SetAutomaticAsync(policy.Path, false, owner.Token);
            entered.SetResult(); await commit.Task;
            File.WriteAllText(settings, JsonSerializer.Serialize(firstService.Load() with { Records = [terminal, prepared] }));
        }, owner.Token);
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            second = Edit(secondService, edit, policy, terminal.Id, default);
            Assert.False(second.IsCompleted); // Even read-check-save metadata work must wait for the owning mutation.
        }
        finally
        {
            commit.TrySetResult(); if (!entered.Task.IsCompleted) owner.Cancel();
            try { await first.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
        }

        if (edit == "unmanage") await Assert.ThrowsAsync<InvalidOperationException>(() => second!);
        else await second!.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = secondService.Load();
        Assert.Contains(saved.Records, record => record.Id == prepared.Id && record.State == WorktreeRetirementState.Prepared);
        Assert.Equal(edit == "history" ? 1 : 2, saved.Records.Count);
        Assert.Equal(edit == "automatic", Assert.Single(saved.Policies).Automatic);
    }

    [Theory]
    [InlineData("history")]
    [InlineData("unmanage")]
    [InlineData("automatic")]
    [InlineData("enroll")]
    public async Task CancellationWhileWaitingForAnotherInstanceDoesNotReadOrSaveSettings(string edit)
    {
        using var fixture = new TestDirectory(); string settings = Path.Join(fixture.Path, "retirement.json");
        var policy = Policy(fixture.Path, "checkout"); var terminal = Record(fixture.Path, policy, WorktreeRetirementState.Restored);
        File.WriteAllText(settings, JsonSerializer.Serialize(new WorktreeRetirementSettings([policy], [terminal])));
        var firstService = new WorktreeRetirement(settings); var secondService = new WorktreeRetirement(settings);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new CancellationTokenSource();
        var first = CleanupMutationLease.RunAsync(async () =>
        {
            await firstService.SetAutomaticAsync(policy.Path, false, owner.Token);
            entered.SetResult(); await release.Task;
        }, owner.Token);
        using var cancel = new CancellationTokenSource();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            string before = File.ReadAllText(settings);
            var waiting = Edit(secondService, edit, policy, terminal.Id, cancel.Token);
            Assert.False(waiting.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(before, File.ReadAllText(settings));
            Assert.False(first.IsCompleted); // A cancelled waiter cannot release the current owner's lease.
        }
        finally
        {
            release.TrySetResult(); if (!entered.Task.IsCompleted) owner.Cancel();
            try { await first.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData(WorktreeRetirementState.Completed)]
    [InlineData(WorktreeRetirementState.Restored)]
    public async Task RemovingTerminalReceiptReclaimsCapacityWithoutChangingPoliciesFilesOrRetainedReferences(WorktreeRetirementState terminal)
    {
        using var fixture = new TestDirectory();
        string settings = Path.Join(fixture.Path, "retirement.json");
        var policy = Policy(fixture.Path, "checkout");
        var records = Enumerable.Range(0, 128).Select(_ => Record(fixture.Path, policy, terminal)).ToArray();
        var untouched = new[] { Path.Join(policy.Path, "source.txt"), Path.Join(records[0].QuarantinePath, "remaining.txt"),
            Path.Join(records[0].CommonGitDirectory, records[0].RecoveryReference.Replace('/', Path.DirectorySeparatorChar)) };
        foreach (string path in untouched) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "preserved"); }
        File.WriteAllText(settings, JsonSerializer.Serialize(new WorktreeRetirementSettings([policy], records)));
        var service = new WorktreeRetirement(settings);

        await service.RemoveHistoryAsync(records[0].Id);

        var saved = service.Load();
        Assert.Equal(JsonSerializer.Serialize(records.Skip(1)), JsonSerializer.Serialize(saved.Records));
        Assert.Equal(policy, Assert.Single(saved.Policies));
        foreach (string path in untouched) Assert.Equal("preserved", File.ReadAllText(path));
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(new WorktreeRetirement(settings).Load()));
    }

    [Theory]
    [InlineData(WorktreeRetirementState.Prepared)]
    [InlineData(WorktreeRetirementState.Quarantined)]
    [InlineData(WorktreeRetirementState.Finalizing)]
    [InlineData(WorktreeRetirementState.NeedsReview)]
    [InlineData(WorktreeRetirementState.RestoreOnly)]
    public async Task UnfinishedRecoveryCannotBeRemovedOrUnmanaged(WorktreeRetirementState pending)
    {
        using var fixture = new TestDirectory(); string settings = Path.Join(fixture.Path, "retirement.json");
        var policy = Policy(fixture.Path, "checkout"); var record = Record(fixture.Path, policy, pending);
        string original = JsonSerializer.Serialize(new WorktreeRetirementSettings([policy], [record]));
        File.WriteAllText(settings, original); var service = new WorktreeRetirement(settings);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveHistoryAsync(record.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UnmanageAsync(policy.Path));

        Assert.Equal(original, File.ReadAllText(settings));
    }

    [Fact]
    public async Task StoppingManagementReclaimsPolicyCapacityAndPreservesAllRecoveryHistory()
    {
        using var fixture = new TestDirectory(); string settings = Path.Join(fixture.Path, "retirement.json");
        var policies = Enumerable.Range(0, 32).Select(index => Policy(fixture.Path, "checkout-" + index)).ToArray();
        var terminal = Record(fixture.Path, policies[0], WorktreeRetirementState.Restored);
        var pendingElsewhere = Record(fixture.Path, policies[1], WorktreeRetirementState.RestoreOnly);
        Directory.CreateDirectory(policies[0].Path); string source = Path.Join(policies[0].Path, "source.txt"); File.WriteAllText(source, "keep checkout");
        File.WriteAllText(settings, JsonSerializer.Serialize(new WorktreeRetirementSettings(policies, [terminal, pendingElsewhere])));
        var service = new WorktreeRetirement(settings);

        await service.UnmanageAsync(policies[0].Path + Path.DirectorySeparatorChar);

        var saved = service.Load();
        Assert.Equal(policies.Skip(1), saved.Policies);
        Assert.Equal(JsonSerializer.Serialize(new[] { terminal, pendingElsewhere }), JsonSerializer.Serialize(saved.Records));
        Assert.Equal("keep checkout", File.ReadAllText(source));
    }

    [Fact]
    public async Task CancelledMetadataRetirementLeavesTheOriginalSettingsUntouched()
    {
        using var fixture = new TestDirectory(); string settings = Path.Join(fixture.Path, "retirement.json");
        var policy = Policy(fixture.Path, "checkout"); var terminal = Record(fixture.Path, policy, WorktreeRetirementState.Completed);
        string original = JsonSerializer.Serialize(new WorktreeRetirementSettings([policy], [terminal])); File.WriteAllText(settings, original);
        var service = new WorktreeRetirement(settings); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RemoveHistoryAsync(terminal.Id, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UnmanageAsync(policy.Path, cancelled.Token));

        Assert.Equal(original, File.ReadAllText(settings));
    }

    private static ManagedWorktreePolicy Policy(string root, string name)
        => new(Path.Join(root, name), "refs/heads/main", "refs/heads/topic", new string('A', 48), Automatic: true);
    private static WorktreeRetirementRecord Record(string root, ManagedWorktreePolicy policy, WorktreeRetirementState state)
        => new(Guid.NewGuid(), policy, Path.Join(root, "quarantine-" + Guid.NewGuid().ToString("N")), new string('B', 40),
            Path.Join(root, "common.git"), DateTime.UtcNow, state, []);

    private static Task Edit(WorktreeRetirement service, string edit, ManagedWorktreePolicy policy, Guid terminal, CancellationToken token)
        => edit switch
        {
            "history" => service.RemoveHistoryAsync(terminal, token),
            "unmanage" => service.UnmanageAsync(policy.Path, token),
            "automatic" => service.SetAutomaticAsync(policy.Path, true, token),
            "enroll" => service.EnrollAsync(policy.Path, policy.IntegrationReference, policy.InactiveDays, policy.GraceDays, true, token),
            _ => throw new ArgumentException("Unknown fixture edit.")
        };
}
