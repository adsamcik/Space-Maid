using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal sealed partial class FileOperations
{
    public Task<OptimizationPreview> PreviewOptimizationAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> findings,
        OptimizationPolicy policy, CancellationToken token = default, int cursor = 0, IReadOnlyList<OptimizationCooldown>? cooldowns = null) => Task.Run(() =>
            OptimizationExecutor.Capture(OptimizationPlanner.Create(scan, findings, policy, token, cursor: cursor, cooldowns: cooldowns), token, policy), token);

    public async Task<OptimizationResult> OptimizeAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> findings, OptimizationPreview preview,
        OptimizationPolicy policy, OpportunityDiscovery discover, CancellationToken token, Action<string, object> diagnostic,
        IProgress<OptimizationFileResult>? progress = null)
    {
        policy = policy.Validate();
        if (!policy.Enabled) throw new InvalidOperationException("Optimization is disabled.");
        var skipped = preview.Skipped.Select(f => new OptimizationFileResult(f.Path, OptimizationOutcome.Skipped, 0, f.Reason)).ToList();
        ArgumentNullException.ThrowIfNull(discover);
        // Producer proofs re-read the exact current output, control records and
        // required inputs under native leases. Do not recursively refresh an
        // arbitrarily large containing cache to authorize a bounded file batch.
        var allowed = await Task.Run(() => discover(scan, token).Where(f => f.AutomaticCleanup is not null
            && !f.RequiresManualReview && !f.IsWorktree && f.CleanupBlockReason.Length == 0)
            .Select(f => (f.Path, Key: f.AutomaticCleanup!.ConsentKey(f.ProviderId))).ToHashSet(), token).ConfigureAwait(false);
        var safe = new List<OptimizationFile>();
        foreach (var file in preview.Files)
        {
            token.ThrowIfCancellationRequested();
            if (allowed.Contains((file.ContainerPath, file.RuleKey)) && file.Evidence?.Disposition == OptimizationDisposition.Eligible) safe.Add(file);
            else skipped.Add(new(file.Path, OptimizationOutcome.Skipped, 0, "File or its detection rule changed after preview; kept."));
        }
        diagnostic("optimization-proof-validation", new { Files = safe.Count, Skipped = skipped.Count,
            preview.ExaminedEntries, preview.CoverageComplete, preview.NextCursor });
        var result = await Task.Run(() => OptimizationExecutor.Execute(preview with { Files = safe.AsReadOnly() }, policy, token, progress));
        return result with { Files = Array.AsReadOnly(skipped.Concat(result.Files).ToArray()) };
    }
}
