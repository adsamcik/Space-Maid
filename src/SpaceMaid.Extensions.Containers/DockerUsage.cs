using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text.Json;

namespace SpaceMaid.Extensions.Containers;

internal sealed record DockerStorageItem(string Kind, string Id, string Name, string State, long? Bytes,
    string Details, bool CanClean = false, string Version = "")
{
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
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;

    internal static async Task<IReadOnlyList<DockerStorageItem>> InspectAsync(CancellationToken token)
    {
        using var client = CreateClient();
        return await InspectAsync(client, token).ConfigureAwait(false);
    }
    internal static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (_, cancellation) =>
            {
                var pipe = new NamedPipeClientStream(".", "docker_engine", PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(cancellation).ConfigureAwait(false); return pipe; }
                catch { pipe.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // One fixed GET against the local Windows pipe. Never follows Docker contexts,
    // DOCKER_HOST, redirects or user-supplied URLs.
    internal static async Task<IReadOnlyList<DockerStorageItem>> InspectAsync(HttpClient client, CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        token = deadline.Token; // ResponseHeadersRead requires our deadline to cover body reads too.
        using var response = await client.GetAsync("http://localhost/v1.47/system/df", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Parse(await ReadBodyAsync(response, token).ConfigureAwait(false), token);
    }
    internal static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Docker's response exceeds the 8 MiB limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); byte[] chunk = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException("Docker's response exceeds the 8 MiB limit.");
            buffer.Write(chunk, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return buffer.ToArray();
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
                        (dev ? "Dev container metadata. " : "") + "Removing this stopped container permanently deletes its writable layer, including untracked agent work. Mounted volumes and bind-mounted files are kept. Size excludes mounts and some logs. Preserve needed work before review.",
                        stopped && Hex(id), Version(entry, "ImageID", "Created", "State", "SizeRw", "Names", "Mounts")));
                }
                else if (kind == "Images")
                {
                    long? references = Number(entry, "Containers");
                    string tags = Names(entry, "RepoTags");
                    rows.Add(new("Images", id, tags.Length > 0 ? tags : id, references == 0 ? "Unused by containers" : references is { } n ? $"{n} container references" : "Usage unknown",
                        Number(entry, "Size"), "Image size includes shared layers. Removing an unused image may require another download or a local rebuild. Preserve offline and local-only images. Cleanup keeps unselected parent images and uses no force; Docker rechecks references and may refuse multiple tags.",
                        references == 0 && id.StartsWith("sha256:", StringComparison.Ordinal) && Hex(id[7..]), Version(entry, "Containers", "Size", "RepoTags", "RepoDigests")));
                }
                else if (kind == "BuildCache")
                {
                    bool? inUse = Boolean(entry, "InUse");
                    rows.Add(new("Build cache", id, Text(entry, "Description") is { Length: > 0 } description ? description : id,
                        inUse == true ? "In use" : inUse == false ? "Not in use" : "Usage unknown", Number(entry, "Size"),
                        "Cleanup targets this exact cache record, not the whole builder. Cache sizes can share storage with images and other records; later builds may take longer or need downloads. In-use records are kept. Separate Buildx builders are outside this report.",
                        inUse == false && SafeCacheId(id), Version(entry, "InUse", "Shared", "Size", "Parents", "LastUsedAt", "CreatedAt")));
                }
                else
                {
                    var usage = entry.TryGetProperty("UsageData", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
                    long? references = usage.ValueKind == JsonValueKind.Object ? Number(usage, "RefCount") : null;
                    bool local = Text(entry, "Driver") == "local" && Text(entry, "CreatedAt").Length > 0
                        && (!entry.TryGetProperty("Options", out var options) || options.ValueKind == JsonValueKind.Null || options.ValueKind == JsonValueKind.Object && !options.EnumerateObject().Any());
                    rows.Add(new("Volumes", id, id, references == 0 ? "Unattached" : references is { } n ? $"{n} references · kept" : "Usage unknown",
                        usage.ValueKind == JsonValueKind.Object ? Number(usage, "Size") : null,
                        "Permanently deleting this volume removes its databases, models, credentials and agent work. Unattached does not mean disposable. Back it up first. Only unattached default local volumes can be removed here; external drivers and mounted-storage options are kept.",
                        local && references == 0 && SafeVolumeName(id), Version(entry, "CreatedAt", "Driver", "Mountpoint", "Labels", "Options", "UsageData")));
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
    internal static bool SafeCacheId(string id) => id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    internal static bool SafeVolumeName(string id) => id.Length is > 0 and <= 255 && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static string Version(JsonElement entry, params string[] keys)
        => string.Join("\n", keys.Select(key => entry.TryGetProperty(key, out var value) ? value.GetRawText() : "<missing>"));
}
