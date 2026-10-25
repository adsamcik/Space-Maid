using System.ComponentModel;

namespace SpaceMaid.Core.Optimization;

public static class OptimizationExecutor
{
    // Read-only capture gives even a logical-only inventory exact native file
    // identities before review. Execution may narrow this list, never recapture.
    public static OptimizationPreview Capture(OptimizationPreview preview, CancellationToken token = default, OptimizationPolicy? policy = null)
    {
        var files = new List<OptimizationFile>(); var skipped = preview.Skipped.ToList(); int next = preview.NextCursor;
        var limits = (policy ?? new()).Validate();
        var budget = new ProofBudget(limits.MaxProofBytesPerRun, TimeSpan.FromSeconds(limits.MaxInspectionSeconds));
        for (int index = 0; index < preview.Files.Count; index++)
        {
            var file = preview.Files[index];
            token.ThrowIfCancellationRequested();
            try { files.Add(RegenerableFileDeleter.Capture(file, budget, token, limits.KeepRecentDays)); }
            catch (Exception error) when (Expected(error)) { skipped.Add(new(file.Path, error.Message)); }
            if (budget.Exhausted)
            {
                // Progress past evidence that cannot fit this page. A later
                // sweep may retry it, without starving later candidates.
                // If this was the last candidate, retain the planner's cursor
                // instead of restarting an already inspected tail of the scan.
                if (index + 1 < preview.Files.Count && file.ScanRecordIndex >= 0) next = file.ScanRecordIndex + 1;
                break;
            }
        }
        return preview with { Files = files.AsReadOnly(), Skipped = skipped.AsReadOnly(), EstimatedBytes = files.Sum(f => f.EstimatedBytes),
            NextCursor = next, CoverageComplete = preview.CoverageComplete && !budget.Exhausted, Limited = preview.Limited || budget.Exhausted };
    }

    public static OptimizationResult Execute(OptimizationPreview preview, OptimizationPolicy policy, CancellationToken token = default,
        IProgress<OptimizationFileResult>? progress = null)
    {
        policy = policy.Validate();
        if (!policy.Enabled) throw new InvalidOperationException("Automatic optimization is disabled.");
        var results = new List<OptimizationFileResult>(); long budget = 0;
        var proofBudget = new ProofBudget(policy.MaxProofBytesPerRun, TimeSpan.FromSeconds(policy.MaxInspectionSeconds));
        foreach (var file in preview.Files)
        {
            if (token.IsCancellationRequested) break;
            OptimizationFileResult result;
            long cost = Math.Max(file.LogicalBytes, file.EstimatedBytes);
            if (results.Count >= policy.MaxFilesPerRun || cost < 0 || cost > policy.MaxBytesPerRun - budget)
                result = new(file.Path, OptimizationOutcome.Skipped, 0, "Run limit reached; kept until the next check.");
            else
            {
                budget += cost;
                try
                {
                    RegenerableFileDeleter.Delete(file, policy, token, proofBudget);
                    bool receiptRemoved = file.Rule.ResourceKind == GeneratedResourceKind.OwnedCache
                        && file.Inputs.FirstOrDefault(i => i.Path.Equals(GeneratedFileOwnership.ReceiptPath(file.Path), StringComparison.OrdinalIgnoreCase)) is { } receipt
                        && GeneratedFileOwnership.TryRemoveReceiptAfterDeletion(file.Path, file.ProviderId, receipt);
                    result = new(file.Path, OptimizationOutcome.Deleted, file.LogicalBytes,
                        receiptRemoved ? "Generated file and its exact ownership receipt permanently deleted." : "Generated file permanently deleted.");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception error) when (Expected(error))
                { result = new(file.Path, error is Win32Exception { NativeErrorCode: 32 or 33 } or InvalidOperationException
                        or FileNotFoundException or DirectoryNotFoundException ? OptimizationOutcome.Skipped : OptimizationOutcome.Failed, 0, error.Message)
                    { Cooldown = error is Win32Exception { NativeErrorCode: 32 or 33 }
                        ? new(file.Path, file.LastWriteFileTime, file.CreationFileTime, DateTime.UtcNow.AddHours(2)) : null }; }
            }
            results.Add(result); progress?.Report(result);
        }
        return new(results.AsReadOnly(), token.IsCancellationRequested);
    }

    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException
        or ArgumentException or System.Text.Json.JsonException or System.Xml.XmlException or FormatException;
}
