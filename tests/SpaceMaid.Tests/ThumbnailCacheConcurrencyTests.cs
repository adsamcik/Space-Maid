using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpaceMaid.Extensions.Contracts;
using SpaceMaid.Extensions.Photos;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ThumbnailCacheConcurrencyTests
{
    [Fact]
    public async Task CacheMaintenanceDoesNotBlockRequestAdmissionOrMemoryReads()
    {
        using var directory = new TestDirectory();
        var source = Path.Join(directory.Path, "source.png");
        var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 32 * 4], 32 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(source)) encoder.Save(output);
        MediaRevision revision;
        using (var metadata = NativeMediaFile.Open(source, false)) revision = metadata.Revision;
        var row = new PhotoRow(revision.Identity, source, "source.png", "Viewing image", revision.Length, null,
            revision.Length, revision.Key, true, revision.Links, "Verified", "PNG", 32, 32, 1, null, null, null, "", null, 0)
            { AnalysisCurrent = true };
        using var releaseMaintenance = new ManualResetEventSlim();
        var maintenanceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new MaintenanceHost(directory.Path, () =>
        {
            maintenanceEntered.TrySetResult();
            if (!releaseMaintenance.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release cache maintenance.");
        });
        var cache = new ThumbnailCache(host);
        var first = cache.GetAsync(row, CancellationToken.None);
        Task<PhotoThumbnail>? duplicate = null;
        Task? admission = null;
        try
        {
            await maintenanceEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            admission = Task.Run(() =>
            {
                duplicate = cache.GetAsync(row, CancellationToken.None);
                Assert.Equal(0, cache.MemoryBytes);
            });
            await admission.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseMaintenance.Set();
            if (admission is not null) await admission.WaitAsync(TimeSpan.FromSeconds(10));
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var result = await first;
        Assert.NotNull(result.Image); // A failed disk cache must still show the decoded source.
        Assert.NotNull(duplicate);
        Assert.Same(result.Image, (await duplicate).Image);
        Assert.True(cache.MemoryBytes <= ThumbnailCache.MemoryLimit);
    }

    private sealed class MaintenanceHost(string directory, Action cacheFailure) : IExtensionHost, IExtensionJobs
    {
        public string DataDirectory => Path.Join(directory, "data");
        public string CacheDirectory => Path.Join(directory, "cache");
        public IInventoryReader Inventory => throw new InvalidOperationException();
        public IExtensionJobs Jobs => this;
        public IToolBroker Tools => throw new InvalidOperationException();
        public void RevealLocation(string path) => throw new InvalidOperationException();
        public void Log(string eventName, object data)
        {
            if (eventName == "photos-thumbnail-cache-unavailable") cacheFailure();
        }
        public Task RunAsync(string workflow, JobResource resource, Func<IExtensionJobContext, Task> work,
            IProgress<ExtensionJobProgress>? progress, CancellationToken token) => Task.Run(() => work(new Context(token, () =>
            {
                // After a disk miss, replace only this test's empty cache folder
                // with a file. Initialization then reports a real filesystem
                // failure while cache maintenance is still coordinated.
                var cache = Path.Join(CacheDirectory, "thumbnails-v1");
                Directory.Delete(cache); File.WriteAllText(cache, "Owned cache failure fixture");
            })), token);
    }

    private sealed class Context(CancellationToken token, Action beforeDecode) : IExtensionJobContext
    {
        private int _checkpoints;
        public CancellationToken Cancellation => token;
        public Task CheckpointAsync()
        {
            token.ThrowIfCancellationRequested();
            if (++_checkpoints == 2) beforeDecode();
            return Task.CompletedTask;
        }
        public void Report(ExtensionJobProgress progress) { }
    }
}
