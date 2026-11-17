using SpaceMaid.Core.IO;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

// Serializable proof of the identity that was actually reviewed. Restoration
// validates this proof; it must never capture a replacement's current identity.
public sealed record FolderDeletionIdentity(string Path, FileIdentity Identity, ulong VolumeSerialNumber,
    long LogicalBytes = 0, long AllocatedBytes = 0, bool HasAllocationData = false, bool IsIncomplete = false);

// Cleanup plans and saved identities describe directories only.
public sealed class FolderDeletionTarget : DeletionTarget
{
    private FolderDeletionTarget(FolderDeletionIdentity identity, DiskAccess files)
        : base(identity.Path, isDirectory: true, (identity.Identity, identity.VolumeSerialNumber), files,
            identity.LogicalBytes, identity.AllocatedBytes, identity.HasAllocationData, identity.IsIncomplete) { }

    public FolderDeletionIdentity ExportCapturedIdentity()
        => new(Path, Identity, Volume, LogicalBytes, AllocatedBytes, HasAllocationData, IsIncomplete);

    public static FolderDeletionTarget RestoreCapturedIdentity(FolderDeletionIdentity identity, DiskAccess files)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(files);
        string path = ValidatePath(identity.Path);
        if (identity.Identity == default || identity.LogicalBytes < 0 || identity.AllocatedBytes < 0)
            throw new InvalidDataException("The captured cleanup identity is invalid.");
        var target = new FolderDeletionTarget(identity with { Path = path }, files);
        target.ValidateCurrent();
        return target;
    }

    public static FolderDeletionIdentity CaptureScopeIdentity(string path, DiskAccess files)
    {
        ArgumentNullException.ThrowIfNull(files);
        path = ScopePath(path);
        var identity = ReadIdentityUsing(path, directory: true, files);
        return new(path, identity.Identity, identity.Volume);
    }

    public static void ValidateScopeIdentity(FolderDeletionIdentity identity, DiskAccess files)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(files);
        string path = ScopePath(identity.Path);
        var current = ReadIdentityUsing(path, directory: true, files);
        if (identity.Identity == default || current.Identity != identity.Identity || current.Volume != identity.VolumeSerialNumber)
            throw new InvalidOperationException("The cleanup location has changed. Review its scope before using it again.");
    }

    private static string ScopePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || path.StartsWith('\\')
            || path.Length < 3 || path[1] != ':' || path[2] is not ('\\' or '/'))
            throw new ArgumentException("Choose an absolute local cleanup location.");
        if (path[3..].Split(['\\', '/']).Any(segment => segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
            || segment.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("The cleanup location is not a safe local path.");
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    }

    // Extension inspections capture the folder's contents separately.
    public static FolderDeletionTarget CaptureCurrent(string path, long logicalBytes)
    {
        if (logicalBytes < 0) throw new ArgumentOutOfRangeException(nameof(logicalBytes));
        path = ValidatePath(path);
        var files = DiskAccess.Default;
        var live = ReadIdentityUsing(path, directory: true, files);
        return new(new(path, live.Identity, live.Volume, logicalBytes), files);
    }

    public static FolderDeletionTarget FromScan(ScanSnapshot scan, int link)
        => FromScan(scan, link, DiskAccess.Default);

    public static FolderDeletionTarget FromScan(ScanSnapshot scan, int link, DiskAccess files)
    {
        var (entry, path, live) = CaptureSelection(scan, link, files, folderOnly: true);
        return new(new(path, live.Identity, live.Volume, entry.LogicalBytes, entry.AllocatedBytes,
            scan.HasAllocationData, entry.IsIncomplete || scan.IsUnmeasuredDirectory(entry.FileIndex)), files);
    }

    public static string? SelectionBlockReason(ScanSnapshot scan, int link)
        => SelectionBlockReason(scan, link, folderOnly: true);

    internal static string ValidatePath(string path) => ValidateDeletionPath(path);

    public void DeletePermanently(DiskAccess files, ScanSnapshot current, CancellationToken token = default,
        Action? mutationStarted = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(current);
        files.DeleteConfirmedDirectoryTree(ValidatePath(Path), Identity, Volume, current, token, mutationStarted);
    }
}
