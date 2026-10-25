using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal sealed partial class FileOperations
{
    public Task<OptimizationPreview> PreviewOptimizationAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> findings,
        OptimizationPolicy policy, CancellationToken token = default) => Task.Run(() =>
            OptimizationExecutor.Capture(OptimizationPlanner.Create(scan, findings, policy, token), token), token);

    public async Task<OptimizationResult> OptimizeAsync(ScanSnapshot scan, IReadOnlyList<Opportunity> findings, OptimizationPreview preview,
        OptimizationPolicy policy, OpportunityDiscovery discover, CancellationToken token, Action<string, object> diagnostic,
        IProgress<OptimizationFileResult>? progress = null)
    {
        policy = policy.Validate();
        if (!policy.Enabled) throw new InvalidOperationException("Optimization is disabled.");
        var skipped = preview.Skipped.Select(f => new OptimizationFileResult(f.Path, OptimizationOutcome.Skipped, 0, f.Reason)).ToList();
        var containers = preview.Files.Select(f => f.ContainerPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = findings.Where(f => containers.Contains(f.Path) && f.AutomaticCleanup is not null).ToArray();
        if (selected.Length == 0) return new(skipped.AsReadOnly(), token.IsCancellationRequested);
        var current = await ReviewCleanupAsync(scan, selected, token, diagnostic, discover).ConfigureAwait(false);
        var validated = current.ValidatedLocations.Select(f => (f.ProviderId, f.Path, f.RuleId)).ToHashSet();
        var live = await Task.Run(() => OptimizationPlanner.Create(current.Snapshot, current.Findings, policy, token), token).ConfigureAwait(false);
        var allowed = live.Files.ToDictionary(f => (f.Path, f.RuleKey));
        var safe = new List<OptimizationFile>();
        foreach (var file in preview.Files)
        {
            token.ThrowIfCancellationRequested();
            bool containerValid = selected.Any(f => f.Path == file.ContainerPath && validated.Contains((f.ProviderId, f.Path, f.RuleId)));
            if (containerValid && allowed.TryGetValue((file.Path, file.RuleKey), out var fresh)
                && file.LogicalBytes == fresh.LogicalBytes && file.LastWriteFileTime == fresh.LastWriteFileTime
                && file.CreationFileTime == fresh.CreationFileTime && (fresh.Identity is null || file.Identity == fresh.Identity)) safe.Add(file);
            else skipped.Add(new(file.Path, OptimizationOutcome.Skipped, 0, "File or its detection rule changed after preview; kept."));
        }
        // Fresh generated-file eligibility and activity checks cannot expand a
        // preview's authorization. The original captured identities survive.
        var result = await Task.Run(() => OptimizationExecutor.Execute(preview with { Files = safe.AsReadOnly() }, policy, token, progress));
        return result with { Files = Array.AsReadOnly(skipped.Concat(result.Files).ToArray()) };
    }
}
