using System.Buffers.Binary;
using System.Text.Json;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.Core.IO;

public sealed record RecyclePermissionRequest(FolderDeletionIdentity Folder, RecyclePermissionTarget Item)
{
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > 65536) throw new InvalidDataException("Permission repair message is too large.");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is < 1 or > 65536) throw new InvalidDataException("Invalid permission repair message.");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Empty permission repair message.");
    }
}

public sealed record RecyclePermissionReply(bool Applied, string? Error);
