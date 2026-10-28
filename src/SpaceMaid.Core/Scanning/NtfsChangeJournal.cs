using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceMaid.Core.Scanning;

public readonly record struct NtfsJournalCheckpoint(ulong VolumeSerial, ulong JournalId, long NextUsn);
public readonly record struct NtfsJournalChange(FileIdentity Identity, FileIdentity ParentIdentity, uint Reason, FileAttributes Attributes);
public sealed record NtfsJournalChanges(NtfsJournalCheckpoint Checkpoint, IReadOnlyList<NtfsJournalChange> Changes);

// Checkpoints identify a particular volume and journal generation. The cursor
// precedes the associated acquisition; a delta is bounded by a fixed fence.
public static class NtfsChangeJournal
{
    public const int MaximumChanges = 262144;
    internal const int MaximumReadBytes = 16 * 1024 * 1024;
    internal const int MaximumResponseBytes = 28 + MaximumChanges * 24;

    internal readonly record struct State(NtfsJournalCheckpoint Checkpoint, long FirstUsn, long LowestValidUsn);

    public static void ValidateCheckpoint(NtfsJournalCheckpoint checkpoint)
    {
        if (checkpoint.JournalId == 0 || checkpoint.NextUsn < 0)
            throw new InvalidDataException("Invalid NTFS change-journal checkpoint.");
    }

    internal static State ParseState(ReadOnlySpan<byte> data, ulong volumeSerial)
    {
        if (data.Length < 56) throw new InvalidDataException("Incomplete NTFS change-journal state.");
        var checkpoint = new NtfsJournalCheckpoint(volumeSerial, BinaryPrimitives.ReadUInt64LittleEndian(data),
            BinaryPrimitives.ReadInt64LittleEndian(data[16..]));
        ValidateCheckpoint(checkpoint);
        long first = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
        long lowest = BinaryPrimitives.ReadInt64LittleEndian(data[24..]);
        if (first < 0 || lowest < 0 || first > checkpoint.NextUsn || lowest > checkpoint.NextUsn)
            throw new InvalidDataException("Invalid NTFS change-journal range.");
        return new(checkpoint, first, lowest);
    }

    internal static void ValidateRange(NtfsJournalCheckpoint since, State state)
    {
        ValidateCheckpoint(since);
        if (since.VolumeSerial != state.Checkpoint.VolumeSerial || since.JournalId != state.Checkpoint.JournalId
            || since.NextUsn < state.FirstUsn || since.NextUsn < state.LowestValidUsn || since.NextUsn > state.Checkpoint.NextUsn)
            throw new IOException("The NTFS change history is no longer available for this inventory.");
    }

    internal static long ParseRecords(ReadOnlySpan<byte> data, long start, long fence, List<NtfsJournalChange> changes)
    {
        if (data.Length < 8) throw new InvalidDataException("Incomplete NTFS change-journal response.");
        long next = BinaryPrimitives.ReadInt64LittleEndian(data);
        if (next <= start) throw new IOException("The NTFS change-journal read made no forward progress.");
        long previous = start - 1;
        for (int offset = 8; offset < data.Length;)
        {
            var record = data[offset..];
            if (record.Length < 8) throw new InvalidDataException("Incomplete NTFS change-journal record.");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record);
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            int header = version switch { 2 => 60, 3 => 76, _ => throw new NotSupportedException("Unsupported NTFS change-journal record version.") };
            if (minor != 0 || length < header || length > record.Length || (length & 7) != 0)
                throw new InvalidDataException("Invalid NTFS change-journal record length or version.");
            record = record[..(int)length];
            int parentOffset = version == 2 ? 16 : 24, usnOffset = version == 2 ? 24 : 40;
            int reasonOffset = version == 2 ? 40 : 56, attributeOffset = version == 2 ? 52 : 68;
            int nameOffset = version == 2 ? 56 : 72;
            ulong identity = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
            ulong parent = BinaryPrimitives.ReadUInt64LittleEndian(record[parentOffset..]);
            if (identity == 0 || parent == 0 || version == 3 && (BinaryPrimitives.ReadUInt64LittleEndian(record[16..]) != 0
                || BinaryPrimitives.ReadUInt64LittleEndian(record[32..]) != 0))
                throw new NotSupportedException("The change journal did not return supported NTFS file identities.");
            long usn = BinaryPrimitives.ReadInt64LittleEndian(record[usnOffset..]);
            if (usn < start || usn <= previous || usn >= next)
                throw new InvalidDataException("Invalid NTFS change-journal record order.");
            previous = usn;
            ushort nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(record[nameOffset..]);
            ushort nameStart = BinaryPrimitives.ReadUInt16LittleEndian(record[(nameOffset + 2)..]);
            if ((nameBytes & 1) != 0 || nameStart < header || nameStart > record.Length || nameBytes > record.Length - nameStart)
                throw new InvalidDataException("Invalid NTFS change-journal filename range.");
            if (usn < fence)
            {
                if (changes.Count >= MaximumChanges) throw new IOException("The NTFS change delta exceeds the bounded recheck limit.");
                changes.Add(new(new(identity), new(parent), BinaryPrimitives.ReadUInt32LittleEndian(record[reasonOffset..]),
                    (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[attributeOffset..])));
            }
            offset = checked(offset + (int)length);
        }
        return next;
    }

    public static byte[] Encode(NtfsJournalChanges changes)
    {
        ValidateCheckpoint(changes.Checkpoint);
        if (changes.Changes.Count > MaximumChanges) throw new InvalidDataException("Too many NTFS journal changes.");
        byte[] data = new byte[checked(28 + changes.Changes.Count * 24)];
        WriteCheckpoint(data, changes.Checkpoint);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), changes.Changes.Count);
        for (int i = 0; i < changes.Changes.Count; i++)
        {
            var change = changes.Changes[i]; ValidateChange(change);
            var record = data.AsSpan(28 + i * 24, 24);
            BinaryPrimitives.WriteUInt64LittleEndian(record, change.Identity.Low);
            BinaryPrimitives.WriteUInt64LittleEndian(record[8..], change.ParentIdentity.Low);
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], change.Reason);
            BinaryPrimitives.WriteUInt32LittleEndian(record[20..], (uint)change.Attributes);
        }
        return data;
    }

    public static NtfsJournalChanges Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28 || data.Length > MaximumResponseBytes) throw new InvalidDataException("Invalid NTFS journal result length.");
        var checkpoint = ReadCheckpoint(data);
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
        if (count is < 0 or > MaximumChanges || data.Length != 28 + count * 24)
            throw new InvalidDataException("Invalid NTFS journal result count.");
        var changes = new NtfsJournalChange[count];
        for (int i = 0; i < count; i++)
        {
            var record = data[(28 + i * 24)..];
            var change = new NtfsJournalChange(new(BinaryPrimitives.ReadUInt64LittleEndian(record)),
                new(BinaryPrimitives.ReadUInt64LittleEndian(record[8..])), BinaryPrimitives.ReadUInt32LittleEndian(record[16..]),
                (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[20..]));
            ValidateChange(change); changes[i] = change;
        }
        return new(checkpoint, changes);
    }

    internal static void WriteCheckpoint(Span<byte> data, NtfsJournalCheckpoint checkpoint)
    {
        ValidateCheckpoint(checkpoint);
        BinaryPrimitives.WriteUInt64LittleEndian(data, checkpoint.VolumeSerial);
        BinaryPrimitives.WriteUInt64LittleEndian(data[8..], checkpoint.JournalId);
        BinaryPrimitives.WriteInt64LittleEndian(data[16..], checkpoint.NextUsn);
    }

    internal static NtfsJournalCheckpoint ReadCheckpoint(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24) throw new InvalidDataException("Incomplete NTFS journal checkpoint.");
        var checkpoint = new NtfsJournalCheckpoint(BinaryPrimitives.ReadUInt64LittleEndian(data),
            BinaryPrimitives.ReadUInt64LittleEndian(data[8..]), BinaryPrimitives.ReadInt64LittleEndian(data[16..]));
        ValidateCheckpoint(checkpoint); return checkpoint;
    }

    private static void ValidateChange(NtfsJournalChange change)
    {
        if (change.Identity.High != 0 || change.ParentIdentity.High != 0 || change.Identity.Low == 0 || change.ParentIdentity.Low == 0)
            throw new InvalidDataException("Invalid NTFS journal change identity.");
    }
}

public sealed partial class NativeNtfsLayoutSource
{
    private const uint QueryUsnJournal = (9u << 16) | (61u << 2);
    private const uint ReadUsnJournal = (9u << 16) | (46u << 2) | 3u;

    public unsafe NtfsJournalChanges ReadJournal(string volume, NtfsJournalCheckpoint? since, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ValidateVolume(volume);
        if (since is { } supplied) NtfsChangeJournal.ValidateCheckpoint(supplied);
        using var handle = NativeMethods.CreateFile(@"\\.\" + volume[..2], 0x80000000, 7, 0, 3, 0, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        ulong serial = VolumeSerial(volume);
        var state = Query(handle, serial);
        if (since is null) return new(state.Checkpoint, []);
        var baseline = since.Value;
        NtfsChangeJournal.ValidateRange(baseline, state);
        long cursor = baseline.NextUsn, fence = state.Checkpoint.NextUsn;
        var changes = new List<NtfsJournalChange>();
        byte[] output = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int total = 0;
            byte* input = stackalloc byte[40];
            while (cursor < fence)
            {
                token.ThrowIfCancellationRequested();
                new Span<byte>(input, 40).Clear();
                BinaryPrimitives.WriteInt64LittleEndian(new(input, 8), cursor);
                BinaryPrimitives.WriteUInt32LittleEndian(new(input + 8, 4), uint.MaxValue);
                BinaryPrimitives.WriteUInt64LittleEndian(new(input + 32, 8), baseline.JournalId);
                bool success; int bytes;
                fixed (byte* buffer = output)
                    success = NativeMethods.DeviceIoControl(handle, ReadUsnJournal, input, 40, buffer, 64 * 1024, out bytes, 0);
                if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (bytes is < 8 or > 64 * 1024) throw new InvalidDataException("Invalid NTFS journal response size.");
                total = checked(total + bytes);
                if (total > NtfsChangeJournal.MaximumReadBytes) throw new IOException("The NTFS change delta exceeds the bounded recheck limit.");
                cursor = NtfsChangeJournal.ParseRecords(output.AsSpan(0, bytes), cursor, fence, changes);
            }
            token.ThrowIfCancellationRequested();
            var after = Query(handle, VolumeSerial(volume));
            NtfsChangeJournal.ValidateRange(baseline, after);
            NtfsChangeJournal.ValidateRange(state.Checkpoint, after);
            return new(state.Checkpoint, changes.ToArray());
        }
        finally { ArrayPool<byte>.Shared.Return(output); }
    }

    private static unsafe NtfsChangeJournal.State Query(SafeFileHandle handle, ulong serial)
    {
        byte* buffer = stackalloc byte[80];
        if (!NativeMethods.DeviceIoControl(handle, QueryUsnJournal, null, 0, buffer, 80, out int bytes, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (bytes is < 56 or > 80) throw new InvalidDataException("Invalid NTFS journal-state response size.");
        return NtfsChangeJournal.ParseState(new(buffer, bytes), serial);
    }

    private static ulong VolumeSerial(string volume)
    {
        if (!GetVolumeInformation(volume, 0, 0, out uint serial, out _, out _, 0, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return serial;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformation(string volume, nint name, uint nameLength,
        out uint serial, out uint componentLength, out uint flags, nint fileSystem, uint fileSystemLength);
}
