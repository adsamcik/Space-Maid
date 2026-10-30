using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using SpaceMaid.Core.IO;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Opportunities;

internal sealed record LibraryModel(string Name, long? Bytes, string Detail)
{
    public string SizeText => Bytes is { } bytes ? OpportunitySizeText.Format(bytes) : "Unavailable";
}
internal sealed record ModelLibrary(IReadOnlyList<LibraryModel> Models, string Note);

// Read only the selected store's small named manifests, never model blobs.
// Names and sizes follow Ollama's host/namespace/model/tag and config + layers format:
// https://github.com/ollama/ollama/blob/main/manifest/manifest.go
internal static class OllamaLibrary
{
    internal const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumEntries = 10000, MaximumModels = 2000;
    private const FileAttributes Unavailable = FileAttributes.ReparsePoint | FileAttributes.Offline
        | (FileAttributes)0x00400000 | (FileAttributes)0x00040000;

    internal static ModelLibrary Read(DiskAccess files, string library, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(Path.Join(library, "manifests"));
        // Do not traverse redirected or cloud-only ancestors to inspect content.
        for (string? path = root; path is not null; path = Path.GetDirectoryName(path))
            RequireLocal(files.GetAttributes(path));
        var models = new List<LibraryModel>();
        int visited = 0, skipped = 0, unknown = 0;
        long bytesRead = 0;
        bool limited = false;
        Visit(root, 0);
        string note = limited ? "Inspection limit reached; some models are not listed."
            : skipped > 0 ? "Some entries could not be read. Refresh after checking folder access."
            : unknown > 0 ? "Some model sizes are unavailable. Refresh after downloads finish."
            : models.Count == 0 ? "No named models found in this library. Downloads may still be in progress." : "";
        return new(models.OrderByDescending(model => model.Bytes).ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray(), note);

        void Visit(string directory, int depth)
        {
            try
            {
                foreach (string path in files.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > MaximumEntries || models.Count >= MaximumModels || bytesRead >= 16L * MaximumManifestBytes)
                    { limited = true; return; }
                    var attributes = files.GetAttributes(path);
                    if ((attributes & Unavailable) != 0) { skipped++; continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (depth < 3) Visit(path, depth + 1);
                        else skipped++;
                        if (limited) return;
                    }
                    else if (depth == 3)
                    {
                        string name = ModelName(Path.GetRelativePath(root, path));
                        try
                        {
                            byte[] data = ReadManifest(files, path, token);
                            bytesRead += data.Length;
                            models.Add(new(name, ManifestSize(data), path));
                        }
                        catch (Exception error) when (Expected(error))
                        { unknown++; models.Add(new(name, null, "Model details unavailable. " + error.Message)); }
                    }
                    else skipped++;
                }
            }
            catch (Exception error) when (Expected(error)) { skipped++; }
        }
    }

    private static byte[] ReadManifest(DiskAccess files, string path, CancellationToken token)
    {
        // Open the final entry itself, without recall or write/delete sharing.
        using var handle = files.OpenHandle(path, 0x80000000, 1, 0, 3, 0x00300000, 0);
        if (handle.IsInvalid) throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        byte[] basic = new byte[40];
        if (!files.ReadHandleInformation(handle, 0, basic, basic.Length))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(basic.AsSpan(32));
        RequireLocal(attributes);
        if ((attributes & FileAttributes.Directory) != 0) throw new IOException("The model manifest is no longer a file.");
        byte[] standard = new byte[24];
        if (!files.ReadHandleInformation(handle, 1, standard, standard.Length))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        long length = BinaryPrimitives.ReadInt64LittleEndian(standard.AsSpan(8));
        if (length < 0 || length > MaximumManifestBytes) throw new InvalidDataException("Manifest exceeds the 1 MiB inspection limit.");
        byte[] data = new byte[(int)length];
        int offset = 0;
        while (offset < data.Length)
        {
            token.ThrowIfCancellationRequested();
            int read = files.Read(handle, data.AsSpan(offset), offset);
            if (read == 0) throw new EndOfStreamException("The model manifest changed during inspection.");
            offset += read;
        }
        token.ThrowIfCancellationRequested();
        return data;
    }

    private static void RequireLocal(FileAttributes attributes)
    {
        if ((attributes & Unavailable) != 0) throw new IOException("Linked or cloud-only model metadata was not read.");
    }
    private static string ModelName(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar);
        string prefix = parts[0].Equals("registry.ollama.ai", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("ollama.com", StringComparison.OrdinalIgnoreCase)
            ? parts[1].Equals("library", StringComparison.OrdinalIgnoreCase) ? "" : parts[1] + "/"
            : parts[0] + "/" + parts[1] + "/";
        return prefix + parts[2] + ":" + parts[3];
    }
    internal static long ManifestSize(ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data, new() { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 2
            || !root.TryGetProperty("config", out var config) || !root.TryGetProperty("layers", out var layers)
            || layers.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            throw new InvalidDataException("Unrecognized model manifest.");
        long total = LayerSize(config);
        if (layers.ValueKind == JsonValueKind.Array)
            foreach (var layer in layers.EnumerateArray()) total = checked(total + LayerSize(layer));
        return total;
    }
    private static long LayerSize(JsonElement layer)
    {
        if (layer.ValueKind != JsonValueKind.Object || !layer.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes < 0
            || !layer.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String
            || digest.GetString() is not { Length: 71 } text || !text.StartsWith("sha256:", StringComparison.Ordinal)
            || text.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
            throw new InvalidDataException("Incomplete model size metadata.");
        return bytes;
    }
    internal static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or Win32Exception or OverflowException or System.Security.SecurityException;
}
