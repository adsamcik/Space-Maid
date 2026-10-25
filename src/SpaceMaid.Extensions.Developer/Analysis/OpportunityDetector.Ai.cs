using SpaceMaid.Core.Analysis;

namespace SpaceMaid.Extensions.Developer.Analysis;

public static partial class OpportunityDetector
{
    // Evidence comes only from the borrowed inventory. Never open transcripts,
    // databases, credentials or model contents, or inspect the process environment.
    private static Rule? AiStorage(IOpportunityInventory scan, int directory, int parent,
        ReadOnlySpan<char> name, Markers direct, MarkerEvidence evidence)
    {
        if (ParentNamed(scan, parent, "projects") && ParentNamed(scan, Parent(scan, parent), ".claude")
            && evidence.Has(directory, Markers.JsonLines))
            return Agent("Claude Code project history", "A project folder under .claude/projects containing JSONL transcripts.",
                "Contains conversations, tool results and possibly auto memory. Removing this folder loses session resumption and project memory.",
                "Open Extensions → Agent cleanup to review individual old transcripts. Project memory, checkpoints and the project folder stay in place.")
                with { BlockReason = "Use Agent cleanup for individual old transcripts; the project history folder can also contain memory." };
        if (Is(name, "file-history") && ParentNamed(scan, parent, ".claude"))
            return Checkpoints("Claude Code file checkpoints", "The .claude/file-history snapshot directory.", "Claude Code");
        if (Is(name, "session-state") && ParentNamed(scan, parent, ".copilot"))
            return Agent("GitHub Copilot session history", "The .copilot/session-state directory used by Copilot CLI and the Copilot app.",
                "Contains resumable sessions and task artifacts. Deleting folders directly can leave the session index out of sync.",
                "Open Extensions → Agent cleanup to review old local transcript files. App indexes, plans, artifacts and synced copies stay in place.")
                with { BlockReason = "Use Agent cleanup for old local files; the whole session-state folder is kept." };
        if (Is(name, "chats") && ParentNamed(scan, Parent(scan, parent), "tmp")
            && ParentNamed(scan, Parent(scan, Parent(scan, parent)), ".gemini") && evidence.Has(directory, Markers.GeminiSession))
            return Agent("Gemini CLI session history", "A .gemini/tmp/<project>/chats folder containing session JSON files.",
                "Despite the tmp path, these are resumable conversations and tool results, not disposable temporary files.",
                "Open Extensions → Agent cleanup to review individual old transcripts. Related plans, artifacts and application indexes are kept.")
                with { BlockReason = "Use Agent cleanup for individual old transcripts; the whole chat folder is kept." };

        // Global storage also holds extension state and credentials. Show its
        // size, but never offer folder-wide recycling for the shared store.
        if (ParentNamed(scan, parent, "workspaceStorage") && CursorUser(scan, Parent(scan, parent))
            && evidence.Has(directory, Markers.StateDatabase))
            return Agent("Cursor workspace history", "A Cursor/User/workspaceStorage workspace containing state.vscdb.",
                "Includes chat navigation and personal workspace state. Deleting the database can lose access to conversations.",
                "Manage editor chats in Cursor. Agent cleanup supports individual Cursor CLI chat folders; the editor's shared database stays protected.")
                with { BlockReason = "This editor database also holds workspace state. Use Cursor's chat controls or Agent cleanup for separate CLI chats." };
        if (Is(name, "globalStorage") && CursorUser(scan, parent) && evidence.Has(directory, Markers.StateDatabase))
            return Agent("Cursor global chat storage", "Cursor/User/globalStorage contains state.vscdb alongside extension state.",
                "Space Maid keeps this folder. It includes chat data, extension settings and potentially credentials. Deleting it can reset the editor and lose conversations.",
                "Review and remove chats using Cursor's own controls; preserve settings and history. Space Maid keeps this folder.")
                with { BlockReason = "Manage chats in Cursor; this folder also holds extension state and may contain credentials." };

        int extension = ParentNamed(scan, parent, "tasks") ? Parent(scan, parent) : -1;
        string? tool = ParentNamed(scan, extension, "saoudrizwan.claude-dev") ? "Cline"
            : ParentNamed(scan, extension, "rooveterinaryinc.roo-cline") ? "Roo Code" : null;
        if (tool is not null && ParentNamed(scan, Parent(scan, extension), "globalStorage")
            && evidence.Has(directory, Markers.AgentMessages))
            return Agent(tool + " task history", "An extension task folder containing conversation or UI-message metadata.",
                "Task conversations, attachments and checkpoints may be the only recovery copy of work. They cannot be regenerated.",
                "Open Extensions → Agent cleanup to review old transcript files after stopping the editor and background agents. Task metadata and checkpoints stay in place.")
                with { BlockReason = "Use Agent cleanup for individual old files; task folders and their recovery data are kept." };
        if (Is(name, "checkpoints") && ParentNamed(scan, parent, "saoudrizwan.claude-dev")
            && ParentNamed(scan, Parent(scan, parent), "globalStorage"))
            return Checkpoints("Cline file checkpoints", "Cline's extension checkpoint directory under globalStorage.", "Cline");

        if (Is(name, "log") && OpenCodeData(scan, parent))
            return Agent("OpenCode logs", "The .local/share/opencode/log diagnostic directory.",
                "Logs may contain useful diagnostics or private context. Stop active OpenCode sessions and preserve evidence you need.",
                "Open Extensions → Agent cleanup for old logs, or its OpenCode sessions tab for local server-managed session removal.")
                with { BlockReason = "Use Agent cleanup for individual old logs; the full log folder stays in place." };
        if (OpenCodeData(scan, directory) && evidence.Has(directory, Markers.OpenCodeDatabase))
            return Agent("OpenCode session storage", "The .local/share/opencode data directory containing opencode.db.",
                "Space Maid keeps this folder. It contains sessions, messages and authentication data; removing it can lose history and sign-in state.",
                "Open Extensions → Agent cleanup → OpenCode sessions to review removal through the local server. Space Maid keeps the database, credentials and shared storage folder.")
                with { BlockReason = "Use Agent cleanup's OpenCode sessions tab; this folder also contains credentials and shared application data." };
        if (Is(name, "opencode") && ParentNamed(scan, parent, ".cache"))
            return new("OpenCode download cache", "Tool caches", "The .cache/opencode tool cache, separate from session data and authentication.",
                "Close OpenCode and review custom files first. Cached plugins or providers may require downloads again.",
                "OpenCode rebuilds its cache on restart; preserve downloads needed offline.", true);

        bool hubRepository = RepositoryName(name, "models--") || RepositoryName(name, "datasets--") || RepositoryName(name, "spaces--");
        if (hubRepository && evidence.Has(directory, Markers.Snapshots) && evidence.Any(directory, Markers.Refs | Markers.Blobs))
            return new("Hugging Face repository downloads", "AI models & data", "A Hub repository cache with snapshots and refs or blobs; also recognizes relocated cache roots.",
                "This selects the whole repository: all revisions, snapshots and its blobs. Keep offline or unavailable models and datasets. Close tools using this cache first. Revisions can share blobs; Windows without symlinks can duplicate snapshots.",
                "Review this complete repository folder and move it to the Recycle Bin here. Individual revisions and blobs are kept together; restore the whole folder if needed.", true);
        if ((direct & (Markers.Blobs | Markers.Manifests)) == (Markers.Blobs | Markers.Manifests)
            && evidence.HasNested(directory, "blobs", Markers.OllamaBlob))
            return new("Ollama model library", "AI models & data", "A model store with manifests and sha256-named blobs, including relocated OLLAMA_MODELS stores.",
                "Space Maid keeps this model library. Models can share blobs and be needed offline; partial downloads may belong to active pulls. Do not remove individual blob files.",
                "Open Extensions → AI models to inspect local Ollama models and review deletion of an individual model. The shared library folder stays protected.", true,
                "Use the AI models workflow to remove individual Ollama models; the shared model library folder is kept.");
        if (ParentNamed(scan, Parent(scan, parent), "models")
            && ParentNamed(scan, Parent(scan, Parent(scan, parent)), ".lmstudio") && evidence.Has(directory, Markers.ModelWeights))
            return new("LM Studio model downloads", "AI models & data", "A .lmstudio/models/<publisher>/<repository> folder containing GGUF weights.",
                "This selects all model variants and companion files in this repository. Close LM Studio and other model users first. Imported models may have no available download; preserve offline copies.",
                "Review this complete model repository and move it to the Recycle Bin here. Split weights and vision projectors remain together; restore the whole folder if needed.", true);
        return null;
    }

    private static Rule Agent(string title, string reason, string caution, string action)
        => new(title, "Agent files", reason, caution, action, true);
    private static Rule Checkpoints(string title, string reason, string tool)
        => new(title, "Agent checkpoints", reason,
            "File snapshots can be the only recovery copy of edited or untracked work. Removing them loses rollback history; age does not prove they are disposable.",
            "Review checkpoints in " + tool + " and preserve work before removing recovery files. Stop active sessions first.", true);
    private static bool CursorUser(IOpportunityInventory scan, int directory)
        => ParentNamed(scan, directory, "User") && ParentNamed(scan, Parent(scan, directory), "Cursor");
    private static bool OpenCodeData(IOpportunityInventory scan, int directory)
        => ParentNamed(scan, directory, "opencode") && ParentNamed(scan, Parent(scan, directory), "share")
            && ParentNamed(scan, Parent(scan, Parent(scan, directory)), ".local");
    private static bool RepositoryName(ReadOnlySpan<char> name, string prefix)
        => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length;
}
