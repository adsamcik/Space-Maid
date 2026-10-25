using System.Windows;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Containers;

public sealed class ContainersExtension : ISpaceMaidExtension
{
    private ContainersView? _view;
    private readonly CancellationTokenSource _lifetime = new();
    public ExtensionDescriptor Descriptor => new("space-maid.containers", "Containers & WSL", "1.0.0");
    public Task InitializeAsync(IExtensionHost host, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public FrameworkElement CreateView() => _view ??= new ContainersView(_lifetime.Token);
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_view is not null) await _view.ShutdownAsync();
        _lifetime.Dispose();
    }
}
