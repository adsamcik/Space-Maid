using System.Windows;
using SpaceMaid.Core.IO;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Opportunities;

public sealed class OpportunitiesExtension : ISpaceMaidExtension
{
    private IOpportunityBrowserHost? _host;
    private DiskAccess? _files;
    private OpportunitiesView? _view;
    public ExtensionDescriptor Descriptor => new("space-maid.opportunities", "Opportunities", "1.0.0");
    public Task InitializeAsync(IExtensionHost host, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _host = (host as IOpportunityBrowserServices)?.OpportunityBrowser
            ?? throw new InvalidOperationException("This host does not provide the Opportunities browser.");
        _files = host.Files;
        return Task.CompletedTask;
    }
    public FrameworkElement CreateView() => _view ??= new(_host ?? throw new InvalidOperationException("Initialize the extension first."), _files!);
    public ValueTask DisposeAsync() { _view?.Dispose(); _view = null; return ValueTask.CompletedTask; }
}
