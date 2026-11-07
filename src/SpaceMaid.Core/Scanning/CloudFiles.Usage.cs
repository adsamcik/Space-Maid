using System.Buffers;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Core.IO;

namespace SpaceMaid.Core.Scanning;

public sealed record CloudUsageTotals(long LogicalBytes, long? AllocatedBytes, bool Complete);
public sealed record CloudFolderUsage(CloudUsageTotals Total, IReadOnlyDictionary<string, CloudUsageTotals> Entries,
    IReadOnlyList<string> Warnings);

public static partial class CloudFiles
{
    internal const int UsageEntryLimit = 250_000;
    private const int UsageDepthLimit = 128;

    // Attribute and directory queries only. A provider may populate missing
    // names while listing a directory. We never open a file's data stream.
    public static CloudFolderUsage MeasureUsage(string path, DiskAccess files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        path = NormalizeLocalPath(path);
        var entries = new Dictionary<string, CloudUsageTotals>(StringComparer.Ordinal);
        var warnings = new List<string>();
        const string limitWarning = "Reached the 250,000-item limit. Open a smaller folder and measure again.";
        var ancestors = new Stack<string>();
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            ancestors.Push(parent);
        var leases = new Stack<SafeFileHandle>();
        int remaining = UsageEntryLimit;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(DiskAccess.DirectoryRecordBufferSize);
        try
        {
            foreach (string ancestor in ancestors)
            {
                token.ThrowIfCancellationRequested();
                // A traversal lease keeps ancestors from being renamed without
                // requiring permission to list their unrelated children.
                SafeFileHandle lease;
                try { lease = Open(ancestor, files, access: 0xA0, flags: 0x00100000); } // FILE_TRAVERSE | READ_ATTRIBUTES
                catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 32 or 33)
                { throw new IOException("Windows could not hold a parent folder open for measurement: " + ancestor, ex); }
                leases.Push(lease);
                ValidateDirectory(lease, default);
            }
            using var root = OpenUsageDirectory(path);
            bool mayNeedPopulation = ValidateDirectory(root, default);
            var total = ReadFolder(root, path, 0, mayNeedPopulation);
            token.ThrowIfCancellationRequested();
            return new(total, entries, warnings.AsReadOnly());
        }
        finally
        {
            while (leases.TryPop(out var lease)) lease.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }

        CloudUsageTotals ReadFolder(SafeFileHandle handle, string folder, int depth, bool mayNeedPopulation)
        {
            var directories = new List<(string Path, FileIdentity Id)>();
            long logical = 0;
            long? allocated = 0;
            bool complete = true;
            bool limited = false;
            try
            {
                token.ThrowIfCancellationRequested();
                int error = files.ReadFirstDirectoryRecords(handle, buffer, out bool extended);
                while (error == 0)
                {
                    int offset = 0;
                    while (offset >= 0)
                    {
                        var item = new DiskAccess.DirectoryRecord(buffer, ref offset, extended);
                        if (item.Name.SequenceEqual(".") || item.Name.SequenceEqual("..")) continue;
                        if (remaining == 0) { MarkLimit(); break; }
                        remaining--;
                        Add(item.Name, item.Attributes, item.Tag, item.Identity, item.LogicalBytes, item.AllocatedBytes);
                    }
                    if (limited) break;
                    token.ThrowIfCancellationRequested();
                    error = files.ReadDirectoryRecords(handle, buffer, extended);
                }
                if (error != 0 && error != NativeMethods.NoMoreFiles) throw new Win32Exception(error);
                if (mayNeedPopulation && ValidateDirectory(handle, default))
                {
                    complete = false;
                    Warn("Some folders are only partly listed by the provider. Check the provider and try again.");
                }
            }
            catch (Exception ex) when (IsUsageReadFailure(ex))
            {
                complete = false;
                Warn("Some folder contents could not be read. Check the provider and try again.");
            }
            foreach (var child in directories)
            {
                token.ThrowIfCancellationRequested();
                if (remaining == 0)
                {
                    MarkLimit();
                    break;
                }
                try
                {
                    using var directory = OpenUsageDirectory(child.Path);
                    bool childMayNeedPopulation = ValidateDirectory(directory, child.Id);
                    var total = ReadFolder(directory, child.Path, depth + 1, childMayNeedPopulation);
                    (logical, allocated) = checked((logical + total.LogicalBytes, allocated + total.AllocatedBytes));
                    complete &= total.Complete;
                    if (depth == 0) entries[child.Path] = total;
                }
                catch (Exception ex) when (IsUsageReadFailure(ex))
                {
                    complete = false;
                    Warn("Some folder contents could not be read. Check the provider and try again.");
                }
            }
            return new(logical, allocated, complete);

            void MarkLimit()
            {
                limited = true;
                complete = false;
                Warn(limitWarning);
            }

            void Add(ReadOnlySpan<char> name, FileAttributes attributes, uint tag, FileIdentity id, long size, long allocation)
            {
                token.ThrowIfCancellationRequested();
                if (name.IsEmpty || name.IndexOfAny('\\', '/', ':') >= 0 || name.Contains('\0'))
                    throw new InvalidDataException("Invalid child name in directory metadata.");
                if (IsUnsupportedLink((uint)attributes, tag))
                {
                    complete = false;
                    Warn("Linked locations were skipped.");
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (depth == UsageDepthLimit)
                    {
                        complete = false;
                        Warn("Very deep folders were skipped. Open a deeper folder and measure again.");
                    }
                    else directories.Add((Path.Join(folder, name), id));
                }
                else
                {
                    if (size < 0) throw new InvalidDataException("Invalid file size in directory metadata.");
                    long? reportedAllocation = allocation < 0 ? null : allocation;
                    if (reportedAllocation is null) Warn("Allocation is unavailable for some files.");
                    (logical, allocated) = checked((logical + size, allocated + reportedAllocation));
                    if (depth == 0) entries[Path.Join(folder, name)] = new(size, reportedAllocation, true);
                }
            }
        }

        SafeFileHandle OpenUsageDirectory(string folder) => Open(folder, files, access: 0x81, flags: 0x00100000); // OPEN_NO_RECALL

        bool ValidateDirectory(SafeFileHandle handle, FileIdentity expected)
        {
            var tags = Information(handle, 9, 8, files);
            uint attributes = LayoutParser.U32(tags, 0);
            uint tag = LayoutParser.U32(tags, 4);
            if ((attributes & (uint)FileAttributes.Directory) == 0 || IsUnsupportedLink(attributes, tag))
                throw new IOException("Choose a folder that does not redirect to a linked location.");
            if (expected != default)
            {
                var id = Information(handle, 18, 24, files);
                if (new FileIdentity(LayoutParser.U64(id, 8), LayoutParser.U64(id, 16)) != expected)
                    throw new IOException("A folder changed during measurement.");
            }
            const uint recallOnDataAccess = 0x00400000, placeholderPartial = 0x10;
            return (attributes & recallOnDataAccess) != 0 ||
                (attributes & (uint)FileAttributes.ReparsePoint) != 0 &&
                (CfGetPlaceholderStateFromAttributeTag(attributes, tag) & placeholderPartial) != 0;
        }

        void Warn(string warning)
        {
            if (!warnings.Contains(warning)) warnings.Add(warning);
        }
    }

    private static bool IsUsageReadFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException or OverflowException;
}
