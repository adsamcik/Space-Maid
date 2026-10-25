using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Deduplication;

public enum StoragePreservationAction { Compress, Decompress, ShareBlocks }
public sealed record StoragePreservationResult(string Path, StoragePreservationAction Action, bool Completed, bool Cancelled,
    long LogicalBytes, long AllocatedBefore, long AllocatedAfter, long SharedLogicalBytes, string Message)
{
    public long ObservedAllocationReduction => Math.Max(0, AllocatedBefore - AllocatedAfter);
}
public sealed record StorageCapabilities(string FileSystem, bool Compression, bool BlockSharing, uint ClusterBytes, string Note);

// These actions retain names, independent file identities, payloads and other
// streams. They never delete files or create hard links. Each call is explicit.
public static partial class StoragePreservation
{
    private const uint SetCompression = 0x0009C040, DuplicateExtents = 0x00098344, GetIntegrity = 0x0009027C;
    private static readonly string[] BuildSuffixes = [".pdb", ".obj", ".lib", ".rlib", ".dll"];
    public const long MaximumActionBytes = 1024L * 1024 * 1024;

    public static Task<DuplicateFile> InspectBuildFileAsync(string path, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); using var file = StorageFile.Open(path);
        if (file.Revision.Length > MaximumActionBytes) throw new InvalidOperationException("Storage actions currently inspect files up to 1 GiB.");
        var captured = new DuplicateFile(path, file.Revision, "");
        if (!IsColdBuildCandidate(captured) && (captured.Revision.Attributes & FileAttributes.Compressed) == 0)
            throw new InvalidOperationException("Choose a supported build file created and last written at least 14 days ago, or an already compressed file to undo compression.");
        string hash = DuplicateFinder.Hash(file, new(MaximumActionBytes, TimeSpan.FromMinutes(1), token));
        return captured with { Hash = hash };
    });

    public static StorageCapabilities Capabilities(string path)
    {
        string root = Path.GetPathRoot(OptimizationPolicy.NormalizePath(path)) ?? throw new ArgumentException("Choose a local file.");
        var format = new StringBuilder(32);
        if (!GetVolumeInformationW(root, null, 0, out _, out _, out uint flags, format, format.Capacity))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!GetDiskFreeSpaceW(root, out uint sectors, out uint bytes, out _, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        string filesystem = format.ToString(); bool ntfs = filesystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        bool refs = filesystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase) && (flags & 0x08000000) != 0;
        uint cluster = checked(sectors * bytes);
        if (cluster == 0) throw new IOException("The filesystem did not report a usable cluster size.");
        return new(filesystem, ntfs && (flags & 0x10) != 0, refs, cluster,
            refs ? "Block sharing preserves independent files; physical reclaim depends on existing shared extents."
                : ntfs ? "Per-file NTFS compression preserves file contents. Block sharing requires supported ReFS storage."
                : "This filesystem has no supported storage-preservation action.");
    }

    public static bool IsColdBuildCandidate(DuplicateFile file, DateTime? nowUtc = null) => BuildSuffixes.Any(s => file.Path.EndsWith(s, StringComparison.OrdinalIgnoreCase))
        && !file.HasAdditionalData && !file.SharesFileObject && (file.Revision.Attributes & (StorageFile.Unavailable | FileAttributes.ReadOnly)) == 0
        && file.Revision.Created > 0 && file.Revision.Written > 0
        && Math.Max(file.Revision.Created, file.Revision.Written) < (nowUtc ?? DateTime.UtcNow).AddDays(-14).ToFileTimeUtc();

    public static Task<StoragePreservationResult> CompressAsync(DuplicateFile confirmed, bool decompress = false,
        CancellationToken token = default) => Task.Run(() => Compress(confirmed, decompress, token));

    private static unsafe StoragePreservationResult Compress(DuplicateFile confirmed, bool decompress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var action = decompress ? StoragePreservationAction.Decompress : StoragePreservationAction.Compress;
        if (confirmed.Revision.Length > MaximumActionBytes) throw new InvalidOperationException("Storage actions are limited to 1 GiB per file.");
        if (!Capabilities(confirmed.Path).Compression) throw new InvalidOperationException("Per-file NTFS compression is unavailable on this drive.");
        if (!decompress && !IsColdBuildCandidate(confirmed)) throw new InvalidOperationException("Select a supported build file created and last written at least 14 days ago, with no shared names or additional streams.");
        using var file = StorageFile.Open(confirmed.Path, writable: true); file.Verify(confirmed.Revision);
        if (file.Revision.HasAlternateStreams || file.Revision.Links != 1) throw new InvalidOperationException("Files with shared names or additional streams stay unchanged.");
        var budget = new DuplicateFinder.ReadBudget(Math.Max(1, checked(file.Revision.Length * 3)), TimeSpan.FromMinutes(5), token);
        string before = DuplicateFinder.Hash(file, budget);
        if (!before.Equals(confirmed.Hash, StringComparison.Ordinal)) throw new IOException("Contents no longer match the confirmed comparison.");
        long allocation = file.Revision.AllocatedBytes; bool changed = false;
        try
        {
            token.ThrowIfCancellationRequested(); ushort format = decompress ? (ushort)0 : (ushort)1;
            if (!NativeMethods.DeviceIoControl(file.Handle, SetCompression, (byte*)&format, 2, null, 0, out _, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            changed = true;
            // A storage-only mutation changes the revision. Verify bytes against
            // the captured digest, rather than accepting the old change time.
            var current = StorageFile.Inspect(file.Handle);
            if (current.ObjectKey != confirmed.Revision.ObjectKey || current.Length != confirmed.Revision.Length)
                throw new IOException("The file identity or length changed during compression.");
            string after = HashCurrent(file, current.Length, CancellationToken.None);
            if (before != after) throw new IOException("Content verification failed after the storage change.");
            return new(confirmed.Path, action, true, token.IsCancellationRequested, current.Length, allocation, current.AllocatedBytes, 0,
                (decompress ? "Decompressed" : "Compressed") + " · Contents verified; names and independent file identity retained."
                + (token.IsCancellationRequested ? " The current file finished before cancellation." : ""));
        }
        finally { if (changed) StorageFile.RestoreTimes(file.Handle, confirmed.Revision); }
    }

    public static Task<StoragePreservationResult> ShareBlocksAsync(DuplicateFile keeper, DuplicateFile confirmed,
        CancellationToken token = default) => Task.Run(() => ShareBlocks(keeper, confirmed, token));

    private static unsafe StoragePreservationResult ShareBlocks(DuplicateFile keeper, DuplicateFile confirmed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (confirmed.Revision.Length > MaximumActionBytes) throw new InvalidOperationException("Storage actions are limited to 1 GiB per file.");
        var capability = Capabilities(confirmed.Path);
        if (!capability.BlockSharing) throw new InvalidOperationException("Block sharing requires a ReFS volume reporting block-reference support.");
        if (keeper.Revision.Volume != confirmed.Revision.Volume || keeper.Revision.Identity == confirmed.Revision.Identity)
            throw new InvalidOperationException("Select independent matching files on the same ReFS volume.");
        using var source = StorageFile.Open(keeper.Path); source.Verify(keeper.Revision);
        using var target = StorageFile.Open(confirmed.Path, writable: true); target.Verify(confirmed.Revision);
        if (source.Revision.Links != 1 || target.Revision.Links != 1 || source.Revision.HasAlternateStreams || target.Revision.HasAlternateStreams
            || (source.Revision.Attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0
            || (target.Revision.Attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0)
            throw new InvalidOperationException("Shared names, alternate streams, sparse and compressed files stay unchanged.");
        if (!EqualIntegrity(source.Handle, target.Handle)) throw new InvalidOperationException("ReFS integrity settings differ; files stay unchanged.");
        var budget = new DuplicateFinder.ReadBudget(Math.Max(1, checked(source.Revision.Length * 3)), TimeSpan.FromMinutes(5), token);
        if (keeper.Hash != confirmed.Hash || !DuplicateFinder.Equal(source, target, budget))
            throw new IOException("The files no longer have identical contents.");
        long aligned = source.Revision.Length / capability.ClusterBytes * capability.ClusterBytes;
        if (aligned == 0) throw new InvalidOperationException("These files are smaller than one complete filesystem cluster.");
        long shared = 0; bool changed = false;
        try
        {
            const long chunkMaximum = 64L * 1024 * 1024;
            while (shared < aligned && !token.IsCancellationRequested)
            {
                long count = Math.Min(chunkMaximum, aligned - shared);
                var input = new CloneRange { Source = source.Handle.DangerousGetHandle(), SourceOffset = shared, TargetOffset = shared, ByteCount = count };
                if (!NativeMethods.DeviceIoControl(target.Handle, DuplicateExtents, (byte*)&input, sizeof(CloneRange), null, 0, out _, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                changed = true; shared += count;
            }
            var after = StorageFile.Inspect(target.Handle);
            if (HashCurrent(target, after.Length, CancellationToken.None) != keeper.Hash)
                throw new IOException("Content verification failed after block sharing.");
            source.Verify(keeper.Revision);
            return new(confirmed.Path, StoragePreservationAction.ShareBlocks, shared == aligned, token.IsCancellationRequested,
                after.Length, confirmed.Revision.AllocatedBytes, after.AllocatedBytes, shared,
                $"{shared:N0} logical bytes now use block sharing. Both files keep independent writes; physical reclaim is unknown."
                + (token.IsCancellationRequested ? " Cancelled; completed shared ranges remain valid." : ""));
        }
        finally { if (changed) StorageFile.RestoreTimes(target.Handle, confirmed.Revision); }
    }

    private static string HashCurrent(StorageFile file, long length, CancellationToken token)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024]; long offset = 0;
        while (offset < length)
        {
            token.ThrowIfCancellationRequested(); int count = (int)Math.Min(buffer.Length, length - offset);
            int read = RandomAccess.Read(file.Handle, buffer.AsSpan(0, count), offset);
            if (read != count) throw new IOException("File length changed during verification.");
            hash.AppendData(buffer, 0, count); offset += count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static unsafe bool EqualIntegrity(SafeFileHandle first, SafeFileHandle second)
    {
        byte* a = stackalloc byte[16], b = stackalloc byte[16];
        if (!NativeMethods.DeviceIoControl(first, GetIntegrity, null, 0, a, 16, out _, 0)
            || !NativeMethods.DeviceIoControl(second, GetIntegrity, null, 0, b, 16, out _, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new ReadOnlySpan<byte>(a, 8).SequenceEqual(new ReadOnlySpan<byte>(b, 8));
    }
    [StructLayout(LayoutKind.Sequential)] private struct CloneRange { internal nint Source; internal long SourceOffset, TargetOffset, ByteCount; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder? name, int nameLength, out uint serial, out uint maximumName,
        out uint flags, StringBuilder fileSystem, int fileSystemLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);
}
