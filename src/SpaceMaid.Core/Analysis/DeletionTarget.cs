using SpaceMaid.Core.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

// Captures the exact Explorer name and its live identity for confirmation.
public class DeletionTarget
{
    public string Path { get; }
    public bool IsDirectory { get; }
    public long LogicalBytes { get; }
    public long AllocatedBytes { get; }
    public bool HasAllocationData { get; }
    public bool IsIncomplete { get; }
    private protected FileIdentity Identity { get; }
    private protected ulong Volume { get; }
    private DiskAccess Files { get; }

    private protected DeletionTarget(string path, bool isDirectory, (FileIdentity Identity, ulong Volume) live,
        DiskAccess files, long logicalBytes, long allocatedBytes, bool hasAllocationData, bool isIncomplete)
    {
        Path = path;
        IsDirectory = isDirectory;
        Identity = live.Identity;
        Volume = live.Volume;
        Files = files;
        LogicalBytes = logicalBytes;
        AllocatedBytes = allocatedBytes;
        HasAllocationData = hasAllocationData;
        IsIncomplete = isIncomplete;
    }

    public static DeletionTarget FromExploreSelection(ScanSnapshot scan, int link, DiskAccess files)
    {
        var (entry, path, live) = CaptureSelection(scan, link, files, folderOnly: false);
        return new(path, entry.IsDirectory, live, files, entry.LogicalBytes, entry.AllocatedBytes,
            scan.HasAllocationData, entry.IsIncomplete || entry.IsDirectory && scan.IsUnmeasuredDirectory(entry.FileIndex));
    }

    public static string? ExploreSelectionBlockReason(ScanSnapshot scan, int link)
        => SelectionBlockReason(scan, link, folderOnly: false);

    // Menu eligibility uses snapshot metadata only. Live identity and type
    // are verified off the UI thread before confirmation and execution.
    private protected static string? SelectionBlockReason(ScanSnapshot scan, int link, bool folderOnly)
    {
        try { ResolveSelection(scan, link, folderOnly); return null; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return ex.Message; }
    }

    private protected static (ScanEntry Entry, string Path, (FileIdentity Identity, ulong Volume) Live) CaptureSelection(
        ScanSnapshot scan, int link, DiskAccess files, bool folderOnly)
    {
        ArgumentNullException.ThrowIfNull(files);
        var (entry, path) = ResolveSelection(scan, link, folderOnly);
        var live = ReadIdentityUsing(path, entry.IsDirectory, files);
        // Basic enumeration has synthetic IDs. Capture a live identity for
        // confirmation in that mode; in native scans reject a replaced entry.
        if (scan.HasAllocationData && live.Identity != scan.Records[entry.FileIndex].Identity)
            throw new InvalidOperationException("This item has changed since scanning. Rescan before deleting it.");
        return (entry, path, live);
    }

    private static (ScanEntry Entry, string Path) ResolveSelection(ScanSnapshot scan, int link, bool folderOnly)
    {
        var entry = scan.Entry(link);
        if (folderOnly && !entry.IsDirectory)
            throw new InvalidOperationException("Select a folder in Explore to recycle it.");
        string path = scan.EntryPath(link) ?? throw new InvalidOperationException("An unresolved entry cannot be deleted. Rescan first.");
        if (entry.IsDirectory && !entry.IsPrimaryName)
            throw new InvalidOperationException("Select the primary folder entry in Explore to delete it.");
        path = ValidateDeletionPath(path);
        string scanRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(scan.RootPath));
        if (!path.Equals(scanRoot, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(scanRoot.EndsWith('\\') ? scanRoot : scanRoot + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This path is outside the scanned location.");
        if ((scan.Records[entry.FileIndex].Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Linked or reparse paths are protected here.");
        return (entry, path);
    }

    public void ValidateCurrent()
    {
        ValidateDeletionPath(Path);
        var live = ReadIdentityUsing(Path, IsDirectory, Files);
        if (live.Identity != Identity || live.Volume != Volume)
            throw new InvalidOperationException("This item has been replaced since confirmation. Rescan before deleting it.");
    }

    /// <summary>Permanently removes this item after explicit manual confirmation.</summary>
    public void DeleteManually(CancellationToken token, Action? mutationStarted)
        => Files.DeleteUserConfirmedEntry(ValidateDeletionPath(Path), IsDirectory, Identity, Volume, token, mutationStarted);

    public IDisposable HoldFileRecyclePath(CancellationToken token, out string shellPath)
    {
        if (IsDirectory) throw new InvalidOperationException("Select an individual file.");
        return Files.HoldFileRecyclePath(Path, Identity, Volume, token, out shellPath);
    }

    // Check only the exact destination reported by Windows. No Recycle Bin
    // traversal is needed, and a successful callback cannot substitute a child
    // or unrelated recycled item for the confirmed target.
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

        var live = ReadIdentityUsing(recycledPath, IsDirectory, Files);
        if (live.Identity != Identity || live.Volume != Volume)
            throw new IOException("Windows did not recycle the confirmed item. Recheck its location before trying again.");
        try { Files.GetAttributes(Path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        throw new IOException("The original item is still present. Recheck its location before trying again.");
    }

    internal static string ValidateDeletionPath(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith("\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Choose a resolved file or folder on a local drive.");
        // Reject names that the Shell could normalize to a different target.
        if (path.Length < 3 || path[1] != ':' || path[2] is not ('\\' or '/'))
            throw new InvalidOperationException("This is not a local drive path.");
        var segments = path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ')
            || s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidOperationException("Windows could interpret this name as a different path.");
        string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (segments.Length == 0)
            throw new InvalidOperationException("Drive roots cannot be deleted.");
        if (segments[0].StartsWith('$') || segments[0].Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("Recycler", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Filesystem metadata and Recycle Bin storage cannot be deleted here.");
        return full;
    }

    private protected static unsafe (FileIdentity Identity, ulong Volume) ReadIdentityUsing(string path, bool directory, DiskAccess files)
    {
        // Reject reparse ancestors as well as reparse leaves. A lexical path
        // must not redirect the confirmed item onto another location.
        CheckAttributes(files.GetAttributes(path), directory);
        for (string? part = System.IO.Path.GetDirectoryName(path); part is not null; part = System.IO.Path.GetDirectoryName(part))
            CheckAttributes(files.GetAttributes(part), directory: true);

        const uint openNoRecall = 0x00100000;
        using var handle = files.OpenHandle(NativeMethods.ExtendedPath(path), 0x80, 7, 0, 3,
            NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint | openNoRecall, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        byte* attributes = stackalloc byte[8];
        if (!files.ReadHandleInformation(handle, 9, attributes, 8)) // FileAttributeTagInfo
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        CheckAttributes((FileAttributes)LayoutParser.U32(new ReadOnlySpan<byte>(attributes, 8), 0), directory);
        byte* buffer = stackalloc byte[24];
        if (!files.ReadHandleInformation(handle, 18, buffer, 24))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not verify this item's identity.");
        var data = new ReadOnlySpan<byte>(buffer, 24);
        var identity = new FileIdentity(LayoutParser.U64(data, 8), LayoutParser.U64(data, 16));
        if (identity == default) throw new InvalidOperationException("Windows did not supply a usable identity for this item.");
        return (identity, LayoutParser.U64(data, 0));
    }

    private static void CheckAttributes(FileAttributes attributes, bool directory)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A linked or reparse path is protected here.");
        if (((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("The selected item has changed type. Rescan before deleting it.");
    }
}
