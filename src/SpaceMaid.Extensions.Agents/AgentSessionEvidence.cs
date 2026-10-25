using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpaceMaid.Extensions.Agents;

internal sealed record AgentSessionMember(string Path, bool Directory, AgentFileIdentity Identity);
internal sealed record AgentSessionEvidence(string Root, IReadOnlyList<AgentSessionMember> Members, long? AcceptedActivity = null)
{
    internal long LatestWriteFileTime => AcceptedActivity ?? Members.Max(member => member.Identity.LastWriteFileTime);

    internal static AgentSessionEvidence? Capture(AgentHome home, string path, CancellationToken token)
    {
        if (home.Tool is not (AgentTool.Copilot or AgentTool.Cline or AgentTool.RooCode)
            || AgentHistoryCleanup.KnownFile(home, path) is not ("Session transcript" or "Task transcript" or "Task write leftover")) return null;
        string root = System.IO.Path.GetDirectoryName(path)!;
        if (AgentHistoryCleanup.LeftoverCanonical(System.IO.Path.GetFileName(path)) is { } canonical
            && !File.Exists(System.IO.Path.Join(root, canonical)))
            throw new IOException("This leftover may be the only recovery copy. Its canonical transcript is missing, so the file is kept.");
        string owners = System.IO.Path.Join(home.Path, "locks", "hub", "owners");
        if (home.Tool == AgentTool.Cline && Directory.Exists(owners))
        {
            AgentFileTarget.ValidateAncestors(owners, includeSelf: true);
            if (Directory.EnumerateFileSystemEntries(owners).Any())
                throw new IOException("Cline background owner registrations are present. Stop its hub and agents before cleanup.");
        }
        if (home.Tool == AgentTool.RooCode) RequireCompletedStandaloneRoo(root);
        var members = new List<AgentSessionMember>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? current))
        {
            token.ThrowIfCancellationRequested();
            Add(current, true);
            foreach (string child in Directory.EnumerateFileSystemEntries(current))
            {
                token.ThrowIfCancellationRequested();
                string name = System.IO.Path.GetFileName(child);
                if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pid", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A session lock or process marker is present. Finish the task and stop its agents before cleanup.");
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // Validate before following a directory, including cloud and
                    // provider paths. No transcript or checkpoint data is read.
                    AgentFileTarget.Capture(child, directory: true);
                    if (members.Count + pending.Count >= 1000) throw new IOException("This session exceeds the 1,000-entry safety limit. Review it in its owning tool.");
                    pending.Push(child);
                }
                else Add(child, false);
            }
        }
        return new(root, members.OrderBy(member => member.Path, StringComparer.Ordinal).ToArray());

        void Add(string entry, bool directory)
        {
            if (members.Count >= 1000) throw new IOException("This session exceeds the 1,000-entry safety limit. Review it in its owning tool.");
            var identity = AgentFileTarget.Capture(entry, directory);
            if (identity.LastWriteFileTime <= 0) throw new IOException("A related session path has no usable activity timestamp. The session is kept.");
            members.Add(new(entry, directory, identity));
        }
    }

    internal void Validate(AgentHistoryItem item, CancellationToken token)
    {
        var current = Capture(item.Home, item.Path, token);
        if (current is null || current.Root != Root || !Members.SequenceEqual(current.Members))
            throw new IOException("The session or a related file changed after inspection. Inspect again; no cleanup started for this file.");
    }

    // A batch can contain both transcript files from one task. Only mutations
    // already confirmed by this batch may change the expected membership.
    internal AgentSessionEvidence AfterOwnRemovals(AgentHistoryItem item, IReadOnlySet<string> removed, CancellationToken token)
    {
        if (!Members.Any(member => removed.Contains(member.Path))) return this;
        var current = Capture(item.Home, item.Path, token) ?? throw new IOException("Session evidence is unavailable.");
        var expected = Members.Where(member => !removed.Contains(member.Path)).ToArray();
        if (expected.Length != current.Members.Count) throw new IOException("Related session files changed during cleanup. Inspect again.");
        for (int i = 0; i < expected.Length; i++)
        {
            var actual = current.Members[i]; var original = expected[i];
            if (original.Directory && original.Path == Root)
                original = original with { Identity = original.Identity with { LastWriteFileTime = actual.Identity.LastWriteFileTime } };
            if (original != actual) throw new IOException("Related session files changed during cleanup. Inspect again.");
        }
        return current with { AcceptedActivity = LatestWriteFileTime };
    }

    private static void RequireCompletedStandaloneRoo(string root)
    {
        string path = System.IO.Path.Join(root, "history_item.json");
        var before = AgentFileTarget.Capture(path);
        if (before.Length > 65536) throw new IOException("Roo task status is too large to verify. Manage this task in Roo Code.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[checked((int)before.Length)]; file.ReadExactly(bytes);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var metadata = json.RootElement;
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.String || id.GetString() != System.IO.Path.GetFileName(root)
            || !metadata.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "completed")
            throw new IOException("Only completed Roo tasks with verified status can be reviewed. Active or unknown tasks are kept.");
        foreach (string key in new[] { "parentTaskId", "rootTaskId", "delegatedToId", "awaitingChildId", "completedByChildId" })
            if (metadata.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null
                && (value.ValueKind != JsonValueKind.String || value.GetString() is { Length: > 0 }))
                throw new IOException("Roo task chains and delegated work are kept. Manage the related tasks together in Roo Code.");
        if (metadata.TryGetProperty("childIds", out var children)
            && (children.ValueKind != JsonValueKind.Array || children.GetArrayLength() != 0))
            throw new IOException("Roo tasks with children are kept. Manage the task chain in Roo Code.");
        if (AgentFileTarget.Capture(path) != before) throw new IOException("Roo task status changed during inspection. Inspect again.");
    }
}

internal static class AgentOwnerProtection
{
    internal static string[] OwnerNames(AgentTool tool) => tool switch
        {
            AgentTool.Cursor => ["Cursor", "cursor-agent", "agent"],
            AgentTool.Copilot => ["copilot", "GitHub Copilot"],
            AgentTool.Cline => ["Code", "Code - Insiders", "Code-Insiders", "Cursor", "VSCodium", "Windsurf", "cline", "cline-core", "cline-hub"],
            AgentTool.RooCode => ["Code", "Code - Insiders", "Code-Insiders", "Cursor", "VSCodium", "Windsurf"],
            AgentTool.OpenCode => ["opencode", "OpenCode"],
            _ => []
        };
    internal static string? RunningOwner(AgentTool tool)
    {
        string[] names = OwnerNames(tool);
        if (names.Length == 0) return null;
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                try
                {
                    if (names.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                        return process.ProcessName;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        return null;
    }
    internal static void RequireStopped(AgentTool tool, Func<AgentTool, string?>? runningOwner = null)
    {
        if ((runningOwner ?? RunningOwner)(tool) is { } owner)
            throw new IOException($"{owner} is running. Close the tool, its editor hosts and background agents before cleanup.");
    }
}
