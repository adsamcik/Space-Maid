using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace SpaceMaid.Extensions.Containers;

internal sealed record DockerCleanupPlan(string EngineId, DockerStorageItem Target);
internal sealed record DockerCleanupResult(bool ConfirmedSuccess, string Message, long? ReportedReclaimedBytes = null);

internal static class DockerCleanup
{
    internal static async Task<DockerCleanupPlan> PrepareAsync(DockerStorageItem selected, CancellationToken token)
    { using var client = DockerUsage.CreateClient(); return await PrepareAsync(client, selected, token); }

    internal static async Task<DockerCleanupResult> ExecuteAsync(DockerCleanupPlan plan, CancellationToken token)
    { using var client = DockerUsage.CreateClient(); return await ExecuteAsync(client, plan, token); }

    internal static async Task<DockerCleanupPlan> PrepareAsync(HttpClient client, DockerStorageItem selected, CancellationToken token)
    {
        string engine = await EngineIdAsync(client, token);
        var current = await FindAsync(client, selected.Kind, selected.Id, token);
        if (!current.CanClean) throw new InvalidOperationException("Docker no longer considers this object eligible for cleanup. Inspect it again.");
        return new(engine, current);
    }

    internal static async Task<DockerCleanupResult> ExecuteAsync(HttpClient client, DockerCleanupPlan plan, CancellationToken token)
    {
        // No mutation is sent before both the engine and captured object have
        // been revalidated. IDs/filters are constructed here, never shell strings.
        if (await EngineIdAsync(client, token) != plan.EngineId) throw new InvalidOperationException("The local Docker engine changed. Review cleanup again.");
        var current = await FindAsync(client, plan.Target.Kind, plan.Target.Id, token);
        if (!current.CanClean || current.Version != plan.Target.Version || current.Name != plan.Target.Name)
            throw new InvalidOperationException("This Docker object changed after the preview. Review it again before cleanup.");
        string escaped = Uri.EscapeDataString(current.Id);
        var (method, path) = current.Kind switch
        {
            "Containers" => (HttpMethod.Delete, $"containers/{escaped}?force=false&v=false&link=false"),
            "Images" => (HttpMethod.Delete, $"images/{escaped}?force=false&noprune=true"),
            "Volumes" => (HttpMethod.Delete, $"volumes/{escaped}?force=false"),
            "Build cache" when DockerUsage.SafeCacheId(current.Id) => (HttpMethod.Post,
                "build/prune?all=false&filters=" + Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string[]>
                { ["id"] = ["^" + System.Text.RegularExpressions.Regex.Escape(current.Id) + "$"] }))),
            _ => throw new InvalidOperationException("Unsupported Docker cleanup target.")
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        token.ThrowIfCancellationRequested();
        try
        {
            using var request = new HttpRequestMessage(method, "http://localhost/v1.47/" + path);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            byte[] body = await DockerUsage.ReadBodyAsync(response, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return new(false, "Docker did not confirm cleanup. " + Error(body, response.StatusCode.ToString()) + " Inspect again before retrying; the request may have changed data.");
            if (current.Kind == "Build cache")
            {
                using var report = JsonDocument.Parse(body);
                if (!report.RootElement.TryGetProperty("CachesDeleted", out var deleted) || deleted.ValueKind != JsonValueKind.Array
                    || !deleted.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == current.Id))
                    return new(false, "Docker did not confirm removal of the selected cache record. It may be shared or no longer removable. Inspect again.");
                if (deleted.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || value.GetString() != current.Id))
                    return new(false, "Docker reported additional cache removals. Inspect the engine before any further cleanup.");
                long? bytes = report.RootElement.TryGetProperty("SpaceReclaimed", out var reclaimed) && reclaimed.ValueKind == JsonValueKind.Number && reclaimed.TryGetInt64(out long size) && size >= 0 ? size : null;
                return new(true, "Docker confirmed removal of the selected cache record. Host virtual-disk allocation may remain unchanged.", bytes);
            }
            if (current.Kind == "Images")
            {
                using var report = JsonDocument.Parse(body);
                if (report.RootElement.ValueKind != JsonValueKind.Array || !report.RootElement.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty("Deleted", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == current.Id))
                    return new(false, "Docker did not confirm deletion of this image ID. Inspect again; tags or layers may have changed.");
            }
            else if ((int)response.StatusCode != 204)
                return new(false, "Docker returned an unexpected response. Inspect again; cleanup outcome is uncertain.");
            return new(true, "Docker confirmed removal. This is permanent; host virtual-disk allocation may remain unchanged.");
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException)
        { return new(false, "Cleanup outcome is uncertain: " + error.Message + " Docker may still finish the request. Inspect again before retrying."); }
    }

    private static async Task<DockerStorageItem> FindAsync(HttpClient client, string kind, string id, CancellationToken token)
        => (await DockerUsage.InspectAsync(client, token)).SingleOrDefault(value => value.Kind == kind && value.Id == id)
            ?? throw new InvalidOperationException("The selected Docker object is no longer present. Inspect again.");
    private static async Task<string> EngineIdAsync(HttpClient client, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync("http://localhost/v1.47/info", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var report = JsonDocument.Parse(await DockerUsage.ReadBodyAsync(response, deadline.Token));
        return report.RootElement.TryGetProperty("ID", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 256 } value
            ? value : throw new InvalidDataException("Docker did not provide a stable engine ID. Cleanup is unavailable.");
    }
    private static string Error(byte[] body, string fallback)
    {
        try { using var json = JsonDocument.Parse(body); return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString()! : fallback; }
        catch (JsonException) { return fallback; }
    }
}
