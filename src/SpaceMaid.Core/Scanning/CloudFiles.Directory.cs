using System.ComponentModel;
using System.IO.Enumeration;

namespace SpaceMaid.Core.Scanning;

public sealed record CloudDirectoryEntry(string Path, string Name, bool IsDirectory, long LogicalBytes,
    long? LocalBytes, string Availability, bool CanRequestOnlineOnly);
public sealed record CloudDirectoryListing(string Path, IReadOnlyList<CloudDirectoryEntry> Entries,
    bool Complete, IReadOnlyList<string> Warnings);

public static partial class CloudFiles
{
    private const int DirectoryEntryLimit = 5000;

    // Names and default-stream lengths come from directory metadata. Opening
    // READ_ATTRIBUTES handles inspects residency without requesting contents.
    public static CloudDirectoryListing ReadDirectory(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        path = NormalizeLocalPath(path);
        ValidateAncestors(path);
        using var directory = Open(path);
        if ((LayoutParser.U32(Information(directory, 9, 8), 0) & (uint)FileAttributes.Directory) == 0)
            throw new IOException("Choose a cloud drive folder.");
        var selected = Inspect(directory);
        var entries = new List<CloudDirectoryEntry>();
        var warnings = new List<string>();
        bool complete = true;
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        var children = new FileSystemEnumerable<(string Path, string Name, bool Directory, long Length)>(path,
            (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.FileName.ToString(), entry.IsDirectory,
                entry.IsDirectory ? 0 : entry.Length), options);
        try
        {
            foreach (var child in children)
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count == DirectoryEntryLimit)
                {
                    RecordFailure("This folder has more than 5,000 items. Open a smaller folder in Explorer to inspect the remaining items.");
                    break;
                }
                try
                {
                    if (HasUnsupportedLink(child.Path))
                    {
                        entries.Add(new(child.Path, child.Name, child.Directory, child.Length, null, "Linked location · not followed", false));
                        continue;
                    }
                    var state = Inspect(child.Path);
                    bool sameRoot = selected is not null && state is not null
                        && state.SyncRootId == selected.SyncRootId && state.Volume == selected.Volume;
                    string availability = state is null ? "Managed by provider"
                        : state.IsDirectory ? "Folder"
                        : !state.InSync || state.ModifiedBytes > 0 ? "Waiting for sync"
                        : state.PinState == 3 ? "Excluded by provider"
                        : state.PinState == 1 ? "Kept offline"
                        : state.LocalBytes == 0 ? "Online-only" : "Available locally";
                    entries.Add(new(child.Path, child.Name, child.Directory, child.Length,
                        state is { IsDirectory: false } ? state.LocalBytes : null,
                        availability, sameRoot && (state!.IsDirectory || state.CanRequestOnlineOnly)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    entries.Add(new(child.Path, child.Name, child.Directory, child.Length, null, "Unavailable · refresh to retry", false));
                    RecordFailure("Some items could not be inspected. Refresh the folder to retry.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            RecordFailure("This folder could not be read completely: " + ex.Message);
        }
        token.ThrowIfCancellationRequested();
        entries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            int order = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return order != 0 ? order : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });
        return new(path, entries.AsReadOnly(), complete, warnings.AsReadOnly());

        void RecordFailure(string warning)
        {
            complete = false;
            if (warnings.Count < 8 && !warnings.Contains(warning)) warnings.Add(warning);
        }
    }

    private static string NormalizeLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Choose an available local cloud drive folder.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
