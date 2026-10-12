using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpaceMaid.LocalModel;

// Shared source, compiled into the host adapter and the isolated worker. Only
// text crosses these pipes; neither side serializes executable tools or handles.
internal static class ModelProtocol
{
    internal const int Version = 1;
    internal const int MaximumRequestBytes = 128 * 1024;
    internal const int MaximumResponseBytes = 64 * 1024;
    internal const int MaximumInputCharacters = 48 * 1024;
    internal const int MaximumOutputCharacters = 24 * 1024;
    internal const int MaximumMessages = 40;
    internal const int MaximumOutputTokens = 1024;

    // Stateful inference may reuse its context only for the exact conversation
    // it generated. Edited, omitted or unrelated history must start afresh.
    internal static bool IsExactContinuation(IReadOnlyList<ModelMessage>? previous, string? generated,
        IReadOnlyList<ModelMessage> current)
    {
        if (previous is null || generated is null || current.Count != previous.Count + 2) return false;
        for (int index = 0; index < previous.Count; index++)
            if (current[index] != previous[index]) return false;
        return current[^2] == new ModelMessage("assistant", generated) && current[^1].Role == "user";
    }

    internal static bool TryGetTemplateSuffix(string? previousPrompt, string? generated, string? terminalMarker,
        string fullPrompt, out string suffix)
    {
        suffix = "";
        if (previousPrompt is null || generated is null || string.IsNullOrEmpty(terminalMarker)) return false;
        string prefix = previousPrompt + generated + terminalMarker;
        if (!fullPrompt.StartsWith(prefix, StringComparison.Ordinal)) return false;
        suffix = fullPrompt[prefix.Length..];
        // InteractiveExecutor otherwise appends a newline of its own. Reuse
        // only when it will preserve the embedded template byte for byte.
        return suffix.Length > 0 && suffix.EndsWith('\n');
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static T Deserialize<T>(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicateFields(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes.Span, Options)
            ?? throw new InvalidDataException("The model returned an empty protocol frame.");
    }

    internal static byte[] Serialize<T>(T value, int maximumBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("The model protocol frame exceeds its limit.");
        return bytes;
    }

    internal static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static void RejectDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate model protocol field.");
                RejectDuplicateFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item);
    }
}

internal sealed class ModelFrameReader(Stream stream, int maximumBytes)
{
    private readonly byte[] _buffer = new byte[4096];
    private int _position;
    private int _count;

    internal async Task<byte[]?> ReadAsync(CancellationToken token)
    {
        ArrayBufferWriter<byte> result = new();
        while (true)
        {
            if (_position == _count)
            {
                _count = await stream.ReadAsync(_buffer, token).ConfigureAwait(false);
                _position = 0;
                if (_count == 0)
                {
                    if (result.WrittenCount == 0) return null;
                    throw new InvalidDataException("The model closed an unfinished protocol frame.");
                }
            }

            var remaining = _buffer.AsSpan(_position, _count - _position);
            int newline = remaining.IndexOf((byte)'\n');
            int length = newline >= 0 ? newline : remaining.Length;
            if (result.WrittenCount + length > maximumBytes)
                throw new InvalidDataException("The model protocol frame exceeds its limit.");
            remaining[..length].CopyTo(result.GetSpan(length));
            result.Advance(length);
            _position += length;
            if (newline >= 0)
            {
                _position++;
                if (result.WrittenCount == 0) throw new InvalidDataException("The model returned an empty protocol frame.");
                return result.WrittenSpan.ToArray();
            }
        }
    }
}

internal sealed record ModelMessage(string Role, string Text);
internal sealed record ModelRequest(int Version, long Id, string Kind, IReadOnlyList<ModelMessage>? Messages = null,
    int MaxOutputTokens = 768, float Temperature = 0.1f, SandboxProbeRequest? Probe = null);
internal sealed record ModelReply(int Version, long Id, string? Text = null, string? Error = null,
    SandboxProbeResult? Probe = null);
internal sealed record SandboxProbeRequest(string ModelPath, string RuntimeDirectory, string OutsideReadPath,
    string OutsideWritePath, int LoopbackPort, string? PrivateRegistryKey = null);
internal sealed record SandboxProbeResult(bool AppContainer, bool LowPrivilege, bool ModelReadAllowed,
    bool ModelWriteBlocked, bool RuntimeWriteBlocked, bool OutsideReadBlocked, bool OutsideWriteBlocked,
    bool LoopbackBlocked, bool ChildProcessBlocked, bool EnvironmentSecretAbsent, string? NetworkObservation = null,
    bool? PrivateRegistryReadBlocked = null);
