using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SpaceMaid.Core.Scanning;

// Offsets follow the Windows SDK winioctl.h. Records are relative-offset chains,
// not arrays of marshalled structs; every range is checked before it is read.
internal static class LayoutParser
{
    public static void Parse(ReadOnlySpan<byte> data, ScanBuilder builder)
    {
        Require(data, 0, 16);
        uint count = U32(data, 0);
        int file = Offset(data, 0, U32(data, 4), 16, allowZero: count == 0);
        if (count > (uint)(data.Length / 40)) throw Bad();
        for (uint i = 0; i < count; i++)
        {
            Require(data, file, 40);
            if (U32(data, file) != 1) throw new InvalidDataException("Unsupported file layout record version.");
            uint nextFile = U32(data, file + 4);
            int fileEnd = nextFile == 0 ? data.Length : Offset(data, file, nextFile, 40);
            var record = data[..fileEnd];
            ulong id = U64(record, file + 16);
            var attributes = (FileAttributes)U32(record, file + 12);
            long writeTime = 0;
            uint extra = U32(record, file + 32);
            if (extra != 0)
            {
                int info = Offset(record, file, extra, 40);
                uint length = U32(record, file + 36);
                if (length != 0 && length < 24) throw Bad();
                Require(record, info, length == 0 ? 56 : checked((int)length));
                writeTime = I64(record, info + 16);
            }
            long logical = 0, allocated = 0;
            uint firstStream = U32(record, file + 28);
            if (firstStream != 0)
            {
                int stream = Offset(record, file, firstStream, 40);
                while (true)
                {
                    Require(record, stream, 48);
                    uint version = U32(record, stream);
                    if (version is not (1 or 2)) throw new InvalidDataException("Unsupported stream layout record version.");
                    uint nextStream = U32(record, stream + 4);
                    int streamEnd = nextStream == 0 ? fileEnd : Offset(record, stream, nextStream, 48);
                    uint byteLength = U32(record, stream + 44);
                    if ((byteLength & 1) != 0) throw Bad();
                    Require(record[..streamEnd], stream + 48, checked((int)byteLength));
                    var identifier = MemoryMarshal.Cast<byte, char>(record.Slice(stream + 48, (int)byteLength));
                    // Version 1 reserves AttributeTypeCode; its identifier still names the type.
                    bool isData = version == 2 ? U32(record, stream + 36) == 0x80
                        : identifier.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase);
                    if (isData)
                    {
                        long size = I64(record, stream + 24), allocation = I64(record, stream + 16);
                        if (size < 0 || allocation < 0) throw Bad();
                        logical = checked(logical + size);
                        allocated = checked(allocated + allocation);
                    }
                    if (nextStream == 0) break;
                    stream = streamEnd;
                }
            }
            int index = builder.AddFile(new(new(id), attributes, logical, allocated, writeTime));
            if ((id & 0x0000FFFFFFFFFFFF) == 5 && (attributes & FileAttributes.Directory) != 0)
                builder.RootIndex = index;
            uint firstName = U32(record, file + 24);
            if (firstName != 0)
            {
                int name = Offset(record, file, firstName, 40);
                while (true)
                {
                    Require(record, name, 24);
                    uint nextName = U32(record, name);
                    int nameEnd = nextName == 0 ? fileEnd : Offset(record, name, nextName, 24);
                    uint flags = U32(record, name + 4), byteLength = U32(record, name + 16);
                    if ((byteLength & 1) != 0) throw Bad();
                    Require(record[..nameEnd], name + 24, checked((int)byteLength));
                    // DOS-only aliases are excluded; PRIMARY|DOS is a legitimate name.
                    if ((flags & 2) == 0 || (flags & 1) != 0)
                        builder.AddLink(index, new(U64(record, name + 8)),
                            MemoryMarshal.Cast<byte, char>(record.Slice(name + 24, (int)byteLength)));
                    if (nextName == 0) break;
                    name = nameEnd;
                }
            }
            if (i + 1 < count && nextFile == 0 || i + 1 == count && nextFile != 0) throw Bad();
            file = fileEnd;
        }
    }

    internal static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    internal static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
    internal static long I64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
    internal static void Require(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length) throw Bad();
    }
    private static int Offset(ReadOnlySpan<byte> data, int origin, uint delta, int minimum, bool allowZero = false)
    {
        if (delta == 0 && allowZero) return 0;
        if (delta < minimum || delta > int.MaxValue) throw Bad();
        int result = checked(origin + (int)delta);
        Require(data, result, 0);
        return result;
    }
    private static InvalidDataException Bad() => new("Invalid or incomplete Windows metadata record.");
}
