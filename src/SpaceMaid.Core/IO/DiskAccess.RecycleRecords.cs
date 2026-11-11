using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.IO;

public sealed partial class DiskAccess
{
    private const int MaximumRecycleRecordBytes = 28 + 32768 * 2;
    internal enum RecycleRecordState { Valid, Corrupt, Unsupported }
    internal sealed record RecycleRecordSnapshot(FileRecord Metadata, long Changed, byte[] Bytes);

    // Known $I layouts: https://github.com/libyal/dtformats/blob/main/documentation/Windows%20Recycle.Bin%20file%20formats.asciidoc
    // Do not infer corruption from an unfamiliar version, a path's spelling, or
    // a mismatch with the payload size. Stored paths are never used for I/O.
    internal static RecycleRecordState InspectRecycleRecord(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) return RecycleRecordState.Corrupt;
        long version = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        if (version == 0) return RecycleRecordState.Corrupt;
        if (version is not (1 or 2)) return RecycleRecordState.Unsupported;
        // The stored size/time are not needed here. In particular, .NET date
        // limits must not turn a Windows FILETIME into deletion authorization.
        if (bytes.Length < 24) return RecycleRecordState.Corrupt;
        ReadOnlySpan<byte> path;
        if (version == 2)
        {
            if (bytes.Length < 28) return RecycleRecordState.Corrupt;
            uint characters = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
            if (characters < 2 || (ulong)characters * 2 > (ulong)(bytes.Length - 28)) return RecycleRecordState.Corrupt;
            path = bytes.Slice(28, (int)characters * 2);
            if (BinaryPrimitives.ReadUInt16LittleEndian(path[^2..]) != 0) return RecycleRecordState.Corrupt;
        }
        else path = bytes[24..]; // Older records may pad the terminated path.
        for (int offset = 0; offset + 1 < path.Length; offset += 2)
            if (BinaryPrimitives.ReadUInt16LittleEndian(path[offset..]) == 0)
                return offset == 0 ? RecycleRecordState.Corrupt : RecycleRecordState.Valid;
        return RecycleRecordState.Corrupt;
    }

    private RecycleRecordSnapshot ReadRecycleRecord(string path, LeftoverName record, ulong volume, CancellationToken token)
    {
        if (record.Record.IsDirectory) throw new IOException("Recycle Bin record is a folder. Entry kept.");
        if (record.Record.LogicalBytes > MaximumRecycleRecordBytes)
            throw new IOException("Recycle Bin record is too large to verify. Entry kept.");
        using (var metadata = OpenPermanentDeleteHandle(path, 0x80, 7))
            VerifyPermanentDeleteEntry(InspectPermanentDeleteHandle(metadata), record.Record, volume);
        // Deny writes and renames while capturing the record. Attribute checks
        // precede readable acquisition to avoid recalling provider placeholders.
        using var handle = OpenPermanentDeleteHandle(path, 0x80000080, 1);
        VerifyPermanentDeleteEntry(InspectPermanentDeleteHandle(handle), record.Record, volume);
        VerifyPermanentDeleteFile(handle, record.Record);
        VerifyPermanentDeleteStreams(handle, directory: false);
        long changed = ReadLeftoverChangeTime(handle);
        if (changed != record.Changed) throw new IOException("Recycle Bin record changed during review.");
        byte[] bytes = new byte[(int)record.Record.LogicalBytes];
        ReadRecycleRecordBytes(handle, bytes, token);
        VerifyPermanentDeleteEntry(InspectPermanentDeleteHandle(handle), record.Record, volume);
        if (changed != ReadLeftoverChangeTime(handle)) throw new IOException("Recycle Bin record changed during review.");
        return new(record.Record, changed, bytes);
    }

    private void ReadRecycleRecordBytes(SafeFileHandle handle, byte[] bytes, CancellationToken token)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            int read = Read(handle, bytes.AsSpan(offset), offset);
            if (read == 0) throw new IOException("Recycle Bin record changed while being read.");
            offset += read;
        }
    }

    private SafeFileHandle? PinCorruptRecycleRecord(string path, RecycleLeftover item, ulong volume, CancellationToken token)
    {
        if (item.DamagedRecord is not { } captured) return null;
        string mate = Path.Join(path, MateName(item.Name));
        using (var metadata = OpenPermanentDeleteHandle(mate, 0x80, 7))
            VerifyPermanentDeleteEntry(InspectPermanentDeleteHandle(metadata), captured.Metadata, volume);
        var handle = OpenPermanentDeleteHandle(mate, 0x80010080, 0);
        try
        {
            VerifyPermanentDeleteEntry(InspectPermanentDeleteHandle(handle), captured.Metadata, volume);
            VerifyPermanentDeleteFile(handle, captured.Metadata);
            VerifyPermanentDeleteStreams(handle, directory: false);
            if (ReadLeftoverChangeTime(handle) != captured.Changed) throw new IOException("Recycle Bin record changed after review. Entry kept.");
            byte[] bytes = new byte[captured.Bytes.Length];
            ReadRecycleRecordBytes(handle, bytes, token);
            if (!bytes.AsSpan().SequenceEqual(captured.Bytes) || InspectRecycleRecord(bytes) != RecycleRecordState.Corrupt)
                throw new IOException("Recycle Bin record changed after review. Entry kept.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private void CheckRecycleRecord(LeftoverDirectoryLease source, RecycleLeftover item, SafeFileHandle? record)
    {
        CheckBinNameLookup(source.Last);
        if (record is null) RequireMissingMate(source.Path, item.Name);
        else if (ReadLeftoverChangeTime(record) != item.DamagedRecord!.Changed)
            throw new IOException("Recycle Bin record changed after review. Entry kept.");
    }
}
