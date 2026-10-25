using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SpaceMaid.Extensions.Models;

internal sealed record OllamaModel(string Name, string Digest, long Bytes, bool Loaded)
{
    public string State => Loaded ? "Loaded · kept" : "Not loaded";
    public bool CanDelete => !Loaded;
    public string SizeText
    {
        get
        {
            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            double value = Bytes; int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return $"{value:0.##} {units[unit]}";
        }
    }
}
internal sealed record OllamaSnapshot(IReadOnlyList<OllamaModel> Models, DateTimeOffset ObservedAt);
internal sealed record OllamaCleanupPlan(OllamaModel Model, DateTimeOffset PreparedAt);
internal sealed record OllamaCleanupResult(bool Deleted, bool Attempted, string Message, OllamaSnapshot? Snapshot);

internal static class OllamaCleanup
{
    internal const string LocalEndpoint = "http://127.0.0.1:11434";
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    internal const int MaximumModels = 2000;
    private static HttpClient CreateClient() => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    internal static async Task<OllamaSnapshot> InspectAsync(CancellationToken token)
    {
        using var client = CreateClient();
        return await InspectAsync(client, token).ConfigureAwait(false);
    }
    internal static async Task<OllamaSnapshot> InspectAsync(HttpClient client, CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = Deadline(token, timeout);
        return await ReadSnapshotAsync(client, deadline.Token).ConfigureAwait(false);
    }
    internal static async Task<OllamaCleanupPlan> PrepareAsync(OllamaModel selected, CancellationToken token)
    {
        using var client = CreateClient();
        return await PrepareAsync(client, selected, token).ConfigureAwait(false);
    }
    internal static async Task<OllamaCleanupPlan> PrepareAsync(HttpClient client, OllamaModel selected, CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = Deadline(token, timeout);
        var current = await ReadSnapshotAsync(client, deadline.Token).ConfigureAwait(false);
        return new(RequireSameUnloadedModel(current, selected), DateTimeOffset.UtcNow);
    }
    internal static async Task<OllamaCleanupResult> ExecuteAsync(OllamaCleanupPlan plan, CancellationToken token)
    {
        using var client = CreateClient();
        return await ExecuteAsync(client, plan, token).ConfigureAwait(false);
    }
    internal static async Task<OllamaCleanupResult> ExecuteAsync(HttpClient client, OllamaCleanupPlan plan, CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = Deadline(token, timeout);
        bool attempted = false;
        try
        {
            var fresh = await ReadSnapshotAsync(client, deadline.Token).ConfigureAwait(false);
            var model = RequireSameUnloadedModel(fresh, plan.Model);
            deadline.Token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Delete, LocalEndpoint + "/api/delete")
            { Content = new StringContent(JsonSerializer.Serialize(new { model = model.Name }), Encoding.UTF8, "application/json") };
            attempted = true;
            await SendAsync(client, request, deadline.Token).ConfigureAwait(false);
            var after = await ReadSnapshotAsync(client, deadline.Token).ConfigureAwait(false);
            bool absent = !after.Models.Any(item => item.Name == model.Name);
            return new(absent, true, absent
                ? "Model removed by Ollama. Shared data still needed by other models may remain."
                : "Ollama accepted deletion, but a model with this name is present after refresh. Review the current list before taking another action.", after);
        }
        catch (Exception error) when (Expected(error))
        {
            return new(false, attempted, attempted
                ? "The deletion request may have reached Ollama. Its result could not be verified; inspect again before retrying. " + error.Message
                : "No deletion started. " + error.Message, null);
        }
    }

    internal static OllamaModel RequireSameUnloadedModel(OllamaSnapshot current, OllamaModel expected)
    {
        var model = current.Models.SingleOrDefault(item => item.Name == expected.Name);
        if (model is null) throw new InvalidOperationException("The selected model is no longer present. Inspect again.");
        if (model.Digest != expected.Digest || model.Bytes != expected.Bytes)
            throw new InvalidOperationException("The selected model changed since it was shown. Inspect and review it again.");
        if (model.Loaded) throw new InvalidOperationException("This model, or an alias sharing its data, is loaded. Finish the sessions using it before cleanup.");
        return model;
    }

    private static async Task<OllamaSnapshot> ReadSnapshotAsync(HttpClient client, CancellationToken token)
    {
        using var tagsRequest = new HttpRequestMessage(HttpMethod.Get, LocalEndpoint + "/api/tags");
        byte[] tags = await SendAsync(client, tagsRequest, token).ConfigureAwait(false);
        using var loadedRequest = new HttpRequestMessage(HttpMethod.Get, LocalEndpoint + "/api/ps");
        byte[] loaded = await SendAsync(client, loadedRequest, token).ConfigureAwait(false);
        return Parse(tags, loaded, token);
    }
    internal static OllamaSnapshot Parse(ReadOnlyMemory<byte> tags, ReadOnlyMemory<byte> loaded, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var tagDocument = JsonDocument.Parse(tags, new() { MaxDepth = 32 });
        using var loadedDocument = JsonDocument.Parse(loaded, new() { MaxDepth = 32 });
        var running = Entries(loadedDocument.RootElement);
        var loadedNames = new HashSet<string>(StringComparer.Ordinal);
        var loadedDigests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in running.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            loadedNames.Add(Name(entry)); loadedDigests.Add(Digest(entry));
        }
        var models = new List<OllamaModel>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries(tagDocument.RootElement).EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            string name = Name(entry), digest = Digest(entry);
            if (!names.Add(name)) throw new InvalidDataException("Ollama returned duplicate model names. Inspect again.");
            if (!entry.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes < 0)
                throw new InvalidDataException("Ollama did not report a valid model size. Cleanup is unavailable.");
            models.Add(new(name, digest, bytes, loadedNames.Contains(name) || loadedDigests.Contains(digest)));
        }
        return new(models.AsReadOnly(), DateTimeOffset.UtcNow);
    }
    private static JsonElement Entries(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Ollama returned an incomplete model or loaded-state report. Cleanup is unavailable.");
        if (entries.GetArrayLength() > MaximumModels) throw new InvalidDataException("Ollama's report exceeds the 2,000-model inspection limit.");
        return entries;
    }
    private static string Name(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Ollama returned invalid model data.");
        string name = Text(entry, "name"), model = Text(entry, "model");
        if (name.Length == 0) name = model;
        if (name.Length == 0 || name.Length > 1024 || name.Any(char.IsControl) || model.Length > 0 && model != name)
            throw new InvalidDataException("Ollama returned an ambiguous model name. Cleanup is unavailable.");
        return name;
    }
    private static string Digest(JsonElement entry)
    {
        string digest = Text(entry, "digest");
        if (digest.StartsWith("sha256:", StringComparison.Ordinal)) digest = digest[7..];
        if (digest.Length != 64 || digest.AsSpan().IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
            throw new InvalidDataException("Ollama did not report a complete model digest. Cleanup is unavailable.");
        return digest.ToLowerInvariant();
    }
    private static string Text(JsonElement entry, string key) => entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static CancellationTokenSource Deadline(CancellationToken token, TimeSpan? timeout)
    { var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(20)); return deadline; }
    private static async Task<byte[]> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Ollama's response exceeds the 4 MiB inspection limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); byte[] chunk = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException("Ollama's response exceeds the 4 MiB inspection limit.");
            buffer.Write(chunk, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }
    internal static bool Expected(Exception error) => error is IOException or InvalidDataException or HttpRequestException or JsonException
        or OperationCanceledException or InvalidOperationException or UnauthorizedAccessException;
}
