using System.IO;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;

namespace SpaceMaid.App;

internal readonly record struct MetadataItem(string Path, string Name, FileAttributes Attributes, long LogicalBytes, long LastWriteFileTime);
internal sealed record MetadataBatch(DateTime ObservedAtUtc, bool Complete, IReadOnlyList<string> Warnings,
    IReadOnlyList<MetadataItem> Items);

internal sealed partial class FileOperations
{
    internal const int MetadataBatchSize = 512;

    // Fresh extension discovery requires only names/attributes/default-stream
    // lengths. Stream native directory metadata without content reads or a
    // second drive-sized inventory; allocation measurements use MeasureFolder.
    public async IAsyncEnumerable<MetadataBatch> ReadMetadataAsync(string root,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        ValidateLocalAncestors(root);
        var observed = DateTime.UtcNow;
        var errors = new List<string>();
        bool complete = true;
        var folders = new Queue<string>(); folders.Enqueue(root);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        IEnumerator<MetadataItem>? active = null;
        try
        {
            while (folders.Count > 0 || active is not null)
            {
                var items = new List<MetadataItem>(MetadataBatchSize);
                await Task.Run(() =>
                {
                    while (items.Count < MetadataBatchSize && (folders.Count > 0 || active is not null))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (active is null)
                            {
                                string folder = folders.Dequeue(); ValidateLocalAncestors(folder);
                                active = new FileSystemEnumerable<MetadataItem>(folder,
                                    (ref FileSystemEntry value) => new(value.ToFullPath(), value.FileName.ToString(), value.Attributes,
                                        value.IsDirectory ? 0 : value.Length, value.LastWriteTimeUtc.ToFileTime()), options).GetEnumerator();
                            }
                            if (!active.MoveNext()) { active.Dispose(); active = null; continue; }
                            var value = active.Current;
                            items.Add(value);
                            if ((value.Attributes & FileAttributes.Directory) != 0)
                            {
                                if ((value.Attributes & FileAttributes.ReparsePoint) == 0) folders.Enqueue(value.Path);
                                else RecordFailure("A reparse folder was left metadata-only and its contents were not followed.");
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            active?.Dispose(); active = null;
                            RecordFailure("A folder was unavailable or could not be read: " + ex.Message);
                        }
                    }
                }, token).ConfigureAwait(false);
                if (items.Count > 0) yield return new(observed, false, errors.ToArray(), items.AsReadOnly());
            }
        }
        finally { active?.Dispose(); }
        token.ThrowIfCancellationRequested();
        yield return new(observed, complete, errors.ToArray(), []);
        void RecordFailure(string message) { complete = false; if (errors.Count < 32) errors.Add(message); }
    }

    internal static void ValidateLocalAncestors(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Choose an available local collection folder.");
        string? current = path;
        while (current is not null)
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.ReparsePoint | (FileAttributes)(0x1000 | 0x40000 | 0x400000))) != 0)
                throw new IOException("Reparse collection folders are metadata-only and are not followed. Choose a regular local collection folder.");
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
    }
}
