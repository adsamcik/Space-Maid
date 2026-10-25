using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

internal static class PhotoViewportChecks
{
    internal static void Check(FrameworkElement loadedView)
    {
        Window? window = null; FrameworkElement? view = null;
        using var lifetime = new CancellationTokenSource();
        using var directory = new TestDirectory();
        // Use the host-loaded extension's types. WPF pack resources cannot
        // identify two same-named view assemblies from different load contexts.
        Type PhotoType(string name) => loadedView.GetType().Assembly.GetType("SpaceMaid.Extensions.Photos." + name)!;
        object Create(string name, params object?[] arguments) => Activator.CreateInstance(PhotoType(name), arguments)!;
        var catalog = Create("PhotoCatalog", directory.Path); using var catalogLifetime = (IDisposable)catalog;
        object? Call(string name, params object?[] arguments) => catalog.GetType().GetMethod(name)!.Invoke(catalog, arguments);
        Call("Initialize");
        try
        {
            long collection = (long)Call("AddCollection", @"G:\photos")!; Call("BeginDiscovery", collection, "one");
            var observations = Array.CreateInstance(PhotoType("PhotoObservation"), 100);
            var updates = Array.CreateInstance(PhotoType("PhotoAnalysisUpdate"), 100);
            for (int i = 0; i < 100; i++)
            {
                observations.SetValue(Create("PhotoObservation", $@"G:\photos\photo-{i:D3}.jpg", "id-" + i,
                    "observed", 3L, 3L, null, 1u, true, "Observed", "Viewing image", "PHOTO-" + i), i);
                updates.SetValue(Create("PhotoAnalysisUpdate", "id-" + i, "observed",
                    Create("MediaRevision", "id-" + i, 3L, 1L, 1L, 0u, 1u), null, null, "Verified", false), i);
            }
            Call("Observe", collection, "one", observations); Call("EndDiscovery", collection, "one", true, "Complete", null);
            Call("StoreAnalysisBatch", updates, CancellationToken.None);
            view = (FrameworkElement)Activator.CreateInstance(loadedView.GetType(), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [new PreviewHost(directory.Path), catalog, lifetime.Token], null)!;
            window = new Window { Content = view, Width = 1020, Height = 820, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            window.Show();
            var gallery = (ListBox)view.FindName("PhotoGallery"); var list = (DataGrid)view.FindName("FileList");
            static bool Requested(object row) => (bool)row.GetType().GetProperty("Requested")!.GetValue(row)!;
            PumpUntil(() => gallery.Items.Count == 100 && list.Items.Cast<object>().Any(Requested));
            var rows = list.Items.Cast<object>().ToArray();
            Assert.True(Requested(rows[0])); Assert.False(Requested(rows[^1]));
            Assert.InRange(rows.Count(Requested), 1, 99);
            var scroll = Descendants(gallery).OfType<ScrollViewer>().First();
            scroll.ScrollToBottom(); window.UpdateLayout();
            PumpUntil(() => Requested(rows[^1]));
            Assert.False(Requested(rows[0])); // Obsolete queued work no longer holds a thumbnail lane.
            Assert.True(scroll.VerticalOffset > 0);
            // Selection also requests a preview when its tile is outside
            // the current viewport; source reads stay deferred in this host.
            list.SelectedItem = rows[50];
            PumpUntil(() => Requested(rows[50]));
            Assert.Same(rows[50], gallery.SelectedItem);
            foreach (var width in new[] { 1020, 1220 })
            {
                window.Width = width; window.UpdateLayout();
                var rendered = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
                for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
                {
                    if (!File.Exists(Path.Join(parent.FullName, "SpaceMaid.slnx"))) continue;
                    var output = Path.Join(parent.FullName, "artifacts", "photo-optimization-views"); Directory.CreateDirectory(output);
                    using var file = File.Create(Path.Join(output, $"gallery-{width}.png")); encoder.Save(file); break;
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            if (view is not null)
            {
                var shutdown = (Task)view.GetType().GetMethod("ShutdownAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
                PumpUntil(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
            }
            window?.Close();
        }
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        while (!complete())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Photo viewport did not settle.");
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(5);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private sealed class PreviewHost(string directory) : IExtensionHost, IExtensionJobs, IToolBroker
    {
        public string DataDirectory => Path.Join(directory, "data");
        public string CacheDirectory => Path.Join(directory, "cache");
        public IInventoryReader Inventory => throw new InvalidOperationException();
        public IExtensionJobs Jobs => this;
        public IToolBroker Tools => this;
        public void Log(string eventName, object data) { }
        public void RevealLocation(string path) { }
        public Task RunAsync(string workflow, JobResource resource, Func<IExtensionJobContext, Task> work, IProgress<ExtensionJobProgress>? progress, CancellationToken token)
            => Task.Delay(Timeout.Infinite, token);
        public Task<string?> ExifToolVersionAsync(CancellationToken token) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<MediaToolMetadata>> ReadExifMetadataAsync(IReadOnlyList<string> paths, CancellationToken token) => throw new InvalidOperationException();
    }
}
