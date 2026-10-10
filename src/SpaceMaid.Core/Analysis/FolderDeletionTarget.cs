using System.ComponentModel;
using System.Runtime.InteropServices;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

// A single, resolved directory from a completed scan. This does not enable
// opportunity cleanup or expose a permanent-deletion path.
public sealed class FolderDeletionTarget
{
    public string Path { get; }
    public long LogicalBytes { get; }
    public long AllocatedBytes { get; }
    public bool HasAllocationData { get; }
    public bool IsIncomplete { get; }
    private readonly FileIdentity _identity;
    private readonly ulong _volume;

    private FolderDeletionTarget(string path, ScanEntry entry, bool allocation, FileIdentity identity, ulong volume)
    {
        Path = path; LogicalBytes = entry.LogicalBytes; AllocatedBytes = entry.AllocatedBytes;
        HasAllocationData = allocation; IsIncomplete = entry.IsIncomplete;
        _identity = identity; _volume = volume;
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
        var live = ReadIdentity(Path);
        if (live.Identity != _identity || live.Volume != _volume)
            throw new InvalidOperationException("This folder has been replaced since confirmation. Rescan before deleting it.");
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

    private static unsafe (FileIdentity Identity, ulong Volume) ReadIdentity(string path)
    {
        // Reject a reparse ancestor too: lexical paths must not redirect the
        // confirmed location onto a different drive or protected namespace.
        for (string? part = path; part is not null; part = System.IO.Path.GetDirectoryName(part))
        {
            var attributes = File.GetAttributes(part);
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("The selected folder is no longer a directory.");
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A linked or reparse path cannot be recycled here.");
        }
        using var handle = NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), 0x80, 7, 0, 3,
            NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        byte* buffer = stackalloc byte[24];
        if (!NativeMethods.GetFileInformationByHandleEx(handle, 18, buffer, 24))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not verify this folder's identity. Deletion was blocked.");
        var data = new ReadOnlySpan<byte>(buffer, 24);
        var identity = new FileIdentity(LayoutParser.U64(data, 8), LayoutParser.U64(data, 16));
        if (identity == default) throw new InvalidOperationException("Windows did not supply a usable folder identity. Deletion was blocked.");
        return (identity, LayoutParser.U64(data, 0));
    }
}
