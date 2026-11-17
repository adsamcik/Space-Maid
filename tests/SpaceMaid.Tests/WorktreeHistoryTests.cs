using System.IO;
using System.Text.Json;
using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class WorktreeHistoryTests
{
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
}
