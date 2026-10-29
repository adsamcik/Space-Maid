using SpaceMaid.Core.IO;
using System.Diagnostics;
using System.IO;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

// Shared host entrypoint. Backend availability is evaluated per request; only
// metadata uses NTFS. Shell and provider operations retain their live guards.
internal sealed partial class FileOperations(Func<INtfsLayoutSource?>? ntfsSource = null, RecycleBinService? recycleBins = null,
    Func<ScanSnapshot?>? inventory = null)
{
    private readonly RecycleBinService _recycleBins = recycleBins ?? new();
    private readonly Dictionary<string, WeakReference<ScanSnapshot>> _measurements = new(StringComparer.Ordinal);
    private readonly object _measurementGate = new();
    private static readonly INtfsLayoutSource NativeSource = new NativeNtfsLayoutSource();
    private readonly DiskAccess _disk = new(() => ntfsSource?.Invoke() ?? NativeSource);

    public Task<ScanSnapshot> ScanAsync(string path, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default, Action<ScanDiagnostic>? diagnostic = null,
        string? fastUnavailableReason = null)
        => _disk.ScanAsync(path, progress, cancellationToken, diagnostic, fastUnavailableReason);

    public async Task<ScanSnapshot> MeasureFolderAsync(string path, CancellationToken token = default,
        Action<string, object>? diagnostic = null, ScanSnapshot? previous = null)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        lock (_measurementGate)
            if (_measurements.TryGetValue(path, out var cached) && cached.TryGetTarget(out var snapshot)
                && (previous is null || snapshot.ScannedAtUtc >= previous.ScannedAtUtc)) previous = snapshot;
        previous ??= inventory?.Invoke();
        var measured = await _disk.MeasureFolderAsync(path, token, diagnostic, previous).ConfigureAwait(false);
        lock (_measurementGate)
        {
            // Do not retain a second large inventory for each extension/app.
            if (_measurements.Count >= 64) _measurements.Remove(_measurements.Keys.First());
            _measurements[path] = new(measured);
        }
        return measured;
    }

    public Task<ScanSnapshot> RecheckAsync(ScanSnapshot scan, IEnumerable<ScanRecheckRequest> requests,
        CancellationToken token = default, Action<string, object>? diagnostic = null)
        => _disk.RecheckAsync(scan, requests, token, diagnostic);

    public Task<OpportunityReviewValidation> ReviewCleanupAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> selected,
        CancellationToken token = default, Action<string, object>? diagnostic = null, OpportunityDiscovery? discover = null,
        IProgress<OpportunityValidationProgress>? progress = null)
        => _disk.ReviewCleanupAsync(scan, selected, token, diagnostic, discover, progress);

    public async Task<FolderDeletionTarget> PrepareRecycleAsync(ScanSnapshot scan, int link, CancellationToken token,
        Action<string, object>? diagnostic = null)
    {
        // Keep the selected identity, but obtain confirmation sizes from just
        // this folder. Unrelated stale inventory does not prevent recycling.
        var selected = await Task.Run(() => FolderDeletionTarget.FromScan(scan, link), token);
        var current = await MeasureFolderAsync(selected.Path, token, diagnostic, scan);
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

    public Task<OnlineOnlyPlan> PrepareOnlineOnlyAsync(string path, CancellationToken token)
        => Task.Run(() => CloudFiles.Prepare(path, token), token);

    public Task<CloudDirectoryListing> ReadCloudDirectoryAsync(string path, CancellationToken token)
        => Task.Run(() => CloudFiles.ReadDirectory(path, token), token);

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
        bool folder = DiskAccess.Default.DirectoryExists(path);
        if (!folder && !DiskAccess.Default.FileExists(path)) throw new FileNotFoundException("This location is no longer available.", path);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(folder ? path : "/select," + path);
        using var process = Process.Start(start);
        return folder ? path : Path.GetDirectoryName(path)!;
    }
}
