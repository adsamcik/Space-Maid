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
    internal DiskAccess Files => _disk;

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
        var selected = await CaptureRecycleTargetAsync(scan, link, token);
        var current = await MeasureFolderAsync(selected.Path, token, diagnostic, scan);
        return await Task.Run(() =>
        {
            selected.ValidateCurrent();
            return FolderDeletionTarget.FromScan(current, -1, Files);
        }, token);
    }

    internal Task<FolderDeletionTarget> CaptureRecycleTargetAsync(ScanSnapshot scan, int link, CancellationToken token)
        => Task.Run(() => FolderDeletionTarget.FromScan(scan, link, Files), token);

    public Task<RecycleResult> RecycleAsync(FolderDeletionTarget target, nint owner, CancellationToken token,
        Action<string, object>? diagnostic = null) => FolderRecycler.RecycleAsync(target, owner, token, diagnostic);

    internal Task<RecycleResult> RetryRecycleWithAdministratorAsync(FolderDeletionTarget target, nint owner,
        CancellationToken token, Action<string, object>? diagnostic = null)
        => FolderRecycler.RetryWithAdministratorAsync(target, owner, token, diagnostic);

    internal async Task<RecycleResult> RecycleWithRecoveryAsync(FolderDeletionTarget target, nint owner,
        CancellationToken token, Action<string, object> diagnostic, Func<FolderDeletionTarget, CancellationToken, Task<bool>>? confirmRetry,
        Func<Task> recheck, Action? validateScope, Func<FolderDeletionTarget, CancellationToken, Task<bool>>? resolveLocks)
        => await RecycleWithRepairRecoveryAsync(target, owner, token, diagnostic, confirmRetry, recheck, validateScope, resolveLocks, null)
            .ConfigureAwait(false);

    internal async Task<RecycleResult> RecycleWithRepairRecoveryAsync(FolderDeletionTarget target, nint owner,
        CancellationToken token, Action<string, object> diagnostic, Func<FolderDeletionTarget, CancellationToken, Task<bool>>? confirmRetry,
        Func<Task> recheck, Action? validateScope, Func<FolderDeletionTarget, CancellationToken, Task<bool>>? resolveLocks,
        Func<FolderDeletionTarget, RecyclePermissionTarget, CancellationToken, Task<bool>>? confirmRepair)
    {
        bool administrator = false, retriedLocks = false;
        Action? scopeCheck = null;
        while (true)
        {
            try
            {
                return await (administrator
                    ? FolderRecycler.RetryWithAdministratorAsync(target, owner, token, diagnostic, scopeCheck)
                    : FolderRecycler.RecycleAsync(target, owner, token, diagnostic, scopeCheck)).ConfigureAwait(false);
            }
            catch (FolderRecycler.PermissionException) when (!administrator && confirmRetry is not null)
            {
                token.ThrowIfCancellationRequested();
                await Task.Run(target.ValidateCurrent, token).ConfigureAwait(false);
                if (!await confirmRetry(target, token).ConfigureAwait(false)) throw;
                administrator = true;
            }
            catch (FolderRecycler.RepairablePermissionException error) when (confirmRepair is not null)
            {
                token.ThrowIfCancellationRequested();
                var item = await Task.Run(() => Files.CaptureRecyclePermissionTarget(target.ExportCapturedIdentity(), error.ItemPath), token)
                    .ConfigureAwait(false);
                if (!await confirmRepair(target, item, token).ConfigureAwait(false)) throw;
                await recheck().ConfigureAwait(false);
                validateScope?.Invoke();
                return await RepairAndRecycleAsync(target, item, owner, token, diagnostic, validateScope, recheck).ConfigureAwait(false);
            }
            catch (FolderRecycler.SharingException) when (!retriedLocks && resolveLocks is not null)
            {
                token.ThrowIfCancellationRequested();
                if (!await resolveLocks(target, token).ConfigureAwait(false)) throw;
                // App closure authorizes a normal retry. If that retry also
                // needs elevation, ask again for the new Shell attempt.
                administrator = false;
                retriedLocks = true;
            }
            token.ThrowIfCancellationRequested();
            await recheck().ConfigureAwait(false);
            scopeCheck = validateScope;
        }
    }

    internal Task<RecycleResult> RepairAndRecycleAsync(FolderDeletionTarget target, RecyclePermissionTarget item,
        nint owner, CancellationToken token, Action<string, object> diagnostic, Action? validateScope = null, Func<Task>? recheck = null)
        => CleanupMutationLease.RunAsync(async () =>
        {
            var folder = target.ExportCapturedIdentity();
            void ValidateExactItem()
            {
                if (Files.CaptureRecyclePermissionTarget(folder, item.Path) != item)
                    throw new IOException("The item changed after confirmation.");
                validateScope?.Invoke();
            }
            ValidateExactItem();
            token.ThrowIfCancellationRequested();
            var reply = await RecyclePermissionClient.RepairAsync(Files, new(folder, item), token).ConfigureAwait(false);
            if (!reply.Applied)
            {
                if (reply.Error is { } error) throw new IOException(error);
                return new RecycleResult(true, null);
            }
            diagnostic("recycle-permission-repaired", new { item.Path });
            try
            {
                if (token.IsCancellationRequested)
                    throw new IOException("Recycling was cancelled before the move.");
                if (recheck is not null) await recheck().ConfigureAwait(false);
                var result = await FolderRecycler.RecycleAsync(target, owner, token, diagnostic, ValidateExactItem).ConfigureAwait(false);
                if (result.Cancelled)
                    throw new IOException("Recycling stopped before Windows confirmed the move.");
                return result;
            }
            catch (Exception error) when (CleanupBatch.IsExpected(error))
            {
                throw new IOException("Ownership and permissions were changed. The Recycle Bin move was not confirmed. " + error.Message, error);
            }
        }, token);

    public Task<CleanupBatchResult> ExecuteCleanupAsync(CleanupPlan plan, nint owner,
        IProgress<CleanupProgress> progress, CancellationToken token, Action<string, object> diagnostic,
        IReadOnlyDictionary<string, WorktreeCleanupPolicy>? checkedWorktrees = null, OpportunityDiscovery? discover = null)
        => token.IsCancellationRequested ? CleanupBatch.ExecuteAsync(plan, owner, progress, token, diagnostic, checkedWorktrees, discover, this)
            : CleanupMutationLease.RunAsync(() => CleanupBatch.ExecuteAsync(plan, owner, progress, token, diagnostic, checkedWorktrees, discover, this), token);

    public Task<CloudFileState?> InspectCloudAsync(string path, CancellationToken token = default)
        => Task.Run(() => { token.ThrowIfCancellationRequested(); return CloudFiles.Inspect(path, Files); }, token);

    public Task<OnlineOnlyPlan> PrepareOnlineOnlyAsync(ScanSnapshot scan, int link, CancellationToken token)
        => Task.Run(() => CloudFiles.Prepare(scan, link, token, Files), token);

    public Task<OnlineOnlyPlan> PrepareOnlineOnlyAsync(string path, CancellationToken token)
        => Task.Run(() => CloudFiles.Prepare(path, token, Files), token);

    public Task<CloudDirectoryListing> ReadCloudDirectoryAsync(string path, CancellationToken token)
        => Task.Run(() => CloudFiles.ReadDirectory(path, token, Files), token);

    public Task<CloudFolderUsage> MeasureCloudFolderUsageAsync(string path, CancellationToken token)
        => Task.Run(() => CloudFiles.MeasureUsage(path, Files, token), token);

    // Execute reports partial cancellation, so it must run even if the token
    // was cancelled before scheduling, rather than returning a cancelled Task.
    public Task<OnlineOnlyResult> MakeOnlineOnlyAsync(OnlineOnlyPlan plan, IProgress<OnlineOnlyProgress>? progress,
        CancellationToken token) => token.IsCancellationRequested ? Task.Run(() => CloudFiles.Execute(plan, progress, token, Files))
            : CleanupMutationLease.RunAsync(() => Task.Run(() => CloudFiles.Execute(plan, progress, token, Files)), token);

    public Task<RecycleBinSnapshot> QueryRecycleBinAsync(string root) => _recycleBins.QueryAsync(root);
    public Task<bool> HasRecycleLeftoversAsync(RecycleBinSnapshot confirmed, CancellationToken token)
        => _recycleBins.HasLeftoversAsync(Files, confirmed, token);
    public DiskAccess.RecycleLeftoverReview ReviewRecycleLeftovers(RecycleBinSnapshot confirmed, CancellationToken token)
        => _recycleBins.ReviewLeftovers(Files, confirmed, token);

    public Task DeleteRecycleLeftoversAsync(RecycleBinSnapshot confirmed, DiskAccess.RecycleLeftoverReview review,
        IReadOnlyList<DiskAccess.RecycleLeftover> approved, IProgress<DiskAccess.RecycleLeftoverResult> results, CancellationToken token)
        => CleanupMutationLease.RunAsync(() => Task.Run(() => _recycleBins.DeleteLeftovers(confirmed, review, approved, results, token), token), token);

    public Task<RecycleBinCleanupResult> EmptyRecycleBinAsync(RecycleBinSnapshot confirmed, nint owner,
        CancellationToken token, Action<string, object>? diagnostic = null)
        => CleanupMutationLease.RunAsync(() => _recycleBins.EmptyAsync(confirmed, owner, token, diagnostic), token);

    public string OpenInExplorer(string path)
    {
        path = Path.GetFullPath(path);
        bool folder = _disk.DirectoryExists(path);
        if (!folder && !_disk.FileExists(path)) throw new FileNotFoundException("This location is no longer available.", path);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(folder ? path : "/select," + path);
        using var process = Process.Start(start);
        return folder ? path : Path.GetDirectoryName(path)!;
    }
}
