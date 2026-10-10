using System.IO;
using System.Windows.Interop;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private Task? _cleanupTask;

    private async Task<CleanupBatchResult> ExecuteCleanupAsync(CleanupPlan plan, ScanSnapshot displayed,
        IProgress<CleanupProgress> progress, CancellationToken token, CancellationTokenSource reviewWork, ScanLog log,
        IReadOnlyDictionary<string, WorktreeCleanupPolicy>? checkedWorktrees = null)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token, reviewWork.Token);
        _work = work;
        CleanupBatchResult? result = null;
        var requests = new List<ScanRecheckRequest>();
        try
        {
            log.Record("cleanup-confirmed", new { Locations = plan.Preview.Locations.Select(location => location.Path).ToArray(), Blocked = plan.BlockedLocations.Count });
            result = await CleanupBatch.ExecuteAsync(plan, new WindowInteropHelper(this).Handle, progress, work.Token, log.Record, checkedWorktrees);
            if (_closing || !ReferenceEquals(_snapshot, displayed)) return result;
            if (result.Validation is not { } validation)
            {
                MarkScanStale("Cleanup validation did not complete; no cleanup started. Review again to check selected folders.", plan.Preview.Locations.Select(location => new ScanRecheckRequest(location.Path)));
                return result with { RefreshNote = "Results remain out of date; review again to validate." };
            }
            requests.AddRange(validation.RecheckedScopes);
            foreach (var item in result.Locations.Where(item => item.Attempted))
            {
                string path = item.Location.Path;
                requests.Add(new(Path.GetDirectoryName(path)!, false));
                if (Directory.Exists(path)) requests.Add(new(path));
                string root = Path.GetPathRoot(path)!, recycleRoot = Path.Combine(root, "$Recycle.Bin");
                if (displayed.FindEntry(recycleRoot) is not null || displayed.FindEntry(root) is not null)
                {
                    if (item.Recycle?.RecycledPath is { } destination)
                    {
                        requests.Add(new(recycleRoot, false)); requests.Add(new(Path.GetDirectoryName(destination)!, false));
                        requests.Add(new(destination));
                    }
                    else requests.Add(new(recycleRoot));
                }
            }
            // Deleted paths must not be recursively enumerated. Their live
            // parent check removes the old subtree from the inventory instead.
            requests.RemoveAll(request => !Directory.Exists(request.Path));
            work.Token.ThrowIfCancellationRequested();
            if (validation.CanMergeInventory)
            {
                _staleScopes.UnionWith(validation.UncheckedScopes);
                progress.Report(new(plan.Targets.Count, plan.Targets.Count, "", "Rechecking affected folders…"));
                var updated = result.Locations.Any(item => item.Attempted)
                    ? await ScanRechecker.RefreshAsync(validation.Snapshot, requests, work.Token, log.Record) : validation.Snapshot;
                await RefreshAffectedAsync(displayed, requests, result.Summary, work.Token, log, updated);
                result = result with { RefreshNote = "Affected folders refreshed; other folders retain earlier scan data." };
            }
            else
            {
                MarkScanStale(result.Summary + " Rescan to update the full logical-only inventory.", [new(displayed.RootPath)]);
                try { var drive = new DriveInfo(displayed.RootPath); ShowDriveSpace(drive.TotalSize, drive.AvailableFreeSpace); }
                catch (IOException) { }
                result = result with { RefreshNote = "Full logical-only inventory remains out of date; rescan to refresh it." };
            }
            var uncheck = result.Locations.Where(item => item.Outcome is CleanupOutcome.Recycled or CleanupOutcome.Skipped)
                .Select(item => item.Location.Path).Concat(validation.ExcludedPaths).ToHashSet(StringComparer.Ordinal);
            _changingOpportunitySelection = true;
            try { foreach (var row in _rows) if (uncheck.Contains(row.Path)) row.Selected = false; }
            finally { _changingOpportunitySelection = false; }
            UpdateSelection();
            log.Record("cleanup-complete", new { result.Summary, result.RefreshNote });
            return result;
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        {
            log.Record("cleanup-recheck-failed", new { Error = error.ToString() });
            if (!_closing) MarkScanStale("Cleanup results may be out of date. Recheck affected folders or rescan.",
                requests.Count > 0 ? requests : plan.Preview.Locations.Select(location => new ScanRecheckRequest(Path.GetDirectoryName(location.Path)!, false)));
            if (result is null) throw;
            return result with { RefreshNote = "Refresh did not finish: " + error.Message + " Results remain out of date." };
        }
        finally { _work = reviewWork; }
    }
}
