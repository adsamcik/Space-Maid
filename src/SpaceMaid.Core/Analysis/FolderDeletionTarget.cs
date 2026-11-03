using SpaceMaid.Core.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

// Serializable proof of the identity that was actually reviewed. Restoration
// validates this proof; it must never capture a replacement's current identity.
public sealed record FolderDeletionIdentity(string Path, FileIdentity Identity, ulong VolumeSerialNumber,
    long LogicalBytes = 0, long AllocatedBytes = 0, bool HasAllocationData = false, bool IsIncomplete = false);

// A resolved directory with a captured live identity, validated again before
// cleanup. Permanent removal additionally requires the caller's explicit build
// action and a fresh native snapshot of the complete target subtree.
public sealed class FolderDeletionTarget
{
    public string Path { get; }
    public long LogicalBytes { get; }
    public long AllocatedBytes { get; }
    public bool HasAllocationData { get; }
    public bool IsIncomplete { get; }
    private readonly FileIdentity _identity;
    private readonly ulong _volume;
    private readonly DiskAccess _files = DiskAccess.Default;

    private FolderDeletionTarget(string path, ScanEntry entry, bool allocation, FileIdentity identity, ulong volume)
    {
        Path = path; LogicalBytes = entry.LogicalBytes; AllocatedBytes = entry.AllocatedBytes;
        HasAllocationData = allocation; IsIncomplete = entry.IsIncomplete;
        _identity = identity; _volume = volume;
    }

    private FolderDeletionTarget(string path, long logicalBytes, FileIdentity identity, ulong volume)
    {
        Path = path; LogicalBytes = logicalBytes; _identity = identity; _volume = volume;
    }

    private FolderDeletionTarget(FolderDeletionIdentity identity, DiskAccess files)
    {
        Path = identity.Path; LogicalBytes = identity.LogicalBytes; AllocatedBytes = identity.AllocatedBytes;
        HasAllocationData = identity.HasAllocationData; IsIncomplete = identity.IsIncomplete;
        _identity = identity.Identity; _volume = identity.VolumeSerialNumber; _files = files;
    }

    public FolderDeletionIdentity ExportCapturedIdentity()
        => new(Path, _identity, _volume, LogicalBytes, AllocatedBytes, HasAllocationData, IsIncomplete);

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
        var identity = ReadIdentityUsing(path, files);
        return new(path, identity.Identity, identity.Volume);
    }

    public static void ValidateScopeIdentity(FolderDeletionIdentity identity, DiskAccess files)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(files);
        string path = ScopePath(identity.Path);
        var current = ReadIdentityUsing(path, files);
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

    // Explicit, bounded extension inspections capture their complete contents
    // separately. This factory retains the same live root/ancestor safeguards
    // and native Shell identity checks as inventory-based folder recycling.
    public static FolderDeletionTarget CaptureCurrent(string path, long logicalBytes)
    {
        if (logicalBytes < 0) throw new ArgumentOutOfRangeException(nameof(logicalBytes));
        path = ValidatePath(path);
        var live = ReadIdentity(path);
        return new(path, logicalBytes, live.Identity, live.Volume);
    }

    public static FolderDeletionTarget FromScan(ScanSnapshot scan, int link)
    {
        var (entry, path) = ResolveSelection(scan, link);
        var live = ReadIdentity(path);
        // Basic enumeration has synthetic IDs. In that mode capture the live
        // identity for the confirmation; otherwise reject a replaced scan entry.
        if (scan.HasAllocationData && live.Identity != scan.Records[entry.FileIndex].Identity)
            throw new InvalidOperationException("This folder has changed since scanning. Rescan before deleting it.");
        return new(path, entry, scan.HasAllocationData, live.Identity, live.Volume);
    }

    // Menu eligibility uses snapshot metadata only. Live identity/permissions
    // are still verified off the UI thread before confirmation and execution.
    public static string? SelectionBlockReason(ScanSnapshot scan, int link)
    {
        try { ResolveSelection(scan, link); return null; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return ex.Message; }
    }

    private static (ScanEntry Entry, string Path) ResolveSelection(ScanSnapshot scan, int link)
    {
        var entry = scan.Entry(link);
        if (!entry.IsDirectory)
            throw new InvalidOperationException("Select a folder in Explore to recycle it.");
        string path = scan.EntryPath(link) ?? throw new InvalidOperationException("An unresolved entry cannot be deleted. Rescan first.");
        if (!entry.IsPrimaryName)
            throw new InvalidOperationException("Select the primary folder entry in Explore to recycle it.");
        path = ValidatePath(path);
        string scanRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(scan.RootPath));
        if (!path.Equals(scanRoot, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(scanRoot.EndsWith('\\') ? scanRoot : scanRoot + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This path is outside the scanned location.");
        if ((scan.Records[entry.FileIndex].Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Linked or reparse folders cannot be recycled here.");
        return (entry, path);
    }

    public void ValidateCurrent()
    {
        ValidatePath(Path);
        var live = ReadIdentityUsing(Path, _files);
        if (live.Identity != _identity || live.Volume != _volume)
            throw new InvalidOperationException("This folder has been replaced since confirmation. Rescan before deleting it.");
    }

    public void DeletePermanently(DiskAccess files, ScanSnapshot current, CancellationToken token = default,
        Action? mutationStarted = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(current);
        files.DeleteConfirmedDirectoryTree(ValidatePath(Path), _identity, _volume, current, token, mutationStarted);
    }

    // Check only the exact destination reported by Windows. No Recycle Bin
    // traversal is needed, and a successful callback cannot substitute a child
    // or unrelated recycled folder for the confirmed root.
    public void ValidateRecycledIdentity(string recycledPath)
    {
        string root = System.IO.Path.GetPathRoot(Path)!;
        if (!System.IO.Path.IsPathFullyQualified(recycledPath)
            || !recycledPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Windows returned an unexpected Recycle Bin destination.");
        string[] parts = recycledPath[root.Length..].Split('\\');
        if (parts.Length != 3 || !parts[0].Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            || !parts[1].StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
            || !parts[2].StartsWith("$R", StringComparison.OrdinalIgnoreCase)
            || parts.Any(p => p is "." or ".." || p.Contains('/') || p.EndsWith('.') || p.EndsWith(' ')))
            throw new IOException("Windows returned an unexpected Recycle Bin destination.");

        var live = ReadIdentityUsing(recycledPath, _files);
        if (live.Identity != _identity || live.Volume != _volume)
            throw new IOException("Windows did not recycle the confirmed folder. Recheck this folder before trying again.");
        try { _files.GetAttributes(Path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        throw new IOException("The original folder is still present. Recheck this folder before trying again.");
    }

    internal static string ValidatePath(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith("\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Only a resolved folder on a local drive can be recycled here.");
        // Reject names that the Shell could normalize to a different target.
        if (path.Length < 3 || path[1] != ':' || path[2] is not ('\\' or '/'))
            throw new InvalidOperationException("This is not a local drive path.");
        var segments = path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ')
            || s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidOperationException("This folder name cannot be safely passed to Windows recycling.");
        string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (segments.Length == 0)
            throw new InvalidOperationException("Drive roots cannot be deleted.");
        if (segments[0].StartsWith('$') || segments[0].Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("Recycler", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Filesystem metadata and Recycle Bin storage cannot be deleted here.");
        return full;
    }

    private static (FileIdentity Identity, ulong Volume) ReadIdentity(string path) => ReadIdentityUsing(path, DiskAccess.Default);

    private static unsafe (FileIdentity Identity, ulong Volume) ReadIdentityUsing(string path, DiskAccess files)
    {
        // Reject a reparse ancestor too: lexical paths must not redirect the
        // confirmed location onto a different drive or protected namespace.
        for (string? part = path; part is not null; part = System.IO.Path.GetDirectoryName(part))
        {
            var attributes = files.GetAttributes(part);
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("The selected folder is no longer a directory.");
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A linked or reparse path cannot be recycled here.");
        }
        using var handle = files.OpenHandle(NativeMethods.ExtendedPath(path), 0x80, 7, 0, 3,
            NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        byte* buffer = stackalloc byte[24];
        if (!files.ReadHandleInformation(handle, 18, buffer, 24))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not verify this folder's identity. Deletion was blocked.");
        var data = new ReadOnlySpan<byte>(buffer, 24);
        var identity = new FileIdentity(LayoutParser.U64(data, 8), LayoutParser.U64(data, 16));
        if (identity == default) throw new InvalidOperationException("Windows did not supply a usable folder identity. Deletion was blocked.");
        return (identity, LayoutParser.U64(data, 0));
    }
}
