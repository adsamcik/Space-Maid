using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace SpaceMaid.Extensions.Agents;

internal enum AgentTool { Codex, ClaudeCode, GeminiCli, Cursor, Copilot, Cline, RooCode, OpenCode }
internal sealed record AgentHome(AgentTool Tool, string Path)
{
    internal string ToolName => Tool switch
    {
        AgentTool.ClaudeCode => "Claude Code", AgentTool.GeminiCli => "Gemini CLI", AgentTool.Cursor => "Cursor CLI",
        AgentTool.Copilot => "Copilot", AgentTool.Cline => "Cline", AgentTool.RooCode => "Roo Code", AgentTool.OpenCode => "OpenCode", _ => "Codex"
    };
}
internal sealed record AgentHistoryItem(AgentHome Home, string Path, string Kind, AgentFileIdentity Identity,
    CursorChatSnapshot? Folder = null, AgentSessionEvidence? SessionEvidence = null)
{
    public string Tool => Home.ToolName;
    public string Name => System.IO.Path.GetFileName(Path);
    public long Bytes => Folder?.LogicalBytes ?? Identity.Length;
    public bool IsFolder => Folder is not null;
    public long LastActivityFileTime => Folder?.LatestWriteFileTime ?? SessionEvidence?.LatestWriteFileTime ?? Identity.LastWriteFileTime;
    public DateTime LastChanged => DateTime.FromFileTimeUtc(LastActivityFileTime).ToLocalTime();
    public string SizeText => AgentHistoryCleanup.Size(Bytes);
    public string Scope => Folder is null ? "Selected file only" : $"Whole Cursor CLI chat folder · {Folder.Members.Count(member => member.RelativePath.Length > 0):N0} entries";
    public string Contents => Folder is null ? "" : string.Join(Environment.NewLine, Folder.Members.Where(member => member.RelativePath.Length > 0).Select(member => member.RelativePath));
}
internal sealed record AgentHistorySnapshot(IReadOnlyList<AgentHistoryItem> Items, IReadOnlyList<string> Warnings);
internal sealed record AgentHistoryPlan(IReadOnlyList<AgentHistoryItem> Items, int OlderThanDays)
{
    public string Summary => $"{Items.Count:N0} {(Items.Count == 1 ? "item" : "items")} · {AgentHistoryCleanup.Size(Items.Sum(item => item.Bytes))} observed";
}
internal sealed record AgentHistoryResult(int Recycled, string Message);

// Fixed known file layouts only. Discovery reads names and native metadata,
// never transcripts, databases, credentials, process environments or memory.
internal static partial class AgentHistoryCleanup
{
    internal const int MaximumEntries = 20000;
    internal static IReadOnlyList<AgentHome> DefaultHomes()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [new(AgentTool.Codex, System.IO.Path.Join(profile, ".codex")),
            new(AgentTool.ClaudeCode, System.IO.Path.Join(profile, ".claude")),
            new(AgentTool.GeminiCli, System.IO.Path.Join(profile, ".gemini")),
            .. AdditionalHomes(profile)];
    }

    internal static async Task<AgentHistorySnapshot> InspectAsync(IReadOnlyList<AgentHome> homes, int olderThanDays, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromMinutes(1));
        try { return await Task.Run(() => Inspect(homes, olderThanDays, DateTime.UtcNow, budget.Token), budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Inspection reached its one-minute limit. Choose a single agent home and inspect again."); }
    }

    internal static AgentHistorySnapshot Inspect(IReadOnlyList<AgentHome> homes, int olderThanDays, DateTime utcNow, CancellationToken token)
    {
        ValidateAge(olderThanDays); RequireUtc(utcNow);
        var items = new List<AgentHistoryItem>(); var warnings = new List<string>(); int visited = 0;
        foreach (var source in homes.Distinct())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string root = AgentFileTarget.ValidatePath(source.Path);
                if (!Directory.Exists(root)) continue;
                AgentFileTarget.ValidateAncestors(root, includeSelf: true);
                var home = source with { Path = root };
                string[] roots = home.Tool switch
                {
                    AgentTool.Codex => ["sessions", "archived_sessions", "log", "logs"],
                    AgentTool.ClaudeCode => ["projects", "debug"],
                    AgentTool.GeminiCli => ["tmp"],
                    _ => AdditionalRoots(home.Tool)
                };
                foreach (string child in roots)
                {
                    string basePath = System.IO.Path.Join(root, child);
                    if (Directory.Exists(basePath)) Walk(basePath, home, 1);
                }
            }
            catch (Exception error) when (Expected(error))
            { warnings.Add(source.ToolName + ": " + error.Message); }
        }
        return new(items.OrderByDescending(item => item.Bytes).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray(), warnings.ToArray());

        void Walk(string path, AgentHome home, int depth)
        {
            token.ThrowIfCancellationRequested();
            AgentFileTarget.ValidateAncestors(path, includeSelf: true);
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > MaximumEntries) throw new IOException($"Inspection reached its {MaximumEntries:N0}-entry limit. The list is partial; choose one agent home to narrow it.");
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { warnings.Add("Linked path kept: " + child); continue; }
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (home.Tool == AgentTool.Cursor && CursorChatCleanup.IsChatFolder(home, child))
                    {
                        try
                        {
                            var folder = CursorChatCleanup.Capture(home, child, token);
                            if (OldEnough(folder.LatestWriteFileTime, olderThanDays, utcNow))
                                items.Add(new(home, child, "CLI chat folder", folder.RootIdentity, folder));
                        }
                        catch (Exception error) when (Expected(error)) { warnings.Add("Kept " + child + ": " + error.Message); }
                        continue;
                    }
                    if (ShouldDescend(home, child, depth)) Walk(child, home, depth + 1);
                    continue;
                }
                string? kind = KnownFile(home, child);
                if (kind is null) continue;
                try
                {
                    var identity = AgentFileTarget.Capture(child);
                    var evidence = AgentSessionEvidence.Capture(home, child, token);
                    if (OldEnough(evidence?.LatestWriteFileTime ?? identity.LastWriteFileTime, olderThanDays, utcNow))
                        items.Add(new(home, child, kind, identity, SessionEvidence: evidence));
                }
                catch (Exception error) when (Expected(error)) { warnings.Add("Kept " + child + ": " + error.Message); }
            }
        }
    }

    // Only descend through structural directories needed to find known files.
    // This deliberately excludes Claude memory, checkpoints and session sidecars.
    private static bool ShouldDescend(AgentHome home, string path, int depth)
    {
        string[] parts = RelativeParts(home, path);
        return home.Tool switch
        {
            AgentTool.Codex => parts[0] == "sessions" && depth <= 3 && parts[^1].All(char.IsAsciiDigit)
                && parts[^1].Length == (depth == 1 ? 4 : 2),
            AgentTool.ClaudeCode => parts[0] == "projects" && depth == 1,
            AgentTool.GeminiCli => parts[0] == "tmp" && (depth == 1 || depth == 2 && parts[^1] == "chats"),
            _ => AdditionalDescend(home, parts, depth)
        };
    }

    internal static string? KnownFile(AgentHome home, string path)
    {
        path = AgentFileTarget.ValidatePath(path); string[] p = RelativeParts(home, path);
        if (p.Length == 0 || p.Any(part => part is "." or "..")) return null;
        string name = p[^1];
        return home.Tool switch
        {
            AgentTool.Codex when p.Length == 5 && p[0] == "sessions" && DateTime.TryParseExact(string.Join('-', p[1..4]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                && name.StartsWith("rollout-", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal) => "Session transcript",
            AgentTool.Codex when p.Length == 2 && p[0] == "archived_sessions" && name.StartsWith("rollout-", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal) => "Archived transcript",
            AgentTool.Codex when p.Length == 2 && p[0] is "log" or "logs" && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) => "Diagnostic log",
            AgentTool.ClaudeCode when p.Length == 3 && p[0] == "projects" && name.EndsWith(".jsonl", StringComparison.Ordinal) && Guid.TryParse(name[..^6], out _) => "Session transcript",
            AgentTool.ClaudeCode when p.Length == 2 && p[0] == "debug" && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(name[..^4], out _) => "Diagnostic log",
            AgentTool.GeminiCli when p.Length == 4 && p[0] == "tmp" && p[2] == "chats" && name.StartsWith("session-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal) => "Session transcript",
            _ => AdditionalKnownFile(home, p)
        };
    }

    private static string[] RelativeParts(AgentHome home, string path)
    {
        string root = AgentFileTarget.ValidatePath(home.Path);
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return [];
        return path[(root.Length + 1)..].Split('\\');
    }

    internal static AgentHistoryPlan Prepare(IReadOnlyList<AgentHistoryItem> selected, int olderThanDays, CancellationToken token,
        Func<AgentTool, string?>? runningOwner = null)
    {
        ValidateAge(olderThanDays);
        if (selected.Count == 0) throw new InvalidOperationException("Choose at least one history file.");
        if (selected.Count > 500) throw new InvalidOperationException("Review at most 500 files at once.");
        var items = selected.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Kind == "Task write leftover" ? 0 : 1).ToArray();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested(); AgentOwnerProtection.RequireStopped(item.Home.Tool, runningOwner);
            Validate(item, olderThanDays, DateTime.UtcNow);
            if (item.Folder is not null) { using var folderLease = CursorChatCleanup.Lease(item, token); CursorChatCleanup.Validate(item, olderThanDays, token); continue; }
            using var lease = AgentFileTarget.Lease(item);
            Validate(item, olderThanDays, DateTime.UtcNow);
        }
        return new(Array.AsReadOnly(items), olderThanDays);
    }

    internal static void Validate(AgentHistoryItem item, int olderThanDays, DateTime utcNow)
    {
        ValidateAge(olderThanDays); RequireUtc(utcNow);
        if (item.Folder is not null) { CursorChatCleanup.Validate(item, olderThanDays, default); return; }
        if (KnownFile(item.Home, item.Path) != item.Kind) throw new InvalidOperationException("This is not a supported history file. Inspect again.");
        if (!OldEnough(item.LastActivityFileTime, olderThanDays, utcNow)) throw new InvalidOperationException("Recent history is kept. Inspect again with the intended age filter.");
        if (item.SessionEvidence is null && AgentSessionEvidence.Capture(item.Home, item.Path, default) is not null)
            throw new InvalidOperationException("This session requires related-file checks. Inspect again before cleanup.");
        item.SessionEvidence?.Validate(item, default);
        if (AgentFileTarget.Capture(item.Path) != item.Identity) throw new InvalidOperationException("This history file changed or was replaced after inspection. Inspect again.");
    }

    internal static async Task<AgentHistoryResult> ExecuteAsync(AgentHistoryPlan plan, nint owner, CancellationToken token,
        Func<AgentHistoryItem, nint, CancellationToken, Task<bool>>? recycle = null, Func<AgentTool, string?>? runningOwner = null)
    {
        // Validate the complete set before starting; each file is checked once
        // more under its native write-denying lease at the Shell callback.
        await Task.Run(() =>
        {
            foreach (var item in plan.Items)
            { token.ThrowIfCancellationRequested(); AgentOwnerProtection.RequireStopped(item.Home.Tool, runningOwner); Validate(item, plan.OlderThanDays, DateTime.UtcNow); }
        }, token).ConfigureAwait(false);
        int completed = 0;
        var removed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var original in plan.Items)
            {
                var item = original.SessionEvidence is { } evidence
                    ? original with { SessionEvidence = evidence.AfterOwnRemovals(original, removed, token) } : original;
                token.ThrowIfCancellationRequested(); AgentOwnerProtection.RequireStopped(item.Home.Tool, runningOwner); Validate(item, plan.OlderThanDays, DateTime.UtcNow);
                bool moved = recycle is null ? item.Folder is not null ? await CursorChatCleanup.RecycleAsync(item, owner, token, plan.OlderThanDays)
                    : await AgentFileRecycler.RecycleAsync(item, owner, token)
                    : await recycle(item, owner, token);
                if (!moved) return new(completed, $"Stopped after {completed:N0} confirmed files. The last file may have moved; inspect again before retrying.");
                completed++;
                removed.Add(item.Path);
            }
            return new(completed, $"Moved {completed:N0} items to the Recycle Bin. They still occupy space until the bin is emptied. Separate app indexes and related files were kept.");
        }
        catch (Exception error) when (Expected(error))
        { return new(completed, $"Stopped after {completed:N0} confirmed files. {error.Message} The last file may have moved; inspect again before retrying."); }
    }

    private static bool OldEnough(long lastWriteFileTime, int days, DateTime utcNow)
        => lastWriteFileTime > 0 && DateTime.FromFileTimeUtc(lastWriteFileTime) <= utcNow.AddDays(-days);
    private static void RequireUtc(DateTime value)
    { if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC observation time is required."); }
    private static void ValidateAge(int days)
    { if (days is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(days), "Choose 1 through 3650 days."); }
    internal static string Size(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):N1} GB" : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):N1} MB" : $"{bytes / 1024d:N1} KB";
    internal static bool Expected(Exception error) => error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
        or Win32Exception or ArgumentException or COMException or OperationCanceledException or System.Text.Json.JsonException;
}
