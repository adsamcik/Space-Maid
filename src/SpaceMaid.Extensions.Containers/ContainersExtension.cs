using System.Windows;
using SpaceMaid.Core.IO;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Containers;

public sealed class ContainersExtension : ISpaceMaidExtension, IOptimizationExtension
{
    private ContainersView? _view;
    private DiskAccess? _files;
    private readonly CancellationTokenSource _lifetime = new();
    public ExtensionDescriptor Descriptor => new("space-maid.containers", "Containers & WSL", "1.0.0");
    public Task InitializeAsync(IExtensionHost host, CancellationToken token)
    { token.ThrowIfCancellationRequested(); _files = host.Files; return Task.CompletedTask; }
    public FrameworkElement CreateView() => _view ??= _files is { } files
        ? new ContainersView(_lifetime.Token, files) : new ContainersView(_lifetime.Token);
    public IReadOnlyList<ExtensionOptimizationAction> GetOptimizationActions() =>
    [
        new("docker", "Docker storage", "Inspect images, stopped containers, volumes and build cache using the local Docker engine.", "Review exact objects before removal. Volumes may contain personal data; shared image sizes are estimates.", Workflow: "docker"),
        new("wsl", "WSL virtual disks", "Review and compact a selected WSL virtual disk using Windows tooling.", "Requires a backup and explicit review. Compaction may require stopping WSL and administrator access.", Workflow: "wsl")
    ];
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_view is not null) await _view.ShutdownAsync();
        _lifetime.Dispose();
    }
}
