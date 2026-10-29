using SpaceMaid.Core.IO;

namespace SpaceMaid.App;

internal sealed partial class FileOperations
{
    internal const int MetadataBatchSize = DiskAccess.MetadataBatchSize;
    public IAsyncEnumerable<MetadataBatch> ReadMetadataAsync(string root, CancellationToken token = default)
        => _disk.ReadMetadataAsync(root, token);
    internal static void ValidateLocalAncestors(string path) => DiskAccess.ValidateLocalAncestors(path);
}
