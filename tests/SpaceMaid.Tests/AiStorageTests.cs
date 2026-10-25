using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class AiStorageTests
{
    public static IEnumerable<object[]> Patterns()
    {
        yield return Case(".claude/projects/project", "session.jsonl", "Claude Code project history", "Agent files");
        yield return Case(".claude/file-history", "session/file@v1", "Claude Code file checkpoints", "Agent checkpoints");
        yield return Case(".copilot/session-state", "session/events.jsonl", "GitHub Copilot session history", "Agent files");
        yield return Case(".gemini/tmp/project/chats", "session-2026.json", "Gemini CLI session history", "Agent files");
        yield return Case("AppData/Roaming/Cursor/User/workspaceStorage/project", "state.vscdb", "Cursor workspace history", "Agent files");
        yield return Case("AppData/Roaming/Cursor/User/globalStorage", "state.vscdb", "Cursor global chat storage", "Agent files", true);
        yield return Case("AppData/Roaming/Code/User/globalStorage/saoudrizwan.claude-dev/tasks/task", "api_conversation_history.json", "Cline task history", "Agent files");
        yield return Case("AppData/Roaming/Code/User/globalStorage/saoudrizwan.claude-dev/checkpoints", "repo/objects/snapshot", "Cline file checkpoints", "Agent checkpoints");
        yield return Case("AppData/Roaming/Cursor/User/globalStorage/rooveterinaryinc.roo-cline/tasks/task", "ui_messages.json", "Roo Code task history", "Agent files");
        yield return Case(".local/share/opencode/log", "opencode.log", "OpenCode logs", "Agent files");
        yield return Case(".local/share/opencode", "opencode.db", "OpenCode session storage", "Agent files", true);
        yield return Case(".cache/opencode", "package.json", "OpenCode download cache", "Tool caches");
        yield return Case(".lmstudio/models/publisher/model", "weights.gguf", "LM Studio model downloads", "AI models & data");

        static object[] Case(string path, string file, string title, string category, bool blocked = false)
            => [path, file, title, category, blocked];
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void RecognizedStorageHasSpecificAdviceAndNeverBecomesAutomaticallyReady(string path, string file,
        string title, string category, bool blocked)
    {
        var scan = AiStorageFixture.Inventory([path + "/" + file]);
        var finding = Assert.Single(OpportunityDetector.Find(scan), item => item.Path == AiStorageFixture.Full(path));
        Assert.Equal(title, finding.Title); Assert.Equal(category, finding.Category);
        Assert.True(finding.RequiresManualReview); Assert.False(finding.IsRegeneratable);
        Assert.Equal(blocked, finding.CleanupBlockReason.Length > 0);
        Assert.NotEmpty(finding.Caution); Assert.NotEmpty(finding.SuggestedAction);
        var assessment = BuildCleanupProtection.Assess(scan, [finding], new())[finding.DirectoryIndex];
        Assert.Equal(blocked ? CleanupReadiness.Protected : CleanupReadiness.NeedsReview, assessment.State);
    }

    [Theory]
    [InlineData("models--org--model", true)]
    [InlineData("datasets--org--data", false)]
    [InlineData("spaces--org--app", true)]
    public void HubRepositoriesRecognizeRelocationAndWindowsCopiesWithoutSelectingSharedBlobs(string repo, bool blobs)
    {
        string path = "relocated-hub/" + repo;
        var scan = AiStorageFixture.Inventory([path + "/snapshots/revision/weights.bin"], [path + (blobs ? "/blobs" : "/refs")]);
        var finding = Assert.Single(OpportunityDetector.Find(scan));
        Assert.Equal(AiStorageFixture.Full(path), finding.Path);
        Assert.Equal("Hugging Face repository downloads", finding.Title); Assert.Empty(finding.CleanupBlockReason);
        Assert.Contains("Windows", finding.Caution);
        Assert.Empty(OpportunityDetector.Find(AiStorageFixture.Inventory([path + "/snapshots/revision/weights.bin"])));
    }

    [Fact]
    public void RelocatedAndScanRootOllamaLibrariesRequireCompleteBlobEvidence()
    {
        string blob = "sha256-" + new string('a', 64);
        var scan = AiStorageFixture.Inventory(["custom/blobs/" + blob], ["custom/manifests"]);
        var finding = Assert.Single(OpportunityDetector.Find(scan));
        Assert.Equal(AiStorageFixture.Full("custom"), finding.Path); Assert.NotEmpty(finding.CleanupBlockReason);
        var root = AiStorageFixture.Inventory(["blobs/" + blob], ["manifests"]);
        Assert.Equal(root.RootPath, Assert.Single(OpportunityDetector.Find(root)).Path);
        Assert.Empty(OpportunityDetector.Find(AiStorageFixture.Inventory(["custom/blobs/sha256-short"], ["custom/manifests"])));
        Assert.Empty(OpportunityDetector.Find(AiStorageFixture.Inventory(["custom/blobs/" + blob])));
    }

    [Fact]
    public void OrdinaryFoldersCredentialsAndUncorroboratedNamesAreExcluded()
    {
        var scan = AiStorageFixture.Inventory([
            ".claude/settings.json", ".claude/projects/project/memory/MEMORY.md", ".codex/auth.json", ".copilot/config.json",
            ".local/share/opencode/auth.json", ".gemini/tmp/project/source.py", "ordinary/chats/session-1.json",
            "AppData/Roaming/Code/User/workspaceStorage/id/state.vscdb", "Cursor/User/workspaceStorage/id/readme.md",
            "models/my-model.gguf", "models--mine/snapshots/revision/data", "cache/weights.gguf",
            "globalStorage/other.extension/tasks/id/ui_messages.json", ".lmstudio/models/notes.txt",
            ".claude/projects/README.md", ".cache/huggingface/token"]);
        Assert.Empty(OpportunityDetector.Find(scan));
        Assert.Throws<OperationCanceledException>(() => OpportunityDetector.Find(scan, new(true)));
    }

    [Fact]
    public void AiGroupsSeparateHistoryCheckpointsAndModelsAndBreakDownByTool()
    {
        var scan = AiStorageFixture.Inventory(AiStorageFixture.ExampleFiles, [".cache/huggingface/hub/models--org--model/refs"]);
        var analysis = new SpaceMaid.Extensions.Developer.DeveloperExtension().Analyze(scan, default);
        var groups = analysis.Groups.Cleanup!;
        Assert.Contains(groups, group => group.Key == "cleanup:agent" && group.Children!.Any(child => child.Name == "Claude Code"));
        Assert.Contains(groups, group => group.Key == "cleanup:checkpoints" && group.Children!.Any(child => child.Name == "Claude Code"));
        var models = Assert.Single(groups, group => group.Key == "cleanup:models");
        Assert.True(models.CanSelectGroup); Assert.Equal(3, models.Children!.Count);
        Assert.All(models.Children, tool =>
        {
            Assert.Equal(tool.Name != "Ollama", tool.CanSelectGroup);
            Assert.All(tool.Children!, project => Assert.Equal(tool.Name != "Ollama", project.CanSelectGroup));
        });
        Assert.Single(models.Locations, item => item.CleanupBlockReason.Length > 0);
        var history = Assert.Single(groups, group => group.Key == "cleanup:agent");
        var rows = history.Locations.Select(item => new OpportunityRow(item, true)).ToArray();
        foreach (var row in rows) row.Selected = true;
        Assert.All(rows.Where(row => row.Item.CleanupBlockReason.Length > 0), row => Assert.False(row.Selected));
        var historyRow = new OpportunityGroupRow(history, rows);
        historyRow.SetSelectedCount(rows.Count(row => row.Selected));
        Assert.True(historyRow.Checked); // Kept rows do not leave an otherwise fully selected group indeterminate.
        Assert.Contains(analysis.Findings, item => item.IsWorktree);
        Assert.DoesNotContain(analysis.Findings, item => item.Path.EndsWith("worktrees", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ManagedStoresAndSelectedAncestorsAreKeptEvenIfTheCallerClearsTheRestriction()
    {
        var scan = AiStorageFixture.Inventory(["environment/pyvenv.cfg", "environment/.ollama/models/blobs/sha256-" + new string('b', 64)],
            ["environment/.ollama/models/manifests"]);
        var found = OpportunityDetector.Find(scan); Assert.Equal(2, found.Count);
        var forged = found.Select(item => item with { CleanupBlockReason = "" });
        var plan = CleanupPlanner.Prepare(scan, forged, discover: DeveloperOpportunityFixture.Find);
        Assert.False(plan.CanExecute); Assert.Empty(plan.Preview.Locations); Assert.Equal(2, plan.BlockedLocations.Count);
        Assert.Contains(plan.BlockedLocations, item => item.Reason.Contains("owning tool", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshExecutionKeepsNewlyRestrictedStoresAndAncestorsWithoutRecycling(bool ancestor)
    {
        using var fixture = new TestDirectory();
        string environment = Directory.CreateDirectory(Path.Join(fixture.Path, "environment")).FullName;
        File.WriteAllText(Path.Join(environment, "pyvenv.cfg"), "owned fixture");
        string models = Path.Join(environment, ".ollama", "models");
        string weights = Path.Join(models, "blobs", "sha256-" + new string('c', 64));
        if (!ancestor) AddLibrary();
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        IReadOnlyList<Opportunity> PreviouslyAllowed(ScanSnapshot current, CancellationToken token)
            => OpportunityDetector.Find(current, token).Select(item => item with { CleanupBlockReason = "" }).ToArray();
        var selected = PreviouslyAllowed(scan, default).Where(item => item.Path == (ancestor ? environment : models)).ToArray();
        var operations = new FileOperations();
        var plan = await operations.PrepareCleanupAsync(scan, selected, discover: PreviouslyAllowed);
        Assert.True(plan.CanExecute);
        if (ancestor) AddLibrary();
        var result = await operations.ExecuteCleanupAsync(plan, 0, new NoProgress(), default, (_, _) => { }, discover: DeveloperOpportunityFixture.Find);
        var skipped = Assert.Single(result.Locations);
        Assert.Equal(CleanupOutcome.Skipped, skipped.Outcome); Assert.False(skipped.Attempted);
        Assert.Equal(0, result.RecycledCount); Assert.Null(result.ValidationError);
        Assert.Contains("ollama", skipped.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserve weights", File.ReadAllText(weights));

        void AddLibrary()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(weights)!); Directory.CreateDirectory(Path.Join(models, "manifests"));
            File.WriteAllText(weights, "preserve weights");
        }
    }
    private sealed class NoProgress : IProgress<CleanupProgress> { public void Report(CleanupProgress value) { } }
}

internal static class AiStorageFixture
{
    internal static readonly string[] ExampleFiles = [
        ".codex/sessions/2026/session.jsonl", ".claude/projects/project/session.jsonl", ".claude/file-history/session/snapshot",
        ".copilot/session-state/session/events.jsonl", ".gemini/tmp/project/chats/session-1.json",
        "AppData/Roaming/Cursor/User/globalStorage/state.vscdb", ".local/share/opencode/opencode.db",
        ".cache/huggingface/hub/models--org--model/snapshots/revision/model.safetensors",
        ".ollama/models/blobs/sha256-" + new string('a', 64), ".ollama/models/manifests/registry/model/latest",
        ".lmstudio/models/publisher/model/model.gguf", ".codex/worktrees/task/project/.git",
        ".codex/worktrees/task/project/package.json", ".codex/worktrees/task/project/node_modules/package/index.js"
    ];
    internal static string Full(string path) => Path.Join(@"G:\fixture", path.Replace('/', Path.DirectorySeparatorChar));
    internal static ScanSnapshot Inventory(IEnumerable<string> files, IEnumerable<string>? folders = null)
    {
        var builder = new ScanBuilder(); ulong next = 1;
        builder.RootIndex = builder.AddFile(new(new(next++), FileAttributes.Directory, 0, 0, 0));
        var paths = new Dictionary<string, (int Index, ulong Id)>(StringComparer.Ordinal) { [""] = (builder.RootIndex, 1) };
        foreach (string folder in folders ?? []) Folder(folder);
        foreach (string file in files)
        {
            int slash = file.LastIndexOf('/'); var parent = Folder(slash < 0 ? "" : file[..slash]);
            int index = builder.AddFile(new(new(next++), FileAttributes.Normal, 1L << 30, 1L << 30, 0));
            builder.AddLink(index, new(parent.Id), slash < 0 ? file : file[(slash + 1)..]);
        }
        return builder.Build(@"G:\fixture", "AI storage fixture", TimeSpan.Zero, 0, default);

        (int Index, ulong Id) Folder(string path)
        {
            if (paths.TryGetValue(path, out var existing)) return existing;
            int slash = path.LastIndexOf('/'); var parent = Folder(slash < 0 ? "" : path[..slash]);
            ulong id = next++; int index = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(index, new(parent.Id), slash < 0 ? path : path[(slash + 1)..]);
            return paths[path] = (index, id);
        }
    }
}
