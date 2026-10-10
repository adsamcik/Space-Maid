using System.Windows;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Photos;

public sealed class PhotosExtension : ISpaceMaidExtension
{
    private IExtensionHost? _host;
    private PhotoCatalog? _catalog;
    private PhotosView? _view;
    private readonly CancellationTokenSource _lifetime = new();
    public ExtensionDescriptor Descriptor => new("space-maid.photos", "Photos", "1.0.0");
    public async Task InitializeAsync(IExtensionHost host, CancellationToken token)
    {
        _host = host;
        _catalog = new PhotoCatalog(host.DataDirectory);
        var native = await Task.Run(_catalog.Initialize, token);
        host.Log("photos-catalog-ready", new { ManagedSqlite = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName().Version?.ToString(), NativeSqlite = native, Schema = 1 });
    }
    public FrameworkElement CreateView()
    {
        if (_host is null || _catalog is null) throw new InvalidOperationException("Photos must be initialized before creating its view.");
        return _view ??= new PhotosView(_host, _catalog, _lifetime.Token);
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_view is not null) await _view.ShutdownAsync();
        _catalog?.Dispose(); _lifetime.Dispose();
    }
}
