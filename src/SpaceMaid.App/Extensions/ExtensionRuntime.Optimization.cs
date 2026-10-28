using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App.Extensions;

internal sealed partial class ExtensionRuntime
{
    internal OptimizationDiscovery DiscoverOptimization() => OptimizationCatalog.Discover(_loaded);
    // A failing extension activity getter cannot authorize competing work.
    internal bool HasOptimizationWork
    {
        get
        {
            foreach (var view in _entries.Select(entry => entry.View).OfType<IOptimizationWorkflowView>())
            {
                try { if (view.IsOptimizationBusy) return true; }
                catch { return true; }
            }
            return false;
        }
    }
}
