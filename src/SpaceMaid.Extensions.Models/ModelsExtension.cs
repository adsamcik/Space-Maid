using System.Windows;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Models;

public sealed class ModelsExtension : ISpaceMaidExtension, IOptimizationExtension
{
    private ModelsView? _view;
    private readonly CancellationTokenSource _lifetime = new();
    public ExtensionDescriptor Descriptor => new("space-maid.models", "AI models", "1.0.0");
    public Task InitializeAsync(IExtensionHost host, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public FrameworkElement CreateView() => _view ??= new ModelsView(_lifetime.Token);
    public IReadOnlyList<ExtensionOptimizationAction> GetOptimizationActions() =>
    [new("ollama", "Downloaded Ollama models", "Inspect models through the local Ollama service and review individual removals.", "Removed models need downloading again. Shared model data means size is not guaranteed recovery.", Workflow: "ollama")];
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_view is not null) await _view.ShutdownAsync();
        _lifetime.Dispose();
    }
}
