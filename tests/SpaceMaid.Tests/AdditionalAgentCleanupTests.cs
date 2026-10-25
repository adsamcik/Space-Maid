using System.IO;
using System.Text.Json;
using SpaceMaid.Extensions.Agents;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class AdditionalAgentCleanupTests
{
    [Fact]
    public void SupportedInsidersEditorProcessesProtectBothExtensionTools()
    {
        foreach (var tool in new[] { AgentTool.Cline, AgentTool.RooCode })
            Assert.Contains("Code - Insiders", AgentOwnerProtection.OwnerNames(tool));
    }
    [Theory]
    [InlineData(4, "session-state/session-one/events.jsonl")]
    [InlineData(5, "tasks/task-one/api_conversation_history.json")]
    [InlineData(5, "data/sessions/session-one/session-one.messages.json")]
    [InlineData(6, "tasks/task-one/ui_messages.json")]
    [InlineData(7, "log/2026-01-01T123456.log")]
    public void SupportedFilesAreDiscoveredWithoutAdmittingSharedStores(int tool, string relative)
    {
        using var fixture = new TestDirectory(); var home = new AgentHome((AgentTool)tool, fixture.Path);
        string file = Write(home.Path, relative);
        Write(home.Path, "auth.json"); Write(home.Path, "session-store.db"); Write(home.Path, "state.vscdb");
        Write(home.Path, "settings.json"); Write(home.Path, "tasks/_index.json");
        if (home.Tool == AgentTool.RooCode) Roo(home.Path, "completed");
        Age(home.Path);
        var snapshot = AgentHistoryCleanup.Inspect([home], 30, DateTime.UtcNow, default);
        Assert.Empty(snapshot.Warnings); var item = Assert.Single(snapshot.Items); Assert.Equal(file, item.Path);
        var plan = AgentHistoryCleanup.Prepare([item], 30, default, _ => null);
        Assert.Equal(file, Assert.Single(plan.Items).Path);
        Assert.True(File.Exists(Path.Join(home.Path, "auth.json")));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("delegated")]
    [InlineData("unknown")]
    [InlineData("completed", true)]
    public void RooActiveUnknownAndRelatedTasksAreKept(string status, bool child = false)
    {
        using var fixture = new TestDirectory(); var home = new AgentHome(AgentTool.RooCode, fixture.Path);
        Write(home.Path, "tasks/task-one/ui_messages.json"); Roo(home.Path, status, child); Age(home.Path);
        var snapshot = AgentHistoryCleanup.Inspect([home], 30, DateTime.UtcNow, default);
        Assert.Empty(snapshot.Items); Assert.NotEmpty(snapshot.Warnings);
    }

    [Fact]
    public async Task RelatedActivityLocksAndRunningHostsBlockCleanupWithoutCallingRecycler()
    {
        using var fixture = new TestDirectory(); var home = new AgentHome(AgentTool.Copilot, fixture.Path);
        Write(home.Path, "session-state/session-one/events.jsonl"); Age(home.Path);
        var item = Assert.Single(AgentHistoryCleanup.Inspect([home], 30, DateTime.UtcNow, default).Items);
        Assert.Throws<IOException>(() => AgentHistoryCleanup.Prepare([item], 30, default, _ => "copilot"));
        var plan = AgentHistoryCleanup.Prepare([item], 30, default, _ => null); int calls = 0;
        await Assert.ThrowsAsync<IOException>(() => AgentHistoryCleanup.ExecuteAsync(plan, 0, default,
            (_, _, _) => { calls++; return Task.FromResult(true); }, _ => "copilot"));
        Assert.Equal(0, calls);
        Write(home.Path, "session-state/session-one/files/new-work.txt");
        await Assert.ThrowsAsync<IOException>(() => AgentHistoryCleanup.ExecuteAsync(plan, 0, default,
            (_, _, _) => { calls++; return Task.FromResult(true); }, _ => null));
        Assert.Equal(0, calls);
        Assert.Empty(AgentHistoryCleanup.Inspect([home], 30, DateTime.UtcNow, default).Items);
        Age(home.Path); Write(home.Path, "session-state/session-one/events.jsonl.lock"); Age(home.Path);
        var locked = AgentHistoryCleanup.Inspect([home], 30, DateTime.UtcNow, default);
        Assert.Empty(locked.Items); Assert.NotEmpty(locked.Warnings);
    }

    [Fact]
    public void RecentEmptyRelatedDirectoriesAreActivityAndMissingEvidenceCannotBypassChecks()
    {
        using var fixture = new TestDirectory(); var home = new AgentHome(AgentTool.Cline, fixture.Path);
        Write(home.Path, "tasks/task-one/ui_messages.json"); Age(home.Path);
        var item = Assert.Single(AgentHistoryCleanup.Inspect([home], 30, DateTime.UtcNow, default).Items);
        Assert.Throws<InvalidOperationException>(() => AgentHistoryCleanup.Prepare([item with { SessionEvidence = null }], 30, default, _ => null));
        Directory.CreateDirectory(Path.Join(home.Path, "tasks/task-one/new-work"));
        Assert.Empty(AgentHistoryCleanup.Inspect([home], 30, DateTime.UtcNow, default).Items);
    }

    [Fact]
    public async Task MultipleFilesFromOneTaskCanMoveWhileUnselectedRecoveryDataStays()
    {
        using var fixture = new TestDirectory(); var home = new AgentHome(AgentTool.RooCode, fixture.Path);
        Write(home.Path, "tasks/task-one/api_conversation_history.json"); Write(home.Path, "tasks/task-one/ui_messages.json");
        string leftover = Write(home.Path, "tasks/task-one/.ui_messages.json.bak_1700000000000_abc123.tmp");
        string checkpoint = Write(home.Path, "tasks/task-one/checkpoints/recovery.txt"); Roo(home.Path, "completed"); Age(home.Path);
        var snapshot = AgentHistoryCleanup.Inspect([home], 30, DateTime.UtcNow, default);
        Assert.Equal(3, snapshot.Items.Count);
        var plan = AgentHistoryCleanup.Prepare(snapshot.Items, 30, default, _ => null);
        Assert.Equal(leftover, plan.Items[0].Path);
        string archive = Directory.CreateDirectory(Path.Join(fixture.Path, "recycled-fixture")).FullName;
        var result = await AgentHistoryCleanup.ExecuteAsync(plan, 0, default, (item, _, _) =>
        { File.Move(item.Path, Path.Join(archive, Path.GetFileName(item.Path))); return Task.FromResult(true); }, _ => null);
        Assert.Equal(3, result.Recycled); Assert.True(File.Exists(checkpoint));
        Assert.True(File.Exists(Path.Join(home.Path, "tasks/task-one/history_item.json")));
    }

    [Fact]
    public void OrphanRooRecoveryCopiesAndBackgroundClineOwnersStayProtected()
    {
        using var fixture = new TestDirectory();
        var roo = new AgentHome(AgentTool.RooCode, Path.Join(fixture.Path, "roo"));
        Write(roo.Path, "tasks/task-one/.ui_messages.json.new_1700000000000_abc123.tmp"); Roo(roo.Path, "completed"); Age(roo.Path);
        Assert.Empty(AgentHistoryCleanup.Inspect([roo], 30, DateTime.UtcNow, default).Items);
        var cline = new AgentHome(AgentTool.Cline, Path.Join(fixture.Path, "cline"));
        Write(cline.Path, "data/sessions/session-one/session-one.messages.json"); Write(cline.Path, "locks/hub/owners/owner.json"); Age(cline.Path);
        Assert.Empty(AgentHistoryCleanup.Inspect([cline], 30, DateTime.UtcNow, default).Items);
    }

    private static string Write(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Join(root, relative)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "owned fixture"); return path;
    }
    private static void Roo(string root, string status, bool child = false)
    {
        string path = Write(root, "tasks/task-one/history_item.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { id = "task-one", status, childIds = child ? new[] { "child" } : Array.Empty<string>() }));
    }
    private static void Age(string root)
    {
        var old = DateTime.UtcNow.AddDays(-60);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, old);
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) Directory.SetLastWriteTimeUtc(directory, old);
        Directory.SetLastWriteTimeUtc(root, old);
    }
}
