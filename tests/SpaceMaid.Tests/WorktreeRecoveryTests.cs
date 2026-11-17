using System.IO;
using System.Text.Json;
using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

[Trait("Stage", "System")]
public sealed class WorktreeRecoveryTests
{
    [Fact]
    public async Task RecheckOfUnchangedCapturedCheckoutRestoresOnlyExplicitFinalizationAuthority()
    {
        using var fixture = new TestDirectory(); using var repo = await GitWorktreeAnalyzerTests.GitFixture.Create(fixture.Path);
        string settings = Path.Join(fixture.Path, "retirement.json"); var service = new WorktreeRetirement(settings);
        var policy = await service.EnrollAsync(repo.Checkout, "refs/heads/main", 30, 7, true);
        var record = await service.QuarantineAsync(policy);
        Interrupt(service, settings, record);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FinalizeAsync(record.Id));

        var checkedRecord = await service.ReconcileAsync(record.Id);

        Assert.Equal(WorktreeRetirementState.Quarantined, checkedRecord.State);
        Assert.Equal(record.Entries, checkedRecord.Entries);
        Assert.False(Assert.Single(service.Load().Policies).Automatic);
        Assert.True(File.Exists(Path.Join(record.QuarantinePath, "source.txt")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FinalizeAsync(record.Id)); // Original grace period still applies.
        Assert.Equal(record.Head, (await GitWorktreeAnalyzerTests.GitFixture.Run(repo.Main, repo.Old, "rev-parse", record.RecoveryReference)).Trim());
    }

    [Fact]
    public async Task PartialCapturedContentsCanBeRestoredButNeverAdoptedAsANewDeletionSet()
    {
        using var fixture = new TestDirectory(); using var repo = await GitWorktreeAnalyzerTests.GitFixture.Create(fixture.Path);
        string settings = Path.Join(fixture.Path, "retirement.json"); var service = new WorktreeRetirement(settings);
        var policy = await service.EnrollAsync(repo.Checkout, "refs/heads/main", 30, 1, false);
        var record = await service.QuarantineAsync(policy);
        File.Delete(Path.Join(record.QuarantinePath, "source.txt"));
        string survivor = Path.Join(record.QuarantinePath, "new-local-work.txt"); File.WriteAllText(survivor, "keep this exact new work");
        Interrupt(service, settings, record);

        var checkedRecord = await service.ReconcileAsync(record.Id);

        Assert.Equal(WorktreeRetirementState.RestoreOnly, checkedRecord.State);
        Assert.Equal(record.Entries, checkedRecord.Entries);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FinalizeAsync(record.Id));
        var restored = await service.RestoreAsync(record.Id);
        Assert.Equal(WorktreeRetirementState.Restored, restored.State);
        Assert.Equal("keep this exact new work", File.ReadAllText(Path.Join(repo.Checkout, "new-local-work.txt")));
        Assert.False(File.Exists(repo.Source));
        Assert.Contains(record.RecoveryReference, restored.Message);
        Assert.Equal("owned tracked source", (await GitWorktreeAnalyzerTests.GitFixture.Run(repo.Main, repo.Old, "show", record.RecoveryReference + ":source.txt")).Trim());
    }

    [Fact]
    public async Task MissingGitPointerRequiresManualRecoveryAndNeverEnablesAnImpossibleRestore()
    {
        using var fixture = new TestDirectory(); using var repo = await GitWorktreeAnalyzerTests.GitFixture.Create(fixture.Path);
        string settings = Path.Join(fixture.Path, "retirement.json"); var service = new WorktreeRetirement(settings);
        var policy = await service.EnrollAsync(repo.Checkout, "refs/heads/main", 30, 1, false);
        var record = await service.QuarantineAsync(policy);
        File.Delete(Path.Join(record.QuarantinePath, ".git"));
        Interrupt(service, settings, record);

        var checkedRecord = await service.ReconcileAsync(record.Id);

        Assert.Equal(WorktreeRetirementState.NeedsReview, checkedRecord.State);
        Assert.Contains("Git metadata", checkedRecord.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(record.Id));
        Assert.Equal("owned tracked source", File.ReadAllText(Path.Join(record.QuarantinePath, "source.txt")));
        Assert.Equal(record.Entries, checkedRecord.Entries);
    }

    [Fact]
    public async Task MissingCheckoutNeverPrunesRegistrationAndCanCloseAfterIndependentRegistrationRepair()
    {
        using var fixture = new TestDirectory(); using var repo = await GitWorktreeAnalyzerTests.GitFixture.Create(fixture.Path);
        string settings = Path.Join(fixture.Path, "retirement.json"); var service = new WorktreeRetirement(settings);
        var policy = await service.EnrollAsync(repo.Checkout, "refs/heads/main", 30, 1, false);
        var record = await service.QuarantineAsync(policy);
        string registration = (await GitWorktreeAnalyzerTests.GitFixture.Run(record.QuarantinePath, repo.Old, "rev-parse", "--absolute-git-dir")).Trim();
        // Independently model a stopped finalization in this owned fixture.
        Directory.Delete(record.QuarantinePath, recursive: true);
        Interrupt(service, settings, record);

        var checkedRecord = await service.ReconcileAsync(record.Id);

        Assert.Equal(WorktreeRetirementState.NeedsReview, checkedRecord.State);
        Assert.Contains("registration", checkedRecord.Message);
        Assert.True(Directory.Exists(registration));
        Assert.False(Directory.Exists(record.QuarantinePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(record.Id));
        // Simulate a separately reviewed targeted Git-administration repair.
        Assert.StartsWith(Path.Join(repo.Main, ".git", "worktrees") + Path.DirectorySeparatorChar, registration, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(registration, recursive: true);
        var repaired = await service.ReconcileAsync(record.Id);
        Assert.Equal(WorktreeRetirementState.Completed, repaired.State);
        Assert.Equal(record.Head, (await GitWorktreeAnalyzerTests.GitFixture.Run(repo.Main, repo.Old, "rev-parse", record.RecoveryReference)).Trim());
        Assert.Equal(record.Head, (await GitWorktreeAnalyzerTests.GitFixture.Run(repo.Main, repo.Old, "rev-parse", policy.Branch)).Trim());
        Assert.True(File.Exists(Path.Join(repo.Main, "source.txt")));
    }

    [Fact]
    public async Task ReplacementAtTheQuarantinePathIsKeptAndDoesNotReceiveRestoreOrDeletionAuthority()
    {
        using var fixture = new TestDirectory(); using var repo = await GitWorktreeAnalyzerTests.GitFixture.Create(fixture.Path);
        string settings = Path.Join(fixture.Path, "retirement.json"); var service = new WorktreeRetirement(settings);
        var policy = await service.EnrollAsync(repo.Checkout, "refs/heads/main", 30, 1, false);
        var record = await service.QuarantineAsync(policy);
        string retained = record.QuarantinePath + "-preserved";
        Directory.Move(record.QuarantinePath, retained);
        Directory.CreateDirectory(record.QuarantinePath);
        string replacement = Path.Join(record.QuarantinePath, "new-data.txt"); File.WriteAllText(replacement, "unrelated replacement");
        Interrupt(service, settings, record);

        var checkedRecord = await service.ReconcileAsync(record.Id);

        Assert.Equal(WorktreeRetirementState.NeedsReview, checkedRecord.State);
        Assert.Contains("different object", checkedRecord.Message);
        Assert.Equal("unrelated replacement", File.ReadAllText(replacement));
        Assert.True(File.Exists(Path.Join(retained, "source.txt")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(record.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FinalizeAsync(record.Id));
    }

    private static void Interrupt(WorktreeRetirement service, string settings, WorktreeRetirementRecord record)
        => File.WriteAllText(settings, JsonSerializer.Serialize(service.Load() with
        { Records = [record with { State = WorktreeRetirementState.NeedsReview, Message = "Owned fixture interrupted finalization" }] }));
}
