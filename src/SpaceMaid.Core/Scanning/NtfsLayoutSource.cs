using System.Buffers;
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
    public static string ValidateVolume(string volume)
    {
        if (volume.Length != 3 || !char.IsAsciiLetter(volume[0]) || volume[1] != ':' || volume[2] != '\\')
            throw new InvalidDataException("Only a local NTFS drive root is accepted.");
        var drive = new DriveInfo(volume);
        if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)
            || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This is not an accessible local NTFS volume.");
        return volume;
    }

    public void Acquire(string volume, Action<NtfsLayoutBlock> consume, CancellationToken token)
    {
        byte[] input = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), 0x37);
        Acquire(volume, input, consume, token);
    }

    public void AcquireFiles(string volume, IReadOnlyList<FileIdentity> files, Action<NtfsLayoutBlock> consume, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateFiles(files);
        var captured = files.ToArray();
        Acquire(volume, CreateFilesInput(captured), consume, token);
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

    private static unsafe void Acquire(string volume, byte[] input, Action<NtfsLayoutBlock> consume, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateVolume(volume);
        using var handle = NativeMethods.CreateFile(@"\\.\" + volume[..2], 0x80000000, 7, 0, 3, 0, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        int size = 1024 * 1024;
        byte[] output = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                long started = Stopwatch.GetTimestamp();
                bool success; int bytes;
                fixed (byte* from = input)
                fixed (byte* to = output)
                    success = NativeMethods.DeviceIoControl(handle, NativeMethods.QueryFileLayout,
                        from, input.Length, to, size, out bytes, 0);
                int error = success ? 0 : Marshal.GetLastPInvokeError();
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!success)
                {
                    consume(new(ReadOnlyMemory<byte>.Empty, elapsed));
                    if (error == NativeMethods.HandleEof) return;
                    if (error is 122 or 234 && size < 64 * 1024 * 1024)
                    {
                        ArrayPool<byte>.Shared.Return(output);
                        size *= 2; output = ArrayPool<byte>.Shared.Rent(size);
                        continue;
                    }
                    throw new Win32Exception(error);
                }
                if (bytes < 16 || bytes > size) throw new InvalidDataException("Invalid NTFS metadata response length.");
                consume(new(output.AsMemory(0, bytes), elapsed));
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4));
                BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), flags & ~1u);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(output); }
    }
}
