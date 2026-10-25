using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text.Json;

namespace SpaceMaid.Extensions.Containers;

internal sealed record DockerStorageItem(string Kind, string Id, string Name, string State, long? Bytes,
    string Details, string? Command = null)
{
    public bool CanCopy => Command is not null;
    public string SizeText => Bytes is { } bytes ? FormatSize(bytes) : "Unknown";
    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}

internal static class DockerUsage
{
    internal const string LocalDocker = "docker --host npipe:////./pipe/docker_engine";
    internal const string CacheCleanup = LocalDocker + " builder prune --filter until=168h";
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;

    internal static async Task<IReadOnlyList<DockerStorageItem>> InspectAsync(CancellationToken token)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (_, cancellation) =>
            {
                var pipe = new NamedPipeClientStream(".", "docker_engine", PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(cancellation).ConfigureAwait(false); return pipe; }
                catch { pipe.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return await InspectAsync(client, token).ConfigureAwait(false);
    }

    // One fixed GET against the local Windows pipe. Never follows Docker contexts,
    // DOCKER_HOST, redirects or user-supplied URLs; no mutation API is implemented.
    internal static async Task<IReadOnlyList<DockerStorageItem>> InspectAsync(HttpClient client, CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        token = deadline.Token; // ResponseHeadersRead requires our deadline to cover body reads too.
        using var response = await client.GetAsync("http://localhost/v1.47/system/df", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Docker's report exceeds the 8 MiB inspection limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); byte[] chunk = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException("Docker's report exceeds the 8 MiB inspection limit.");
            buffer.Write(chunk, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return Parse(buffer.ToArray(), token);
    }

    internal static IReadOnlyList<DockerStorageItem> Parse(ReadOnlyMemory<byte> json, CancellationToken token = default)
    {
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 48 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Docker returned an unrecognized usage report.");
        var rows = new List<DockerStorageItem>();
        foreach (string kind in new[] { "Containers", "Images", "BuildCache", "Volumes" })
        {
            token.ThrowIfCancellationRequested();
            if (!root.TryGetProperty(kind, out var entries)) throw new InvalidDataException("Docker's usage report is incomplete.");
            if (entries.ValueKind == JsonValueKind.Null) continue;
            if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Docker returned an invalid usage list.");
            foreach (var entry in entries.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (rows.Count >= 5000) throw new InvalidDataException("Docker's report exceeds the 5,000-object inspection limit.");
                if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Docker returned an invalid storage object.");
                string id = Text(entry, kind == "BuildCache" ? "ID" : kind == "Volumes" ? "Name" : "Id");
                if (kind == "Containers")
                {
                    string state = Text(entry, "State"); bool stopped = state is "exited" or "created";
                    string names = Names(entry, "Names").TrimStart('/');
                    bool dev = entry.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object
                        && (labels.TryGetProperty("devcontainer.local_folder", out _) || labels.TryGetProperty("vsch.local.folder", out _));
                    rows.Add(new("Containers", id, names.Length > 0 ? names : id, state.Length > 0 ? state : "Unknown", Number(entry, "SizeRw"),
                        (dev ? "Dev container metadata. " : "") + "Writable-layer size excludes mounted volumes, bind mounts and logs. Stopped does not mean abandoned. Before removal, copy out agent work, untracked files and artifacts. The copied command permanently removes this container's writable layer; it keeps volumes and refuses running containers. Reinspect before running.",
                        stopped && Hex(id) ? LocalDocker + " container rm " + id : null));
                }
                else if (kind == "Images")
                {
                    long? references = Number(entry, "Containers");
                    string tags = Names(entry, "RepoTags");
                    rows.Add(new("Images", id, tags.Length > 0 ? tags : id, references == 0 ? "Unused by containers" : references is { } n ? $"{n} container references" : "Usage unknown",
                        Number(entry, "Size"), "Image size includes shared layers; rows must not be added together. Unused means no stopped or running container references at inspection time, not that the image is disposable. Preserve offline, local-only and agent-base images. The copied command removes this image without force; Docker rechecks references and may refuse multiple tags.",
                        references == 0 && id.StartsWith("sha256:", StringComparison.Ordinal) && Hex(id[7..]) ? LocalDocker + " image rm " + id : null));
                }
                else if (kind == "BuildCache")
                {
                    bool? inUse = Boolean(entry, "InUse");
                    rows.Add(new("Build cache", id, Text(entry, "Description") is { Length: > 0 } description ? description : id,
                        inUse == true ? "In use" : inUse == false ? "Not in use" : "Usage unknown", Number(entry, "Size"),
                        "Cache sizes can share storage with images and other records. Cleanup slows later builds and may need downloads. The copied command is builder-wide: it asks Docker to prune dangling default-engine cache older than seven days, not just this row. It retains Docker's confirmation prompt. Separate Buildx builders are outside this report.",
                        inUse == false ? CacheCleanup : null));
                }
                else
                {
                    var usage = entry.TryGetProperty("UsageData", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
                    long? references = usage.ValueKind == JsonValueKind.Object ? Number(usage, "RefCount") : null;
                    rows.Add(new("Volumes", id, id, references == 0 ? "Unattached · kept" : references is { } n ? $"{n} references · kept" : "Kept",
                        usage.ValueKind == JsonValueKind.Object ? Number(usage, "Size") : null,
                        "Volumes can hold databases, models, credentials and agent work. Unattached volumes can still be valuable. Space Maid provides no volume deletion or prune command. Back up and manage them in Docker Desktop."));
                }
            }
        }
        return rows.AsReadOnly();
    }

    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static long? Number(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number >= 0 ? number : null;
    private static bool? Boolean(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static string Names(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
        ? string.Join(", ", value.EnumerateArray().Where(element => element.ValueKind == JsonValueKind.String).Select(element => element.GetString()).Take(8)) : "";
    private static bool Hex(string id) => id.Length == 64 && id.AsSpan().IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}
