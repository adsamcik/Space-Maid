using System.Windows;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Cloud;

public sealed class CloudExtension : ISpaceMaidExtension
{
    private IExtensionHost? _host;
    private CloudView? _view;
    private readonly CancellationTokenSource _lifetime = new();
    public ExtensionDescriptor Descriptor => new("space-maid.cloud", "Cloud drives", "1.0.0");
    public Task InitializeAsync(IExtensionHost host, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _host = host;
        return Task.CompletedTask;
    }
    public FrameworkElement CreateView() => _view ??= new CloudView(
        _host ?? throw new InvalidOperationException("Initialize Cloud drives first."), _lifetime.Token);
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_view is not null) await _view.ShutdownAsync();
        _lifetime.Dispose();
    }
}
