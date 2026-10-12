using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private async Task<EvidenceCleanupReviewResult> ReviewAssistantEvidenceAsync(IInventoryEvidenceSession session,
        IReadOnlyList<EvidenceId> items, CancellationToken token)
    {
        if (!Dispatcher.CheckAccess())
            return await Dispatcher.InvokeAsync(() => ReviewAssistantEvidenceAsync(session, items, token)).Task.Unwrap();
        token.ThrowIfCancellationRequested();
        if (_closing || _work is not null || _scanStale || _snapshot is null)
            throw new InvalidOperationException("Finish the current work and refresh the scan before reviewing suggestions.");
        var capturedScan = _snapshot;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in items) paths.Add((await session.DetailsAsync(id, token)).Path);
        if (_closing || _work is not null || _scanStale || !ReferenceEquals(_snapshot, capturedScan))
            throw new InvalidOperationException("The scan changed before review. Refresh the suggestions.");
        // Map to already discovered host-owned rules, never synthesize an
        // Opportunity or deletion target from a model's text/path.
        var matching = _rows.Where(row => paths.Contains(row.Item.Path)).ToArray();
        var unmatched = paths.Except(matching.Select(row => row.Item.Path), StringComparer.Ordinal).ToArray();
        if (matching.Length == 0) return new(0, unmatched, "No existing cleanup rule covers these folders. Keep them or inspect their contents in Explore.");
        var before = _rows.Where(row => row.Selected).ToDictionary(row => row.SelectionIdentity,
            row => capturedScan.GetMetadata(capturedScan.PrimaryLink(row.Item.DirectoryIndex)));
        _changingOpportunitySelection = true;
        try { foreach (var row in _rows) row.Selected = paths.Contains(row.Item.Path); }
        finally { _changingOpportunitySelection = false; }
        UpdateSelection();
        try
        {
            token.ThrowIfCancellationRequested();
            var review = ReviewSelectionAsync();
            var reviewWork = _work;
            using var cancellation = token.Register(() =>
            {
                try { reviewWork?.Cancel(); } catch (ObjectDisposedException) { }
            });
            await review;
            return new(matching.Length, unmatched, "Host review: " + StatusText.Text
                + (unmatched.Length == 0 ? "" : $" {unmatched.Length} folders have no existing cleanup rule."));
        }
        finally
        {
            _changingOpportunitySelection = true;
            try
            {
                foreach (var row in _rows)
                {
                    bool retained = before.TryGetValue(row.SelectionIdentity, out var previous);
                    if (retained && !ReferenceEquals(_snapshot, capturedScan))
                    {
                        var current = _snapshot?.GetMetadata(_snapshot.PrimaryLink(row.Item.DirectoryIndex));
                        retained = previous.NativeIdLow is not null && current?.NativeIdLow == previous.NativeIdLow
                            && current?.NativeIdHigh == previous.NativeIdHigh;
                    }
                    row.Selected = retained;
                }
            }
            finally { _changingOpportunitySelection = false; }
            UpdateSelection();
        }
    }
}
