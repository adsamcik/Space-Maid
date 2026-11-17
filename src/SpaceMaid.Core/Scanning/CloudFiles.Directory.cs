using SpaceMaid.Core.IO;
using System.ComponentModel;
using System.IO.Enumeration;

namespace SpaceMaid.Core.Scanning;

public sealed record CloudDirectoryEntry(string Path, string Name, bool IsDirectory, long LogicalBytes,
    long? LocalBytes, string Availability, bool CanRequestOnlineOnly);
public sealed record CloudDirectoryListing(string Path, IReadOnlyList<CloudDirectoryEntry> Entries,
    bool Complete, IReadOnlyList<string> Warnings)
{
    public CloudDirectoryContinuation? Continuation { get; init; }
    public bool PageComplete { get; init; } = true;
}

// Opaque read-only cursor. It carries no authority to mutate a file.
public sealed class CloudDirectoryContinuation
{
    internal string Path { get; }
    internal string Version { get; }
    internal string AfterName { get; }
    internal CloudDirectoryContinuation(string path, string version, string afterName)
    { Path = path; Version = version; AfterName = afterName; }
}

public static partial class CloudFiles
{
    private const int DirectoryEntryLimit = 5000;

    // Names and default-stream lengths come from directory metadata. Opening
    // READ_ATTRIBUTES handles inspects residency without requesting contents.
    public static CloudDirectoryListing ReadDirectory(string path, CancellationToken token)
        => ReadDirectoryPage(path, null, token);

    public static CloudDirectoryListing ReadDirectoryPage(string path, CloudDirectoryContinuation? continuation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        path = NormalizeLocalPath(path);
        ValidateAncestors(path);
        using var directory = Open(path);
        if ((LayoutParser.U32(Information(directory, 9, 8), 0) & (uint)FileAttributes.Directory) == 0)
            throw new IOException("Choose a cloud drive folder.");
        string Version() => Convert.ToHexString(Information(directory, 18, 24))
            + Convert.ToHexString(Information(directory, 0, 40).AsSpan(16, 16)); // Last-write and change times.
        string? version;
        try { version = Version(); }
        catch (Win32Exception error) when (error.NativeErrorCode is 1 or 50 or 87)
        { version = null; } // Browsing still works when this filesystem cannot supply a safe cursor.
        if (continuation is not null && (!path.Equals(continuation.Path, StringComparison.Ordinal)
            || version != continuation.Version))
            throw new IOException("The folder changed since the previous page. Existing items were kept; refresh the folder before loading more.");
        var rootInspection = InspectHandleDetailed(directory);
        var selected = rootInspection.State;
        var entries = new List<CloudDirectoryEntry>();
        var warnings = new List<string>();
        bool complete = true;
        if (rootInspection.Status == CloudInspectionStatus.Unavailable)
            RecordFailure("Cloud availability for this folder is unavailable; online-only requests are disabled. Refresh to retry. " + rootInspection.Error);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        var children = DiskAccess.Default.EnumerateEntries<DirectoryCandidate>(path,
            (ref FileSystemEntry entry) => new(entry.ToFullPath(), entry.FileName.ToString(), entry.IsDirectory,
                entry.IsDirectory ? 0 : entry.Length), options);
        var candidates = new SortedSet<DirectoryCandidate>(Comparer<DirectoryCandidate>.Create((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name)));
        bool enumerationComplete = true;
        try
        {
            foreach (var child in children)
            {
                token.ThrowIfCancellationRequested();
                if (continuation is not null && StringComparer.Ordinal.Compare(child.Name, continuation.AfterName) <= 0) continue;
                candidates.Add(child);
                if (candidates.Count > DirectoryEntryLimit + 1) candidates.Remove(candidates.Max!);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            enumerationComplete = false;
            RecordFailure("This folder could not be read completely: " + ex.Message);
        }
        bool more = candidates.Count > DirectoryEntryLimit;
        if (more) candidates.Remove(candidates.Max!);
        foreach (var child in candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (HasUnsupportedLink(child.Path))
                {
                    entries.Add(new(child.Path, child.Name, child.Directory, child.Length, null, "Linked location · not followed", false));
                    continue;
                }
                var inspection = InspectDetailed(child.Path);
                if (inspection.Status == CloudInspectionStatus.Unavailable)
                {
                    entries.Add(new(child.Path, child.Name, child.Directory, child.Length, null, "Unavailable · refresh to retry", false));
                    RecordFailure($"{child.Name}: Cloud availability could not be inspected. Refresh to retry. {inspection.Error}");
                    continue;
                }
                var state = inspection.State;
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
            catch (Exception ex) when (InspectionFailure(ex))
            {
                entries.Add(new(child.Path, child.Name, child.Directory, child.Length, null, "Unavailable · refresh to retry", false));
                RecordFailure($"{child.Name}: Cloud availability could not be inspected. Refresh to retry. {ex.Message}");
            }
        }
        token.ThrowIfCancellationRequested();
        ValidateAncestors(path);
        if (version is not null)
        {
            using var currentDirectory = Open(path);
            if (!Information(currentDirectory, 18, 24).AsSpan().SequenceEqual(Information(directory, 18, 24)))
                throw new IOException("The folder was replaced while reading this page. Existing items were kept; refresh to retry.");
            if (Version() != version)
                throw new IOException("The folder changed while reading this page. Existing items were kept; refresh the folder to retry.");
        }
        bool pageComplete = complete;
        CloudDirectoryContinuation? next = more && enumerationComplete && version is not null
            ? new(path, version, candidates.Max!.Name) : null;
        if (more) RecordFailure(next is not null ? "5,000 items loaded in this page. Load more to inspect and filter the remaining names."
            : "5,000 items loaded. Windows could not establish safe continuation; open this folder in Explorer to inspect the remaining names.");
        entries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            int order = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return order != 0 ? order : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });
        return new(path, entries.AsReadOnly(), complete, warnings.AsReadOnly()) { Continuation = next, PageComplete = pageComplete };

        void RecordFailure(string warning)
        {
            complete = false;
            if (warnings.Count < 8 && !warnings.Contains(warning)) warnings.Add(warning);
        }
    }

    private sealed record DirectoryCandidate(string Path, string Name, bool Directory, long Length);

    private static string NormalizeLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Choose an available local cloud drive folder.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
