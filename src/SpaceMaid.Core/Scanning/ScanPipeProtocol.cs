using System.Buffers.Binary;
using System.Text;

namespace SpaceMaid.Core.Scanning;

public enum ScanFrameKind : ushort { Hello = 1, Request = 2, Batch = 3, Complete = 4, Failure = 5, Cancel = 6, Cancelled = 7, FilesRequest = 8, JournalRequest = 9 }

// A bounded, versioned stream protocol. Payloads are binary metadata, never
// serialized paths/objects. Only serial read-only scans and cancellation.
// Request IDs keep a late cancellation from affecting a subsequent scan.
public static class ScanPipeProtocol
{
    public const int MaximumPayload = 64 * 1024 * 1024 + 16;
    public const int MaximumFileIds = 1024;
    public const int MaximumFilesRequestPayload = 15 + 8 * MaximumFileIds;
    public const ushort Version = 4;
    private const uint Magic = 0x534D4149;

    public static byte[] CreateJournalRequest(long request, string volume, NtfsJournalCheckpoint? since)
    {
        if (volume.Length != 3 || !char.IsAsciiLetter(volume[0]) || volume[1] != ':' || volume[2] != '\\')
            throw new InvalidDataException("Only a local NTFS drive root is accepted.");
        byte[] payload = new byte[since is null ? 11 : 35];
        BinaryPrimitives.WriteInt64LittleEndian(payload, request);
        Encoding.ASCII.GetBytes(volume).CopyTo(payload, 8);
        if (since is { } checkpoint) NtfsChangeJournal.WriteCheckpoint(payload.AsSpan(11), checkpoint);
        return payload;
    }

    internal static NtfsJournalCheckpoint? ReadJournalRequest(ReadOnlySpan<byte> payload) => payload.Length switch
    {
        11 => null,
        35 => NtfsChangeJournal.ReadCheckpoint(payload[11..]),
        _ => throw new InvalidDataException("Invalid NTFS journal request length.")
    };

    public static byte[] CreateFilesRequest(long request, string volume, IReadOnlyList<FileIdentity> files)
    {
        NativeNtfsLayoutSource.ValidateFiles(files);
        if (volume.Length != 3 || !char.IsAsciiLetter(volume[0]) || volume[1] != ':' || volume[2] != '\\')
            throw new InvalidDataException("Only a local NTFS drive root is accepted.");
        byte[] payload = new byte[15 + files.Count * 8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, request);
        Encoding.ASCII.GetBytes(volume).CopyTo(payload, 8);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(11), files.Count);
        for (int i = 0; i < files.Count; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(15 + i * 8), files[i].Low);
        return payload;
    }

    internal static FileIdentity[] ReadFilesRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 23 or > MaximumFilesRequestPayload)
            throw new InvalidDataException("Invalid scoped NTFS request length.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload[11..]);
        if (count is < 1 or > MaximumFileIds || payload.Length != 15 + count * 8)
            throw new InvalidDataException("Invalid scoped NTFS identity count.");
        var files = new FileIdentity[count];
        for (int i = 0; i < count; i++) files[i] = new(BinaryPrimitives.ReadUInt64LittleEndian(payload[(15 + i * 8)..]));
        NativeNtfsLayoutSource.ValidateFiles(files);
        return files;
    }

    public static void Write(Stream stream, ScanFrameKind kind, ReadOnlySpan<byte> payload)
    {
        WriteHeader(stream, kind, payload.Length);
        stream.Write(payload); stream.Flush();
    }

    public static void WriteBlock(Stream stream, long request, NtfsLayoutBlock block)
    {
        WriteHeader(stream, ScanFrameKind.Batch, checked(16 + block.Data.Length));
        Span<byte> prefix = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(prefix, request);
        BinaryPrimitives.WriteDoubleLittleEndian(prefix[8..], block.QueryMilliseconds);
        stream.Write(prefix); stream.Write(block.Data.Span); stream.Flush();
    }

    private static void WriteHeader(Stream stream, ScanFrameKind kind, int length)
    {
        if (length > MaximumPayload) throw new InvalidDataException("Scan frame is too large.");
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)kind);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], length);
        stream.Write(header);
    }

    public static (ScanFrameKind Kind, int Length) ReadHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[12]; stream.ReadExactly(header);
        ushort kind = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Version
            || !Enum.IsDefined((ScanFrameKind)kind) || length < 0 || length > MaximumPayload)
            throw new InvalidDataException("Invalid or incompatible scan frame.");
        return ((ScanFrameKind)kind, length);
    }
}
