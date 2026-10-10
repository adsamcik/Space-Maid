using System.Buffers.Binary;

namespace SpaceMaid.Core.Scanning;

public enum ScanFrameKind : ushort { Hello = 1, Request = 2, Batch = 3, Complete = 4, Failure = 5, Cancel = 6 }

// A bounded, versioned stream protocol. Payloads are binary metadata, never
// serialized paths/objects. No commands beyond one scan and cancellation.
public static class ScanPipeProtocol
{
    public const int MaximumPayload = 64 * 1024 * 1024 + 8;
    public const ushort Version = 1;
    private const uint Magic = 0x534D4149;

    public static void Write(Stream stream, ScanFrameKind kind, ReadOnlySpan<byte> payload)
    {
        WriteHeader(stream, kind, payload.Length);
        stream.Write(payload); stream.Flush();
    }

    public static void WriteBlock(Stream stream, NtfsLayoutBlock block)
    {
        WriteHeader(stream, ScanFrameKind.Batch, checked(8 + block.Data.Length));
        Span<byte> timing = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(timing, block.QueryMilliseconds);
        stream.Write(timing); stream.Write(block.Data.Span); stream.Flush();
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
