using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private string? _explorerRecheckPath;
    private bool _explorerReturnPending;
    private readonly HashSet<ScanRecheckRequest> _staleScopes = [];

    private ScanLog StartActionLog(string action, string path)
    {
        var log = new ScanLog(LogDirectory);
        _scanLogPath = log.FilePath;
        OpenLogButton.ToolTip = log.FilePath;
        OpenLogButton.Visibility = log.FilePath is null ? Visibility.Collapsed : Visibility.Visible;
        log.Record("file-action-start", new { Action = action, Path = path });
        return log;
    }

    internal async Task RecheckFolderAsync(string path)
    {
        if (_snapshot is not { } scan || !CanUseFolderMenu(scan)) return;
        using var work = new CancellationTokenSource();
        using var log = StartActionLog("recheck", path);
        _work = work; SetBusy(true);
        try { await RefreshAffectedAsync(scan, [new(NearestAvailableFolder(scan, path))], "Folder rechecked.", work.Token, log); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or Win32Exception
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            log.Record("recheck-failed", new { Error = ex.ToString() });
            if (!_closing) MarkScanStale(ex is OperationCanceledException ? "Recheck cancelled. Results remain out of date." : $"Recheck could not finish: {ex.Message}", [new(path)]);
        }
        finally { _work = null; if (!_closing) { ShowActionLogError(log); SetBusy(false); } }
    }

    private static string NearestAvailableFolder(ScanSnapshot scan, string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (scan.FindEntry(current) is { } link && scan.Entry(link).IsDirectory && Directory.Exists(current)) return current;
        throw new IOException("The scanned location is no longer available. Scan its drive again.");
    }

    private async Task RefreshAffectedAsync(ScanSnapshot scan, IReadOnlyList<ScanRecheckRequest> requests,
        string note, CancellationToken token, ScanLog log, ScanSnapshot? prechecked = null,
        IReadOnlyList<Opportunity>? findings = null)
    {
        var selectedOpportunities = _rows.Where(row => row.Selected).Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
        string? detailPath = (OpportunityGrid.SelectedItem as OpportunityRow)?.Path;
        StatusText.Text = prechecked is null ? "Rechecking affected metadata…" : "Updating checked results…";
        foreach (var request in requests) _staleScopes.Add(request);
        var dirty = _staleScopes.ToArray();
        var timer = Stopwatch.StartNew();
        var updated = prechecked ?? await ScanRechecker.RefreshAsync(scan, requests, token, log.Record);
        var analysis = await Task.Run(() =>
        {
            var opportunities = findings ?? OpportunityDetector.Find(updated, token);
            return (Opportunities: opportunities, Total: CleanupPlanner.Create(updated, opportunities, token), Groups: OpportunityGrouping.Build(updated, opportunities, token));
        }, token);
        token.ThrowIfCancellationRequested();
        if (_closing || !ReferenceEquals(_snapshot, scan)) return;
        string[] expanded = _explorer?.ExpandedPaths() ?? [];
        string selected = scan.EntryPath(_selectedLink) ?? scan.RootPath;
        bool grouped = _selectionIsGroup;
        var column = _treeSortColumn; bool descending = _treeDescending;
        // Publish a coherent inventory; finish the bounded UI restore even if
        // cancellation arrives after the metadata/analysis has completed.
        var explorer = await PrepareExplorerRefreshAsync(updated, analysis.Opportunities,
            expanded, selected, column, descending, CancellationToken.None);
        if (_closing || !ReferenceEquals(_snapshot, scan)) return;
        var viewport = CaptureTreeViewport();
        PublishExplorerRefresh(explorer, column, descending, grouped);
        _staleScopes.UnionWith(dirty);
        PublishOpportunities(updated, analysis.Opportunities, analysis.Groups, selectedOpportunities, detailPath, preserveScope: true);
        OpportunitiesTab.Content = $"Opportunities · {_rows.Count:N0}";
        CandidateText.Text = SizeText.Format(analysis.Total.EstimatedBytes);
        CandidateCaption.Text = $"Estimate · {_rows.Count:N0} locations · overlapping paths counted once";
        ScannedSizeLabel.Text = "Scanned";
        ScannedSizeText.Text = SizeText.Format(updated.AllocatedBytes);
        ScannedSizeCaption.Text = "Partial refresh · Other folders retain earlier scan data";
        TreeNote.Text = $"Rechecked at {updated.LastRecheckUtc!.Value.ToLocalTime():HH:mm:ss} · Other folders retain earlier scan data";
        MapBasis.Text = "Area = allocated size · Partial refresh";
        try { var drive = new DriveInfo(updated.RootPath); ShowDriveSpace(drive.TotalSize, drive.AvailableFreeSpace); }
        catch (IOException) { }
        _staleScopes.RemoveWhere(scope => requests.Any(request =>
            scope.Path.Equals(request.Path, StringComparison.Ordinal) && (!scope.Recursive || request.Recursive)
            || request.Recursive && scope.Path.StartsWith(request.Path.EndsWith(Path.DirectorySeparatorChar)
                ? request.Path : request.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
        _scanStale = _staleScopes.Count > 0;
        _scanStatus = note + $" Updated {updated.RecheckedPaths.Count:N0} locations in {timer.Elapsed.TotalSeconds:0.00}s; other folders retain earlier scan data.";
        StatusText.Text = _scanStatus;
        log.Record("recheck-published", new { ElapsedMs = timer.Elapsed.TotalMilliseconds, updated.RecheckedPaths, updated.AllocatedBytes, updated.LogicalBytes, Opportunities = _rows.Count });
        if (_scanStale) MarkScanStale(note + " Other affected locations remain out of date. Recheck them or rescan.");
        UpdateSelection();
        RestoreTreeViewport(viewport);
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        if (_explorerRecheckPath is null) return;
        _explorerReturnPending = true;
        DrainExplorerRecheck();
    }

    private async void DrainExplorerRecheck()
    {
        if (!_explorerReturnPending) return;
        if (_explorerRecheckPath is not { } path || _snapshot is not { } scan || !CanUseFolderMenu(scan)) return;
        _explorerRecheckPath = null; _explorerReturnPending = false;
        await RecheckFolderAsync(path);
    }

    private void ShowActionLogError(ScanLog log)
    {
        if (log.Error is null) return;
        BackendStatusText.Visibility = Visibility.Visible;
        BackendStatusText.Text += $" · Logging unavailable: {log.Error}";
    }
}
