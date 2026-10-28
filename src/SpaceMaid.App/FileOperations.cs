using System.Diagnostics;
using System.IO;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

// Shared host entrypoint. Backend availability is evaluated per request; only
// metadata uses NTFS. Shell and provider operations retain their live guards.
internal sealed partial class FileOperations(Func<INtfsLayoutSource?>? ntfsSource = null, RecycleBinService? recycleBins = null)
{
    private readonly RecycleBinService _recycleBins = recycleBins ?? new();

    private INtfsLayoutSource? MetadataSource() => ntfsSource?.Invoke()
        ?? (Elevation.IsAdministrator ? new NativeNtfsLayoutSource() : null);

    public Task<ScanSnapshot> ScanAsync(string path, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default, Action<ScanDiagnostic>? diagnostic = null,
        string? fastUnavailableReason = null)
    {
        var source = MetadataSource();
        return new VolumeScanner(source).ScanAsync(path, progress, cancellationToken,
            preferFast: source is not null, diagnostic: diagnostic, fastUnavailableReason: fastUnavailableReason);
    }

    public Task<ScanSnapshot> MeasureFolderAsync(string path, CancellationToken token = default,
        Action<string, object>? diagnostic = null)
        => new VolumeScanner(MetadataSource()).MeasureFolderAsync(path, token, diagnostic);

    public Task<ScanSnapshot> RecheckAsync(ScanSnapshot scan, IEnumerable<ScanRecheckRequest> requests,
        CancellationToken token = default, Action<string, object>? diagnostic = null)
        => ScanRechecker.RefreshAsync(scan, requests, token, diagnostic, MetadataSource());

    public Task<OpportunityReviewValidation> ReviewCleanupAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> selected,
        CancellationToken token = default, Action<string, object>? diagnostic = null, OpportunityDiscovery? discover = null,
        IProgress<OpportunityValidationProgress>? progress = null)
        => OpportunityReviewValidator.ValidateAsync(scan, selected, token, diagnostic, discover, progress, MetadataSource());

    public async Task<FolderDeletionTarget> PrepareRecycleAsync(ScanSnapshot scan, int link, CancellationToken token,
        Action<string, object>? diagnostic = null)
    {
        // Keep the selected identity, but obtain confirmation sizes from just
        // this folder. Unrelated stale inventory does not prevent recycling.
        var selected = await Task.Run(() => FolderDeletionTarget.FromScan(scan, link), token);
        var current = await MeasureFolderAsync(selected.Path, token, diagnostic);
        return await Task.Run(() =>
        {
            selected.ValidateCurrent();
            return FolderDeletionTarget.FromScan(current, -1);
        }, token);
    }

    public Task<RecycleResult> RecycleAsync(FolderDeletionTarget target, nint owner, CancellationToken token,
        Action<string, object>? diagnostic = null) => FolderRecycler.RecycleAsync(target, owner, token, diagnostic);

    public Task<CleanupBatchResult> ExecuteCleanupAsync(CleanupPlan plan, nint owner,
        IProgress<CleanupProgress> progress, CancellationToken token, Action<string, object> diagnostic,
        IReadOnlyDictionary<string, WorktreeCleanupPolicy>? checkedWorktrees = null, OpportunityDiscovery? discover = null)
        => CleanupBatch.ExecuteAsync(plan, owner, progress, token, diagnostic, checkedWorktrees, discover, this);

    public Task<CloudFileState?> InspectCloudAsync(string path, CancellationToken token = default)
        => Task.Run(() => { token.ThrowIfCancellationRequested(); return CloudFiles.Inspect(path); }, token);

    public Task<OnlineOnlyPlan> PrepareOnlineOnlyAsync(ScanSnapshot scan, int link, CancellationToken token)
        => Task.Run(() => CloudFiles.Prepare(scan, link, token), token);

    // Execute reports partial cancellation, so it must run even if the token
    // was cancelled before scheduling, rather than returning a cancelled Task.
    public Task<OnlineOnlyResult> MakeOnlineOnlyAsync(OnlineOnlyPlan plan, IProgress<OnlineOnlyProgress>? progress,
        CancellationToken token) => Task.Run(() => CloudFiles.Execute(plan, progress, token));

    public Task<RecycleBinSnapshot> QueryRecycleBinAsync(string root) => _recycleBins.QueryAsync(root);

    public Task<RecycleBinCleanupResult> EmptyRecycleBinAsync(RecycleBinSnapshot confirmed, nint owner,
        CancellationToken token, Action<string, object>? diagnostic = null)
        => _recycleBins.EmptyAsync(confirmed, owner, token, diagnostic);

    public string OpenInExplorer(string path)
    {
        path = Path.GetFullPath(path);
        bool folder = Directory.Exists(path);
        if (!folder && !File.Exists(path)) throw new FileNotFoundException("This location is no longer available.", path);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(folder ? path : "/select," + path);
        using var process = Process.Start(start);
        return folder ? path : Path.GetDirectoryName(path)!;
    }
}
