using System.Windows;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Agents;

public sealed class AgentsExtension : ISpaceMaidExtension, IOptimizationExtension
{
    private AgentsView? _view;
    private readonly CancellationTokenSource _lifetime = new();
    public ExtensionDescriptor Descriptor => new("space-maid.agents", "Agent cleanup", "1.0.0");
    public Task InitializeAsync(IExtensionHost host, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public FrameworkElement CreateView() => _view ??= new AgentsView(_lifetime.Token);
    public IReadOnlyList<ExtensionOptimizationAction> GetOptimizationActions() =>
    [
        new("history", "Agent history and logs", "Review old transcripts and logs with source-specific activity checks.", "Conversations are personal data. Review selected items before moving eligible files to the Recycle Bin.", Workflow: "history"),
        new("opencode", "OpenCode sessions", "Inspect sessions through a local OpenCode server and review individual removals.", "Session deletion is permanent. Active and uncertain sessions remain protected.", Workflow: "opencode")
    ];
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_view is not null) await _view.ShutdownAsync();
        _lifetime.Dispose();
    }
}
