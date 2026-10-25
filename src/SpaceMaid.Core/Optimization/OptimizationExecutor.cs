using System.ComponentModel;

namespace SpaceMaid.Core.Optimization;

public static class OptimizationExecutor
{
    // Read-only capture gives even a logical-only inventory exact native file
    // identities before review. Execution may narrow this list, never recapture.
    public static OptimizationPreview Capture(OptimizationPreview preview, CancellationToken token = default)
    {
        var files = new List<OptimizationFile>(); var skipped = preview.Skipped.ToList();
        foreach (var file in preview.Files)
        {
            token.ThrowIfCancellationRequested();
            try { files.Add(RegenerableFileDeleter.Capture(file)); }
            catch (Exception error) when (Expected(error)) { skipped.Add(new(file.Path, error.Message)); }
        }
        return preview with { Files = files.AsReadOnly(), Skipped = skipped.AsReadOnly(), EstimatedBytes = files.Sum(f => f.EstimatedBytes) };
    }

    public static OptimizationResult Execute(OptimizationPreview preview, OptimizationPolicy policy, CancellationToken token = default,
        IProgress<OptimizationFileResult>? progress = null)
    {
        policy = policy.Validate();
        if (!policy.Enabled) throw new InvalidOperationException("Automatic optimization is disabled.");
        var results = new List<OptimizationFileResult>(); long budget = 0;
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
                    RegenerableFileDeleter.Delete(file, policy, token);
                    result = new(file.Path, OptimizationOutcome.Deleted, file.LogicalBytes, "Generated file permanently deleted.");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception error) when (Expected(error))
                { result = new(file.Path, error is Win32Exception { NativeErrorCode: 32 or 33 } or InvalidOperationException
                        or FileNotFoundException or DirectoryNotFoundException ? OptimizationOutcome.Skipped : OptimizationOutcome.Failed, 0, error.Message); }
            }
            results.Add(result); progress?.Report(result);
        }
        return new(results.AsReadOnly(), token.IsCancellationRequested);
    }

    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException;
}
