using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpaceMaid.Extensions.Agents;

internal sealed class OpenCodeConnection(string password)
{
    internal string Password { get; } = password;
    public override string ToString() => OpenCodeSessions.LocalEndpoint;
}
internal sealed record OpenCodeSession(string Id, string Title, string Directory, string ProjectId,
    long Created, long Updated, string? ParentId, string Status, bool OldEnough)
{
    public DateTime LastChanged => DateTimeOffset.FromUnixTimeMilliseconds(Updated).LocalDateTime;
    public bool Shared { get; init; }
    public bool ManagedWorkspace { get; init; }
    public bool CanDelete => OldEnough && Status == "idle" && !Shared && !ManagedWorkspace && ParentId is null;
    public string State => Shared ? "Shared · kept" : ManagedWorkspace ? "Workspace · kept" : ParentId is not null ? "Child session · kept"
        : !OldEnough ? "Recent · kept" : Status == "idle" ? "Idle · review children" : "Active · kept";
}
internal sealed record OpenCodeServer(string Version, string StateDirectory, string Directory);
internal sealed record OpenCodeSessionSnapshot(OpenCodeServer Server, IReadOnlyList<OpenCodeSession> Sessions);
internal sealed record OpenCodeSessionPlan(OpenCodeServer Server, OpenCodeSession Session, int OlderThanDays)
{
    public string Endpoint => OpenCodeSessions.LocalEndpoint;
}
internal sealed record OpenCodeSessionResult(bool Deleted, bool Attempted, string Message);

// Stable /session API only. Newer /api/session protocols are deliberately not
// guessed. The server owns its database, cascading records and related data.
internal static class OpenCodeSessions
{
    internal const string LocalEndpoint = "http://127.0.0.1:4096";
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    internal const int MaximumSessions = 2000;
    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    internal static async Task<OpenCodeSessionSnapshot> InspectAsync(OpenCodeConnection connection, int age, CancellationToken token)
    { using var client = Client(); return await InspectAsync(client, connection, age, token).ConfigureAwait(false); }
    internal static async Task<OpenCodeSessionSnapshot> InspectAsync(HttpClient client, OpenCodeConnection connection, int age,
        CancellationToken token, TimeSpan? timeout = null)
    {
        ValidateAge(age); using var deadline = Deadline(token, timeout);
        var server = await ReadServer(client, connection, deadline.Token).ConfigureAwait(false);
        var entries = await Get(client, connection, "/session?limit=" + (MaximumSessions + 1), server.Directory, deadline.Token).ConfigureAwait(false);
        var statuses = await ReadStatus(client, connection, server.Directory, deadline.Token).ConfigureAwait(false);
        var sessions = ParseSessions(entries, statuses, age, DateTimeOffset.UtcNow);
        return new(server, sessions);
    }
    internal static async Task<OpenCodeSessionPlan> PrepareAsync(OpenCodeConnection connection, OpenCodeSessionSnapshot snapshot,
        OpenCodeSession selected, int age, CancellationToken token)
    { using var client = Client(); return await PrepareAsync(client, connection, snapshot, selected, age, token).ConfigureAwait(false); }
    internal static async Task<OpenCodeSessionPlan> PrepareAsync(HttpClient client, OpenCodeConnection connection,
        OpenCodeSessionSnapshot snapshot, OpenCodeSession selected, int age, CancellationToken token, TimeSpan? timeout = null)
    {
        ValidateAge(age); using var deadline = Deadline(token, timeout);
        var server = await ReadServer(client, connection, deadline.Token).ConfigureAwait(false);
        SameServer(server, snapshot.Server);
        var current = await RequireIdleLeaf(client, connection, selected, age, deadline.Token).ConfigureAwait(false);
        return new(server, current, age);
    }
    internal static async Task<OpenCodeSessionResult> ExecuteAsync(OpenCodeConnection connection, OpenCodeSessionPlan plan, CancellationToken token)
    { using var client = Client(); return await ExecuteAsync(client, connection, plan, token).ConfigureAwait(false); }
    internal static async Task<OpenCodeSessionResult> ExecuteAsync(HttpClient client, OpenCodeConnection connection,
        OpenCodeSessionPlan plan, CancellationToken token, TimeSpan? timeout = null)
    {
        bool attempted = false; using var deadline = Deadline(token, timeout);
        try
        {
            ValidateAge(plan.OlderThanDays);
            SameServer(await ReadServer(client, connection, deadline.Token).ConfigureAwait(false), plan.Server);
            var current = await RequireIdleLeaf(client, connection, plan.Session, plan.OlderThanDays, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            using var request = Request(connection, HttpMethod.Delete, "/session/" + current.Id, current.Directory);
            attempted = true;
            byte[] response = await Send(client, request, deadline.Token).ConfigureAwait(false);
            using (var json = Document(response))
                if (json.RootElement.ValueKind != JsonValueKind.True) throw new InvalidDataException("OpenCode did not confirm session deletion.");
            using var verify = Request(connection, HttpMethod.Get, "/session/" + current.Id, current.Directory);
            using var absent = await client.SendAsync(verify, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (absent.StatusCode != HttpStatusCode.NotFound)
                return new(false, true, "OpenCode accepted the request, but absence was not confirmed. Inspect again before taking another action.");
            SameServer(await ReadServer(client, connection, deadline.Token).ConfigureAwait(false), plan.Server);
            return new(true, true, "Session removed by OpenCode. Its database file may retain free pages; host disk recovery is unknown.");
        }
        catch (Exception error) when (Expected(error))
        {
            return new(false, attempted, attempted
                ? "The deletion request may have reached OpenCode. Its result could not be verified; inspect again before retrying. " + SafeError(error)
                : "No deletion started. " + SafeError(error));
        }
    }

    private static async Task<OpenCodeSession> RequireIdleLeaf(HttpClient client, OpenCodeConnection connection,
        OpenCodeSession expected, int age, CancellationToken token)
    {
        RequireId(expected.Id); RequireDirectory(expected.Directory);
        using (var project = Document(await Get(client, connection, "/project/current", expected.Directory, token).ConfigureAwait(false)))
            if (Text(project.RootElement, "id", 256) != expected.ProjectId)
                throw new InvalidOperationException("The session's local project identity changed. Inspect again before cleanup.");
        var before = await ReadStatus(client, connection, expected.Directory, token).ConfigureAwait(false);
        if (before.GetValueOrDefault(expected.Id, "idle") != "idle") throw new InvalidOperationException("This session is active or retrying. Finish its work before cleanup.");
        byte[] details = await Get(client, connection, "/session/" + expected.Id, expected.Directory, token).ConfigureAwait(false);
        using var detail = Document(details);
        var current = ParseSession(detail.RootElement, "idle", age, DateTimeOffset.UtcNow);
        if (current.Shared) throw new InvalidOperationException("Shared sessions are kept because OpenCode may remove their remote share during deletion.");
        if (current.ManagedWorkspace) throw new InvalidOperationException("Workspace-managed sessions are kept; review them in OpenCode.");
        if (current.ParentId is not null) throw new InvalidOperationException("Child sessions are kept with their parent to preserve active work and recovery context.");
        if (current.Id != expected.Id || current.Title != expected.Title || current.Directory != expected.Directory
            || current.ProjectId != expected.ProjectId || current.Created != expected.Created || current.Updated != expected.Updated
            || current.ParentId != expected.ParentId)
            throw new InvalidOperationException("The selected session changed after inspection. Inspect and review it again.");
        if (!current.OldEnough) throw new InvalidOperationException("Recent session activity is kept. Inspect again with the intended age filter.");
        byte[] children = await Get(client, connection, "/session/" + current.Id + "/children", current.Directory, token).ConfigureAwait(false);
        using (var json = Document(children))
        {
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("OpenCode did not report a valid child-session list.");
            if (json.RootElement.GetArrayLength() != 0)
                throw new InvalidOperationException("This session has child sessions. Keep it; only sessions without children can be removed here.");
        }
        var after = await ReadStatus(client, connection, current.Directory, token).ConfigureAwait(false);
        if (after.GetValueOrDefault(current.Id, "idle") != "idle") throw new InvalidOperationException("This session became active during inspection. Finish its work before cleanup.");
        return current;
    }
    private static async Task<OpenCodeServer> ReadServer(HttpClient client, OpenCodeConnection connection, CancellationToken token)
    {
        using var health = Document(await Get(client, connection, "/global/health", null, token).ConfigureAwait(false));
        var root = health.RootElement;
        RequireObject(root);
        if (!root.TryGetProperty("healthy", out var healthy) || healthy.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("The local OpenCode server is not healthy.");
        string version = Text(root, "version", 128);
        if (!Version.TryParse(version.Split('-')[0], out var parsed) || parsed.Major != 1)
            throw new InvalidDataException("This cleanup supports OpenCode's version 1 /session API. The connected version is unsupported.");
        using var path = Document(await Get(client, connection, "/path", null, token).ConfigureAwait(false));
        RequireObject(path.RootElement);
        string state = Text(path.RootElement, "state", 32768), directory = Text(path.RootElement, "directory", 32768);
        RequireDirectory(state); RequireDirectory(directory);
        return new(version, state, directory);
    }
    private static void SameServer(OpenCodeServer current, OpenCodeServer expected)
    {
        if (current != expected) throw new InvalidOperationException("The connected OpenCode version or storage/project scope changed. Inspect again.");
    }
    private static async Task<Dictionary<string, string>> ReadStatus(HttpClient client, OpenCodeConnection connection, string directory, CancellationToken token)
    {
        byte[] body = await Get(client, connection, "/session/status", directory, token).ConfigureAwait(false);
        return ParseStatus(body);
    }
    internal static Dictionary<string, string> ParseStatus(ReadOnlyMemory<byte> body)
    {
        using var json = Document(body); var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OpenCode did not report valid active-session state.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root.EnumerateObject())
        {
            if (result.Count >= MaximumSessions) throw new InvalidDataException("The active-session report exceeds its inspection limit.");
            RequireId(entry.Name); RequireObject(entry.Value); string type = Text(entry.Value, "type", 16);
            if (type is not ("idle" or "busy" or "retry") || !result.TryAdd(entry.Name, type))
                throw new InvalidDataException("OpenCode returned an unknown or duplicate active-session state.");
        }
        // Version 1 SessionStatus removes idle entries from this map; absence
        // means idle, not an incomplete status report (v1.2.27 source).
        return result;
    }
    internal static IReadOnlyList<OpenCodeSession> ParseSessions(ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> statuses, int age, DateTimeOffset now)
    {
        ValidateAge(age); using var json = Document(body); var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaximumSessions)
            throw new InvalidDataException("OpenCode returned an invalid or oversized session list.");
        var ids = new HashSet<string>(StringComparer.Ordinal); var result = new List<OpenCodeSession>();
        foreach (var entry in root.EnumerateArray())
        {
            string id = Text(entry, "id", 256); RequireId(id);
            if (!ids.Add(id)) throw new InvalidDataException("OpenCode returned duplicate session IDs.");
            result.Add(ParseSession(entry, statuses.GetValueOrDefault(id, "idle"), age, now));
        }
        return result.OrderBy(session => session.Updated).ThenBy(session => session.Id, StringComparer.Ordinal).ToArray();
    }
    private static OpenCodeSession ParseSession(JsonElement root, string status, int age, DateTimeOffset now)
    {
        RequireObject(root);
        string id = Text(root, "id", 256); RequireId(id);
        string title = Text(root, "title", 4096), directory = Text(root, "directory", 32768), project = Text(root, "projectID", 256);
        RequireDirectory(directory);
        string? parent = null;
        if (root.TryGetProperty("parentID", out var value) && value.ValueKind != JsonValueKind.Null)
        { parent = Text(root, "parentID", 256); RequireId(parent); }
        if (!root.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Object
            || !time.TryGetProperty("created", out var created) || created.ValueKind != JsonValueKind.Number || !created.TryGetInt64(out long c)
            || !time.TryGetProperty("updated", out var updated) || updated.ValueKind != JsonValueKind.Number || !updated.TryGetInt64(out long u)
            || c <= 0 || u < c || u > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            throw new InvalidDataException("OpenCode did not report valid session dates.");
        RequireObject(time);
        bool shared = root.TryGetProperty("share", out var share) && share.ValueKind != JsonValueKind.Null;
        bool workspace = root.TryGetProperty("workspaceID", out var workspaceId) && workspaceId.ValueKind != JsonValueKind.Null;
        return new(id, title, directory, project, c, u, parent, status, u <= now.AddDays(-age).ToUnixTimeMilliseconds()) { Shared = shared, ManagedWorkspace = workspace };
    }
    private static string Text(JsonElement entry, string key, int limit)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("OpenCode returned incomplete session or server metadata.");
        string result = value.GetString()!;
        if (result.Length == 0 || result.Length > limit || result.Any(char.IsControl)) throw new InvalidDataException("OpenCode returned invalid text metadata.");
        return result;
    }
    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OpenCode returned an invalid metadata object.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!fields.Add(field.Name)) throw new InvalidDataException("OpenCode returned ambiguous duplicate metadata fields.");
    }
    private static void RequireId(string id)
    {
        if (id.Length is < 5 or > 256 || !id.StartsWith("ses_", StringComparison.Ordinal)
            || id.AsSpan(4).IndexOfAnyExcept("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ") >= 0)
            throw new InvalidDataException("OpenCode returned an invalid session ID.");
    }
    private static void RequireDirectory(string path)
    {
        // The local server may run in WSL. Paths are scope identifiers passed
        // back to OpenCode, never used for host filesystem writes.
        if (!(path.StartsWith('/') || Path.IsPathFullyQualified(path)) || path.Any(char.IsControl) || path.Contains('%'))
            throw new InvalidDataException("OpenCode returned an unresolved storage or project path.");
    }
    private static void ValidateAge(int age)
    { if (age is < 1 or > 3650) throw new InvalidOperationException("Enter an age from 1 through 3650 days."); }
    private static JsonDocument Document(ReadOnlyMemory<byte> body) => JsonDocument.Parse(body, new() { MaxDepth = 32 });
    private static CancellationTokenSource Deadline(CancellationToken token, TimeSpan? timeout)
    { var result = CancellationTokenSource.CreateLinkedTokenSource(token); result.CancelAfter(timeout ?? TimeSpan.FromSeconds(20)); return result; }
    private static async Task<byte[]> Get(HttpClient client, OpenCodeConnection connection, string path, string? directory, CancellationToken token)
    { using var request = Request(connection, HttpMethod.Get, path, directory); return await Send(client, request, token).ConfigureAwait(false); }
    private static HttpRequestMessage Request(OpenCodeConnection connection, HttpMethod method, string path, string? directory)
    {
        var request = new HttpRequestMessage(method, LocalEndpoint + path + (directory is null ? "" : (path.Contains('?') ? "&" : "?") + "directory=" + Uri.EscapeDataString(directory)));
        if (connection.Password.Length > 4096) throw new InvalidOperationException("The local server password is too long.");
        if (connection.Password.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + connection.Password)));
        return request;
    }
    private static async Task<byte[]> Send(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("OpenCode's response exceeds the 4 MiB inspection limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream(); byte[] chunk = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > MaximumResponseBytes) throw new InvalidDataException("OpenCode's response exceeds the 4 MiB inspection limit.");
            result.Write(chunk, 0, count);
        }
        token.ThrowIfCancellationRequested(); return result.ToArray();
    }
    internal static bool Expected(Exception error) => error is IOException or InvalidDataException or HttpRequestException
        or JsonException or OperationCanceledException or InvalidOperationException or ArgumentException;
    internal static string SafeError(Exception error) => error is HttpRequestException request && request.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        ? "The local server rejected authentication. Check its password and default opencode username."
        : error is JsonException ? "OpenCode returned invalid JSON; cleanup is unavailable."
        : error is OperationCanceledException ? "The operation was cancelled or timed out." : error.Message;
}
