using System.IO;
using System.Text.RegularExpressions;

namespace SpaceMaid.Extensions.Agents;

internal static partial class AgentHistoryCleanup
{
    private static IEnumerable<AgentHome> AdditionalHomes(string profile)
    {
        yield return new(AgentTool.Cursor, Path.Join(profile, ".cursor"));
        yield return new(AgentTool.Copilot, Path.Join(profile, ".copilot"));
        yield return new(AgentTool.Cline, Path.Join(profile, ".cline"));
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (string host in new[] { "Code", "Code - Insiders", "Cursor", "VSCodium", "Windsurf" })
        {
            string storage = Path.Join(roaming, host, "User", "globalStorage");
            yield return new(AgentTool.Cline, Path.Join(storage, "saoudrizwan.claude-dev"));
            yield return new(AgentTool.RooCode, Path.Join(storage, "rooveterinaryinc.roo-cline"));
        }
        yield return new(AgentTool.OpenCode, Path.Join(profile, ".local", "share", "opencode"));
    }

    private static string[] AdditionalRoots(AgentTool tool) => tool switch
    {
        AgentTool.Cursor => ["chats"], AgentTool.Copilot => ["session-state", "logs"],
        AgentTool.Cline => ["tasks", "data"], AgentTool.RooCode => ["tasks"], AgentTool.OpenCode => ["log"], _ => []
    };

    private static bool AdditionalDescend(AgentHome home, string[] parts, int depth) => home.Tool switch
    {
        AgentTool.Cursor => depth == 1 && parts[0] == "chats" && parts[^1].Length == 32 && parts[^1].All(char.IsAsciiHexDigit),
        AgentTool.Copilot => depth == 1 && parts[0] == "session-state",
        AgentTool.Cline => depth == 1 && parts[0] == "tasks"
            || parts[0] == "data" && (depth == 1 && parts[^1] is "sessions" or "logs" || depth == 2 && parts[1] == "sessions"),
        AgentTool.RooCode => depth == 1 && parts[0] == "tasks",
        _ => false
    };

    private static string? AdditionalKnownFile(AgentHome home, string[] p)
    {
        string name = p[^1];
        return home.Tool switch
        {
            AgentTool.Copilot when p.Length == 3 && p[0] == "session-state" && name == "events.jsonl" => "Session transcript",
            AgentTool.Copilot when p.Length == 2 && p[0] == "logs" && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) => "Diagnostic log",
            AgentTool.Cline or AgentTool.RooCode when p.Length == 3 && p[0] == "tasks"
                && name is "api_conversation_history.json" or "ui_messages.json" => "Task transcript",
            AgentTool.RooCode when p.Length == 3 && p[0] == "tasks" && WriteLeftover().IsMatch(name) => "Task write leftover",
            AgentTool.Cline when p.Length == 4 && p[0] == "data" && p[1] == "sessions"
                && name == p[2] + ".messages.json" => "Session transcript",
            AgentTool.Cline when p.Length == 3 && p[0] == "data" && p[1] == "logs" && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) => "Diagnostic log",
            AgentTool.OpenCode when p.Length == 2 && p[0] == "log" && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) => "Diagnostic log",
            _ => null
        };
    }

    internal static string? LeftoverCanonical(string name)
    {
        var match = WriteLeftover().Match(name);
        return match.Success ? match.Groups[1].Value : null;
    }
    [GeneratedRegex(@"^\.(api_conversation_history\.json|ui_messages\.json)\.(?:new|bak)_[0-9]{10,17}_[A-Za-z0-9]{1,32}\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex WriteLeftover();
}
