using SpaceMaid.Core.IO;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpaceMaid.Core.Scanning;

public readonly record struct NtfsLayoutBlock(ReadOnlyMemory<byte> Data, double QueryMilliseconds);

// The callback consumes each borrowed buffer before returning. Sources do not
// parse metadata or build an inventory; the caller owns aggregation and UI.
public interface INtfsLayoutSource
{
    void Acquire(string volume, Action<NtfsLayoutBlock> consume, CancellationToken token);
    void AcquireFiles(string volume, IReadOnlyList<FileIdentity> files, Action<NtfsLayoutBlock> consume, CancellationToken token) =>
        throw new NotSupportedException("This metadata source does not support scoped NTFS queries.");
    NtfsJournalChanges ReadJournal(string volume, NtfsJournalCheckpoint? since, CancellationToken token) =>
        throw new NotSupportedException("This metadata source does not support the NTFS change journal.");
}

public sealed partial class NativeNtfsLayoutSource : INtfsLayoutSource
{
    public int InitialBufferBytes { get; }
    public bool ReadAhead { get; }

    public NativeNtfsLayoutSource() : this(8 * 1024 * 1024, true) { }

    public NativeNtfsLayoutSource(int initialBufferBytes, bool readAhead = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialBufferBytes, 64 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialBufferBytes, NtfsLayoutAcquisition.MaximumBufferBytes);
        InitialBufferBytes = initialBufferBytes;
        ReadAhead = readAhead;
    }

    public static string ValidateVolume(string volume)
    {
        if (volume.Length != 3 || !char.IsAsciiLetter(volume[0]) || volume[1] != ':' || volume[2] != '\\')
            throw new InvalidDataException("Only a local NTFS drive root is accepted.");
        if (!DiskAccess.Default.CanUseNtfs(volume))
            throw new InvalidDataException("This is not an accessible local NTFS volume.");
        return volume;
    }

    public void Acquire(string volume, Action<NtfsLayoutBlock> consume, CancellationToken token)
    {
        byte[] input = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), 0x37);
        Acquire(volume, input, InitialBufferBytes, ReadAhead, consume, token);
    }

    public void AcquireFiles(string volume, IReadOnlyList<FileIdentity> files, Action<NtfsLayoutBlock> consume, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateFiles(files);
        var captured = files.ToArray();
        // A request contains at most 1024 IDs. Avoid a worker and a second large
        // buffer for these short refreshes; retain growth for unusually big records.
        Acquire(volume, CreateFilesInput(captured), 1024 * 1024, false, consume, token);
    }

    public static void ValidateFiles(IReadOnlyList<FileIdentity> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count is < 1 or > ScanPipeProtocol.MaximumFileIds)
            throw new InvalidDataException("A scoped NTFS query needs between 1 and 1024 file identities.");
        var seen = new HashSet<ulong>();
        foreach (var file in files)
            if (file.High != 0 || file.Low == 0 || !seen.Add(file.Low))
                throw new InvalidDataException("A scoped NTFS query needs distinct, nonzero 64-bit NTFS file identities.");
    }

    internal static byte[] CreateFilesInput(IReadOnlyList<FileIdentity> files)
    {
        ValidateFiles(files);
        byte[] input = new byte[checked(16 + files.Count * 16)];
        BinaryPrimitives.WriteUInt32LittleEndian(input, (uint)files.Count);
        // STREAMS | EXTRA_INFO | resident/unallocated streams | RESTART.
        // No names/extents: scoped directory membership stays with the caller.
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), 0x35);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 2);
        for (int i = 0; i < files.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16 + i * 16), files[i].Low);
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24 + i * 16), files[i].Low);
        }
        return input;
    }

    private static unsafe void Acquire(string volume, byte[] input, int initialBufferBytes, bool readAhead,
        Action<NtfsLayoutBlock> consume, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateVolume(volume);
        using var handle = DiskAccess.Default.OpenHandle(@"\\.\" + volume[..2], 0x80000000, 7, 0, 3, 0, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        NtfsLayoutAcquisition.Acquire(Query, initialBufferBytes, readAhead, consume, token);

        NtfsLayoutQueryResult Query(byte[] output, int size)
        {
            long started = Stopwatch.GetTimestamp();
            bool success; int bytes;
            fixed (byte* from = input)
            fixed (byte* to = output)
                success = DiskAccess.Default.DeviceControl(handle, NativeMethods.QueryFileLayout,
                    from, input.Length, to, size, out bytes, 0);
            int error = success ? 0 : Marshal.GetLastPInvokeError();
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (success)
            {
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4));
                BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), flags & ~1u);
            }
            return new(bytes, error, elapsed);
        }
    }
}
