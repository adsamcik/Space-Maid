using System.IO;
using System.Text.Json;

namespace SpaceMaid.App.AgentAccess;

// Small, bounded MCP stdio subset. No resource, prompt, shell or content tools.
internal sealed class AgentProtocol(Func<string, JsonElement, Task<object>> call, AgentAccessLevel level)
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private bool _initialized, _ready;
    private static object Schema(object properties, string[] required) => new
        { type = "object", properties, required, additionalProperties = false };

    internal static object Tools(AgentAccessLevel access)
    {
        var tools = new List<object>();
        void Add(string name, string description, object schema, bool readOnly = true) => tools.Add(new
        {
            name, description, inputSchema = schema,
            annotations = new { readOnlyHint = readOnly, destructiveHint = false, openWorldHint = false }
        });
        Add("get_summary", "Get authorized scope totals and coverage. No names, paths or contents. Sizes are observations, not promised recovery.", Schema(new { }, []));
        if (access >= AgentAccessLevel.Groups)
            Add("get_groups", "Get coarse tool/storage/policy groups with session-only IDs. Groups overlap: never add their sizes. No per-file or per-folder browsing.", Schema(new { }, []));
        if (access == AgentAccessLevel.SuggestReview)
            Add("suggest_review", "Queue one suggestion for human review in Space Maid. Does not select, authorize or execute cleanup. Protected/incomplete groups are refused.",
                Schema(new
                {
                    groupIds = new { type = "array", items = new { type = "string", format = "uuid" }, minItems = 1, maxItems = 8, uniqueItems = true },
                    note = new { type = "string", minLength = 1, maxLength = 500 }
                }, ["groupIds", "note"]), false);
        return new { tools };
    }

    internal async Task<string?> HandleAsync(string line)
    {
        JsonElement? id = null;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            NoDuplicateFields(root);
            Fields(root, ["jsonrpc", "id", "method", "params"], ["jsonrpc", "method"]);
            if (root.TryGetProperty("id", out var requestId))
            {
                if (requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) || requestId.GetRawText().Length > 66)
                    throw new InvalidDataException();
                id = requestId.Clone();
            }
            if (Text(root, "jsonrpc") != "2.0") throw new InvalidDataException();
            string method = Text(root, "method");
            var parameters = root.TryGetProperty("params", out var p) ? p : JsonSerializer.SerializeToElement(new { });
            if (parameters.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            // Notifications never receive a response, including invalid ones.
            if (id is null)
            {
                if (method == "notifications/initialized" && _initialized) _ready = true;
                return null;
            }
            object result;
            switch (method)
            {
                case "initialize" when !_initialized:
                    Fields(parameters, ["protocolVersion", "capabilities", "clientInfo", "_meta"], ["protocolVersion", "capabilities", "clientInfo"]);
                    _ = Text(parameters, "protocolVersion");
                    if (parameters.GetProperty("capabilities").ValueKind != JsonValueKind.Object
                        || parameters.GetProperty("clientInfo").ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                    _initialized = true;
                    result = new
                    {
                        protocolVersion = "2025-11-25", capabilities = new { tools = new { } },
                        serverInfo = new { name = "space-maid-aggregates", version = "1.0.0" },
                        instructions = "Evaluate aggregates only. There are no content, path, search, execution or elevation tools. Human review remains in Space Maid. Reconnect with a new user-created session after scan changes."
                    };
                    break;
                case "ping": result = new { }; break;
                case "tools/list" when _ready:
                    Fields(parameters, ["_meta"], []); result = Tools(level); break;
                case "tools/call" when _ready:
                    Fields(parameters, ["name", "arguments", "_meta"], ["name"]);
                    string name = Text(parameters, "name");
                    var args = parameters.TryGetProperty("arguments", out var a) ? a : JsonSerializer.SerializeToElement(new { });
                    if (args.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                    try
                    {
                        var output = await call(name, args);
                        result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(output, Json) } }, structuredContent = output, isError = false };
                    }
                    catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException or OperationCanceledException)
                    {
                        // Never return exception messages: even host validation
                        // errors can contain paths, labels or tool diagnostics.
                        result = new { content = new[] { new { type = "text", text = "Request refused. Check the access level, issued IDs and current session in Space Maid." } }, isError = true };
                    }
                    break;
                default: return Error(id, -32601, "Method unavailable or session not initialized.");
            }
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, Json);
        }
        catch (JsonException) { return Error(id, -32700, "Invalid JSON."); }
        catch (InvalidDataException) { return Error(id, -32602, "Invalid request fields."); }
    }

    internal static object Execute(AgentAggregateSession session, string name, JsonElement arguments)
    {
        session.EnsureCurrent();
        switch (name)
        {
            case "get_summary": Fields(arguments, [], []); return session.Overview;
            case "get_groups" when session.Level >= AgentAccessLevel.Groups:
                Fields(arguments, [], []); return new { groups = session.Groups };
            case "suggest_review" when session.Level == AgentAccessLevel.SuggestReview:
                Fields(arguments, ["groupIds", "note"], ["groupIds", "note"]);
                var ids = arguments.GetProperty("groupIds");
                if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 8) throw new InvalidDataException();
                var groups = new List<Guid>();
                foreach (var item in ids.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "D", out var id)) throw new InvalidDataException();
                    groups.Add(id);
                }
                var suggestion = session.Suggest(groups, Text(arguments, "note"));
                return new { suggestionId = suggestion.Id, status = "awaiting_human_review", cleanupExecuted = false };
            default: throw new InvalidDataException();
        }
    }

    private static string Error(JsonElement? id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }, Json);
    internal static void Fields(JsonElement value, string[] allowed, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) throw new InvalidDataException();
        if (required.Any(name => !seen.Contains(name))) throw new InvalidDataException();
    }
    private static string Text(JsonElement value, string name) => value.GetProperty(name) is { ValueKind: JsonValueKind.String } text
        ? text.GetString()! : throw new InvalidDataException();
    private static void NoDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            { if (!seen.Add(field.Name)) throw new InvalidDataException(); NoDuplicateFields(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) NoDuplicateFields(item);
    }
}
