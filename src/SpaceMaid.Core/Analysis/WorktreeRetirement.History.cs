namespace SpaceMaid.Core.Analysis;

public sealed partial class WorktreeRetirement
{
    // Policy removal never removes the checkout or its recovery information.
    public Task UnmanageAsync(string path, CancellationToken token = default)
        => CleanupMutationLease.RunAsync(() => UnmanageCapturedAsync(path, token), token);

    private async Task UnmanageCapturedAsync(string path, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            string normalized = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            var state = Load();
            if (state.Records.Any(record => record.Policy.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                && !TerminalHistory(record.State)))
                throw new InvalidOperationException("This checkout has unfinished recovery. Restore or reconcile it before stopping management; automatic retirement can be paused meanwhile.");
            if (!state.Policies.Any(policy => policy.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This checkout is no longer managed. Refresh its policy.");
            Save(state with { Policies = state.Policies.Where(policy => !policy.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase)).ToArray() });
        }
        finally { _gate.Release(); }
    }

    // Explicitly discard only a terminal receipt. Git branches, retained refs,
    // registration and both recorded filesystem locations remain untouched.
    public Task RemoveHistoryAsync(Guid id, CancellationToken token = default)
        => CleanupMutationLease.RunAsync(() => RemoveHistoryCapturedAsync(id, token), token);

    private async Task RemoveHistoryCapturedAsync(Guid id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var state = Load();
            var record = state.Records.SingleOrDefault(record => record.Id == id)
                ?? throw new InvalidOperationException("This recovery record no longer exists. Refresh records.");
            if (!TerminalHistory(record.State))
                throw new InvalidOperationException("Only completed or restored history can be removed. Unfinished recovery records must stay.");
            Save(state with { Records = state.Records.Where(record => record.Id != id).ToArray() });
        }
        finally { _gate.Release(); }
    }

    private static bool TerminalHistory(WorktreeRetirementState state)
        => state is WorktreeRetirementState.Completed or WorktreeRetirementState.Restored;
}
