using System.Windows;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Apps;

public sealed class AppsExtension : ISpaceMaidExtension
{
    private IExtensionHost? _host;
    private AppsView? _view;
    private readonly CancellationTokenSource _lifetime = new();
    public ExtensionDescriptor Descriptor => new("space-maid.apps", "Apps", "1.0.0");
    public Task InitializeAsync(IExtensionHost host, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _host = host;
        return Task.CompletedTask;
    }
    public FrameworkElement CreateView() => _view ??= new AppsView(
        _host ?? throw new InvalidOperationException("Apps must be initialized before creating its view."), _lifetime.Token);
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_view is not null) await _view.ShutdownAsync();
        _lifetime.Dispose();
    }
}
