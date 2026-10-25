using System.ComponentModel;
using System.IO;
using SpaceMaid.Extensions.Agents;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class AgentHistoryCleanupTests
{
    private static readonly string SessionId = "f6abf5b8-5fa4-461e-984c-7249263df961";
    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    public void InspectionFindsOldKnownFilesWithoutReadingContentsAndKeepsSharedState()
    {
        using var fixture = new TestDirectory();
        var codex = new AgentHome(AgentTool.Codex, Path.Join(fixture.Path, "codex-home"));
        var claude = new AgentHome(AgentTool.ClaudeCode, Path.Join(fixture.Path, "claude-home"));
        var gemini = new AgentHome(AgentTool.GeminiCli, Path.Join(fixture.Path, "gemini-home"));
        string[] expected = [Write(codex, "sessions/2025/01/01/rollout-old.jsonl"), Write(codex, "archived_sessions/rollout-archived.jsonl"),
            Write(codex, "log/codex-tui.log"), Write(claude, "projects/project/" + SessionId + ".jsonl"),
            Write(claude, "debug/" + SessionId + ".txt"), Write(gemini, "tmp/project/chats/session-old.json")];
        Write(codex, "sessions/2025/01/01/rollout-recent.jsonl", Now);
        Write(codex, "auth.json"); Write(codex, "state_5.sqlite"); Write(codex, "session_index.jsonl");
        Write(claude, "projects/project/memory/MEMORY.md"); Write(claude, "projects/project/memory/nested/" + SessionId + ".jsonl");
        Write(claude, "projects/project/" + SessionId + "/subagents/agent.jsonl"); Write(claude, "file-history/" + SessionId + "/snapshot");
        Write(gemini, "tmp/project/logs.json"); Write(gemini, "oauth_creds.json");

        var snapshot = AgentHistoryCleanup.Inspect([codex, claude, gemini], 30, Now, default);
        Assert.Empty(snapshot.Warnings);
        Assert.Equal(expected.Order(StringComparer.Ordinal), snapshot.Items.Select(item => item.Path).Order(StringComparer.Ordinal));
        // Contents are deliberately not valid JSON; only exact layouts and
        // metadata determine candidates. Merely inspecting preserves all files.
        Assert.All(expected, path => Assert.Equal("owned fixture, not JSON", File.ReadAllText(path)));
    }

    [Theory]
    [InlineData(0, "sessions/2025/13/99/rollout-old.jsonl")]
    [InlineData(0, "sessions/2025/01/01/unrelated.jsonl")]
    [InlineData(0, "sessions/rollout-old.jsonl")]
    [InlineData(1, "projects/project/settings.jsonl")]
    [InlineData(1, "projects/project/memory/session.jsonl")]
    [InlineData(2, "tmp/project/chats/settings.json")]
    public void UnknownAndSharedLayoutsCannotEnterAPlan(int tool, string relative)
    {
        using var fixture = new TestDirectory(); var home = new AgentHome((AgentTool)tool, fixture.Path);
        string path = Write(home, relative);
        var item = new AgentHistoryItem(home, path, "Session transcript", AgentFileTarget.Capture(path));
        Assert.Null(AgentHistoryCleanup.KnownFile(home, path));
        Assert.Throws<InvalidOperationException>(() => AgentHistoryCleanup.Prepare([item], 30, default));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ChangedFileBlocksEntireConfirmedSetBeforeAnyMutation()
    {
        using var fixture = new TestDirectory(); var home = new AgentHome(AgentTool.Codex, fixture.Path);
        Write(home, "archived_sessions/rollout-first.jsonl"); string changed = Write(home, "archived_sessions/rollout-second.jsonl");
        var items = AgentHistoryCleanup.Inspect([home], 30, Now, default).Items;
        var plan = AgentHistoryCleanup.Prepare(items, 30, default);
        File.AppendAllText(changed, "changed"); int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentHistoryCleanup.ExecuteAsync(plan, 0, default,
            (_, _, _) => { calls++; return Task.FromResult(true); }));
        Assert.Equal(0, calls); Assert.All(items, item => Assert.True(File.Exists(item.Path)));
    }

    [Fact]
    public void NativeIdentityDetectsReplacementEvenWithSameLengthAndTimestamp()
    {
        using var fixture = new TestDirectory(); var home = new AgentHome(AgentTool.Codex, fixture.Path);
        string path = Write(home, "archived_sessions/rollout-old.jsonl");
        var item = AgentHistoryCleanup.Inspect([home], 30, Now, default).Items.Single();
        File.Move(path, path + ".preserved"); Write(home, "archived_sessions/rollout-old.jsonl");
        File.SetLastWriteTimeUtc(path, DateTime.FromFileTimeUtc(item.Identity.LastWriteFileTime));
        Assert.Throws<InvalidOperationException>(() => AgentHistoryCleanup.Prepare([item], 30, default));
    }

    [Fact]
    public void StableLeaseBlocksWritesAndAncestorRenameThenReleases()
    {
        using var fixture = new TestDirectory(); var home = new AgentHome(AgentTool.Codex, fixture.Path);
        string path = Write(home, "archived_sessions/rollout-old.jsonl");
        var item = AgentHistoryCleanup.Inspect([home], 30, Now, default).Items.Single();
        string parent = Path.GetDirectoryName(path)!;
        using (AgentFileTarget.Lease(item))
        {
            Assert.Throws<IOException>(() => File.AppendAllText(path, "blocked"));
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-moved"));
            Assert.Equal(item.Identity, AgentFileTarget.Capture(path));
        }
        File.AppendAllText(path, "released"); Directory.Move(parent, parent + "-moved");
        Assert.True(File.Exists(Path.Join(parent + "-moved", Path.GetFileName(path))));
    }

    [Fact]
    public void ActiveDataWriterBlocksPreparationEvenIfMetadataIsOld()
    {
        using var fixture = new TestDirectory(); var home = new AgentHome(AgentTool.Codex, fixture.Path);
        string path = Write(home, "archived_sessions/rollout-old.jsonl");
        var item = AgentHistoryCleanup.Inspect([home], 30, Now, default).Items.Single();
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Assert.Throws<Win32Exception>(() => AgentHistoryCleanup.Prepare([item], 30, default));
    }

    [Fact]
    public async Task ConfirmationUsesExactSelectedFilesAndStopsOnUnconfirmedResult()
    {
        using var fixture = new TestDirectory(); var home = new AgentHome(AgentTool.Codex, fixture.Path);
        Write(home, "archived_sessions/rollout-first.jsonl"); Write(home, "archived_sessions/rollout-second.jsonl");
        var items = AgentHistoryCleanup.Inspect([home], 30, Now, default).Items;
        var plan = AgentHistoryCleanup.Prepare(items, 30, default); var calls = new List<string>();
        var result = await AgentHistoryCleanup.ExecuteAsync(plan, 42, default, (item, owner, _) =>
        { Assert.Equal(42, owner); calls.Add(item.Path); return Task.FromResult(false); });
        Assert.Equal(0, result.Recycled); Assert.Single(calls); Assert.Contains("may have moved", result.Message);
        Assert.All(items, item => Assert.True(File.Exists(item.Path)));
    }

    [Fact]
    public async Task CancelledPlanNeverInvokesRecyclerAndRecentFilesCannotBePrepared()
    {
        using var fixture = new TestDirectory(); var home = new AgentHome(AgentTool.Codex, fixture.Path);
        string path = Write(home, "archived_sessions/rollout-old.jsonl");
        var item = AgentHistoryCleanup.Inspect([home], 30, Now, default).Items.Single();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentHistoryCleanup.ExecuteAsync(new([item], 30), 0, cancellation.Token,
            (_, _, _) => { calls++; return Task.FromResult(true); }));
        Assert.Equal(0, calls);
        File.SetLastWriteTimeUtc(path, Now);
        var current = item with { Identity = AgentFileTarget.Capture(path) };
        Assert.Throws<InvalidOperationException>(() => AgentHistoryCleanup.Prepare([current], 1, default));
    }

    [Fact]
    public void RecyclerRefusesPermanentDeletionAndHonoursCancellation()
    {
        Assert.NotEqual(0, AgentFileRecycler.DeletePermission(0, default));
        Assert.Equal(0, AgentFileRecycler.DeletePermission(0x80, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.NotEqual(0, AgentFileRecycler.DeletePermission(0x80, cancellation.Token));
    }

    [Fact]
    public async Task WindowsRecyclesOnlyTheOwnedConfirmedTranscriptAndVerifiesItsDestination()
    {
        using var fixture = new TestDirectory(); var home = new AgentHome(AgentTool.Codex, fixture.Path);
        string path = Write(home, "archived_sessions/rollout-owned-native-test.jsonl");
        string kept = Write(home, "auth.json");
        var item = AgentHistoryCleanup.Inspect([home], 30, Now, default).Items.Single();
        var plan = AgentHistoryCleanup.Prepare([item], 30, default);
        var result = await AgentHistoryCleanup.ExecuteAsync(plan, 0, default);
        Assert.Equal(1, result.Recycled);
        Assert.False(File.Exists(path)); Assert.True(File.Exists(kept));
        // AgentFileRecycler validates the exact same native identity, length
        // and timestamp at the Shell-reported Recycle Bin destination before
        // reporting success. No permanent-delete fallback exists.
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\history")]
    [InlineData(@"C:\$Recycle.Bin\history")]
    [InlineData(@"C:\history\..\auth.json")]
    [InlineData(@"C:\history\file.jsonl:stream")]
    public void UnsafePathsAreRejected(string path) => Assert.Throws<InvalidOperationException>(() => AgentFileTarget.ValidatePath(path));

    private static string Write(AgentHome home, string relative, DateTime? changed = null)
    {
        string path = Path.Join(home.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "owned fixture, not JSON");
        File.SetLastWriteTimeUtc(path, changed ?? Now.AddDays(-90)); return path;
    }
}
