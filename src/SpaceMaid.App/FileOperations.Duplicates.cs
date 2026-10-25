using SpaceMaid.Core.Deduplication;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal sealed partial class FileOperations
{
    public Task<DuplicateSearchResult> FindDuplicatesAsync(ScanSnapshot scan, DuplicateSearchPolicy policy,
        IProgress<DuplicateSearchProgress>? progress, CancellationToken token) => DuplicateFinder.FindAsync(scan, policy, progress, token);
    public Task<DuplicateSearchResult> ContinueDuplicatesAsync(DuplicateSearchSession session,
        IProgress<DuplicateSearchProgress>? progress, CancellationToken token) => session.ContinueAsync(progress, token);

    public Task<DuplicateFile> InspectBuildStorageAsync(string path, CancellationToken token) => StoragePreservation.InspectBuildFileAsync(path, token);

    public async Task<StoragePreservationResult> PreserveStorageAsync(DuplicateFile file, StoragePreservationAction action,
        DuplicateFile? keeper, CancellationToken token, Action<string, object> diagnostic)
    {
        diagnostic("storage-preservation-start", new { file.Path, file.Revision, Action = action.ToString(), Keeper = keeper?.Path });
        var result = action == StoragePreservationAction.ShareBlocks
            ? await StoragePreservation.ShareBlocksAsync(keeper ?? throw new InvalidOperationException("A verified independent keeper is required."), file, token)
            : await StoragePreservation.CompressAsync(file, action == StoragePreservationAction.Decompress, token);
        diagnostic("storage-preservation-complete", result);
        return result;
    }
}
