using System.IO;
using System.Runtime.InteropServices;
using SpaceMaid.Core.IO;

namespace SpaceMaid.App;

internal sealed record RecycleBinSnapshot(string RootPath, string VolumeId, long Bytes, long Items);
internal sealed record RecycleBinCleanupResult(bool Cancelled, RecycleBinSnapshot Remaining);

// Empty/null paths mean every drive to Windows and must never reach the Shell.
internal sealed class RecycleBinService
{
    internal const string StorageName = "Recycle Bin storage";
    internal const string StorageDescription = "Scanned storage can include leftover files and other Windows accounts. Cleanup checks your account for both Windows-listed items and leftovers.";
    private readonly Func<string, string?> _volume;
    private readonly Func<string, (long Bytes, long Items)> _query;
    private readonly Func<nint, string, int> _empty;
    private readonly Func<DiskAccess, string, CancellationToken, bool> _hasLeftovers;
    private readonly Func<DiskAccess, string, CancellationToken, DiskAccess.RecycleLeftoverReview> _reviewLeftovers;
    internal RecycleBinService() : this(DriveDiscovery.VolumeId, QueryNative,
        (owner, root) => SHEmptyRecycleBinW(owner, root, 0x1 | 0x4)) { }
    internal RecycleBinService(Func<string, string?> volume, Func<string, (long Bytes, long Items)> query,
        Func<nint, string, int> empty)
        : this(volume, query, empty, (files, root, token) => files.HasRecycleLeftovers(root, token)) { }

    internal RecycleBinService(Func<string, string?> volume, Func<string, (long Bytes, long Items)> query,
        Func<nint, string, int> empty, Func<DiskAccess, string, CancellationToken, bool> hasLeftovers,
        Func<DiskAccess, string, CancellationToken, DiskAccess.RecycleLeftoverReview>? reviewLeftovers = null)
    {
        _volume = volume; _query = query; _empty = empty; _hasLeftovers = hasLeftovers;
        _reviewLeftovers = reviewLeftovers ?? ((files, root, token) => files.ReviewRecycleLeftovers(root, token));
    }

    internal Task<RecycleBinSnapshot> QueryAsync(string root) => Task.Run(() => Query(root));
    internal Task<bool> HasLeftoversAsync(DiskAccess files, RecycleBinSnapshot confirmed, CancellationToken token)
        => Task.Run(() => OnConfirmedVolume(confirmed, root => _hasLeftovers(files, root, token)), token);

    internal DiskAccess.RecycleLeftoverReview ReviewLeftovers(DiskAccess files, RecycleBinSnapshot confirmed, CancellationToken token)
        => OnConfirmedVolume(confirmed, root => _reviewLeftovers(files, root, token));

    internal void DeleteLeftovers(RecycleBinSnapshot confirmed, DiskAccess.RecycleLeftoverReview review,
        IReadOnlyList<DiskAccess.RecycleLeftover> approved, IProgress<DiskAccess.RecycleLeftoverResult> results, CancellationToken token)
        => OnConfirmedVolume(confirmed, _ => { review.Delete(approved, results, token); return true; });

    private T OnConfirmedVolume<T>(RecycleBinSnapshot confirmed, Func<string, T> read)
    {
        string root = ValidateRoot(confirmed.RootPath);
        RequireSameVolume();
        T result = read(root);
        RequireSameVolume();
        return result;

        void RequireSameVolume()
        {
            if (!confirmed.VolumeId.Equals(_volume(root), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Drive changed. Check its Recycle Bin again.");
        }
    }
    private RecycleBinSnapshot Query(string root)
    {
        root = ValidateRoot(root);
        string identity = _volume(root) ?? throw new IOException("This drive's Recycle Bin is unavailable.");
        var info = _query(root);
        if (info.Bytes < 0 || info.Items < 0) throw new IOException("Windows returned an invalid Recycle Bin size.");
        if (!identity.Equals(_volume(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Drive changed. Choose it again.");
        return new(root, identity, info.Bytes, info.Items);
    }

    internal Task<RecycleBinCleanupResult> EmptyAsync(RecycleBinSnapshot confirmed, nint owner, CancellationToken token,
        Action<string, object>? diagnostic = null)
    {
        var completion = new TaskCompletionSource<RecycleBinCleanupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var current = Query(confirmed.RootPath);
                if (!current.VolumeId.Equals(confirmed.VolumeId, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Drive changed. Review the Recycle Bin again.");
                if (current.Bytes != confirmed.Bytes || current.Items != confirmed.Items)
                    throw new IOException("Recycle Bin contents changed. Review it again.");
                token.ThrowIfCancellationRequested();
                if (current.Items == 0) { completion.SetResult(new(false, current)); return; }
                diagnostic?.Invoke("recycle-bin-native-start", new { current.RootPath, current.Items, current.Bytes, current.VolumeId });
                int result = _empty(owner, current.RootPath);
                diagnostic?.Invoke("recycle-bin-native-finish", new { current.RootPath, HResult = $"0x{result:X8}" });
                bool cancelled = result is unchecked((int)0x800704C7) or unchecked((int)0x80270000) or unchecked((int)0x80270001);
                if (!cancelled) Marshal.ThrowExceptionForHR(result);
                // Cancellation cannot roll back items already permanently deleted.
                var remaining = Query(current.RootPath);
                if (!remaining.VolumeId.Equals(confirmed.VolumeId, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Drive changed. Cleanup results could not be checked.");
                diagnostic?.Invoke("recycle-bin-checked", remaining);
                completion.SetResult(new(cancelled, remaining));
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "Space Maid emptying Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    internal static string? RootForFolder(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return null;
        string folder = path[3..].TrimEnd('\\');
        return IsStorageName(folder) ? ValidateRoot(path[..3]) : null;
    }

    internal static bool IsStorageName(string name) => name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Recycler", StringComparison.OrdinalIgnoreCase);

    internal static string ValidateRoot(string root)
    {
        if (root is null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\')
            throw new ArgumentException("Choose a local drive before emptying its Recycle Bin.", nameof(root));
        return char.ToUpperInvariant(root[0]) + ":\\";
    }
    private static (long Bytes, long Items) QueryNative(string root)
    {
        // shellapi.h packs this structure to one byte only for Win32.
        if (IntPtr.Size == 4)
        {
            var info = new QueryInfo32 { Size = (uint)Marshal.SizeOf<QueryInfo32>() };
            Marshal.ThrowExceptionForHR(SHQueryRecycleBin32(root, ref info));
            return (info.Bytes, info.Items);
        }
        var wide = new QueryInfo64 { Size = (uint)Marshal.SizeOf<QueryInfo64>() };
        Marshal.ThrowExceptionForHR(SHQueryRecycleBin64(root, ref wide));
        return (wide.Bytes, wide.Items);
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct QueryInfo32 { public uint Size; public long Bytes, Items; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct QueryInfo64 { public uint Size; public long Bytes, Items; }
    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHQueryRecycleBin32(string root, ref QueryInfo32 info);
    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHQueryRecycleBin64(string root, ref QueryInfo64 info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHEmptyRecycleBinW(nint owner, string root, uint flags);
}
