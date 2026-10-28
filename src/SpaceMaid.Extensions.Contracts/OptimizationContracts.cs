using SpaceMaid.Core.Optimization;

namespace SpaceMaid.Extensions.Contracts;

// Optional, metadata-only contributions. Reading declarations must not inspect
// tools, scan files or start work. The host owns fixed native adapters; owning
// views retain their own review/confirmation for other manual workflows.
public sealed record ExtensionOptimizationAction(string Id, string Name, string Description, string Consequence,
    ToolCacheKind? NativeTool = null, string? Workflow = null);

public interface IOptimizationExtension
{
    IReadOnlyList<ExtensionOptimizationAction> GetOptimizationActions();
}

public interface IOptimizationWorkflowView
{
    // Includes nested/hidden native workflows. Read on the UI thread.
    bool IsOptimizationBusy { get; }
    // Navigation only; must not inspect or mutate. Reject unknown IDs or busy views.
    bool OpenOptimizationWorkflow(string workflowId);
}
