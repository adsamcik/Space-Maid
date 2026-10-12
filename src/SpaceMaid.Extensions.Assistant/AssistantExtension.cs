using System.Windows;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Assistant;

public sealed class AssistantExtension : ISpaceMaidExtension
{
    private IExtensionHost? _host;
    private AssistantView? _view;
    private readonly CancellationTokenSource _lifetime = new();
    public ExtensionDescriptor Descriptor => new("space-maid.assistant", "Local assistant", "1.0.0");
    public Task InitializeAsync(IExtensionHost host, CancellationToken token)
    { token.ThrowIfCancellationRequested(); _host = host; return Task.CompletedTask; }
    public FrameworkElement CreateView() => _view ??= new(_host ?? throw new InvalidOperationException("Initialize the assistant first."), _lifetime.Token);
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_view is not null) await _view.ShutdownAsync();
        _lifetime.Dispose();
    }
}
