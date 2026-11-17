using System.IO;
using SpaceMaid.Core.IO;
using SpaceMaid.Extensions.Contracts;
using SpaceMaid.Extensions.Photos;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PhotoMetadataCompletionTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("error")]
    [InlineData("timeout")]
    [InlineData("unavailable")]
    public async Task OptionalMetadataFailureDoesNotInvalidateCompletedContentComparison(string failure)
    {
        using var fixture = new TestDirectory();
        string source = Directory.CreateDirectory(Path.Join(fixture.Path, "photos")).FullName;
        string first = Path.Join(source, "first.jpg"), second = Path.Join(source, "second.jpg");
        File.WriteAllBytes(first, [1, 2, 3]); File.WriteAllBytes(second, [1, 2, 3]);
        using var catalog = new PhotoCatalog(Path.Join(fixture.Path, "catalog")); catalog.Initialize();
        long id = catalog.AddCollection(source);
        var inner = new PhotoTestHost(fixture.Path, source, [first, second]);
        var host = new MetadataHost(inner, new UnavailableMetadata(failure));
        var service = new PhotosService(host, catalog);
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        var progress = new CapturedProgress();
        await service.AnalyzeCoreAsync(catalog.Collections().Single(), new PhotoTestContext(CancellationToken.None, progress));

        var job = Assert.IsType<PhotoJobStatus>(catalog.LastJobStatus(id));
        Assert.Equal("CompletedWithMetadataLimitations", job.State);
        Assert.True(job.ContentComparisonComplete); Assert.True(job.MetadataLimited);
        Assert.Contains("Rich metadata unavailable for 2", job.Message);
        Assert.Contains("Completed", catalog.LastJob(id));
        Assert.False(progress.Values.Last().NeedsAttention);
        var duplicates = catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows;
        Assert.Equal(2, duplicates.Count);
        Assert.All(duplicates, row => { Assert.NotNull(row.Hash); Assert.True(row.AnalysisCurrent); Assert.Null(row.Format); });

        // The distinction is durable; a new catalog instance must not turn the
        // optional limitation back into an incomplete comparison.
        using var reopened = new PhotoCatalog(Path.Join(fixture.Path, "catalog")); reopened.Initialize();
        Assert.True(reopened.LastJobStatus(id)!.ContentComparisonComplete);
        Assert.True(reopened.LastJobStatus(id)!.MetadataLimited);
    }

    [Fact]
    public async Task ActualContentFailureStillNeedsAttentionWhenMetadataIsOptional()
    {
        using var fixture = new TestDirectory();
        string source = Directory.CreateDirectory(Path.Join(fixture.Path, "photos")).FullName;
        string first = Path.Join(source, "first.jpg"), second = Path.Join(source, "second.jpg");
        File.WriteAllBytes(first, [1, 2, 3]); File.WriteAllBytes(second, [1, 2, 3]);
        using var catalog = new PhotoCatalog(Path.Join(fixture.Path, "catalog")); catalog.Initialize();
        long id = catalog.AddCollection(source);
        var inner = new PhotoTestHost(fixture.Path, source, [first, second]);
        var service = new PhotosService(new MetadataHost(inner, new UnavailableMetadata("missing")), catalog);
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        File.Delete(first);
        var progress = new CapturedProgress();
        await service.AnalyzeCoreAsync(catalog.Collections().Single(), new PhotoTestContext(CancellationToken.None, progress));

        var job = catalog.LastJobStatus(id)!;
        Assert.Equal("NeedsAttention", job.State); Assert.False(job.ContentComparisonComplete);
        Assert.Contains("unavailable or unresolved content", job.Message);
        Assert.True(progress.Values.Last().NeedsAttention);
        Assert.Empty(catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows);
    }

    [Fact]
    public async Task UserCancellationDuringOptionalMetadataStillInterruptsComparison()
    {
        using var fixture = new TestDirectory();
        string source = Directory.CreateDirectory(Path.Join(fixture.Path, "photos")).FullName;
        string image = Path.Join(source, "photo.jpg"); File.WriteAllBytes(image, [1, 2, 3]);
        using var catalog = new PhotoCatalog(Path.Join(fixture.Path, "catalog")); catalog.Initialize();
        long id = catalog.AddCollection(source);
        using var cancellation = new CancellationTokenSource();
        var inner = new PhotoTestHost(fixture.Path, source, [image]);
        var service = new PhotosService(new MetadataHost(inner, new UnavailableMetadata("cancel", cancellation)), catalog);
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnalyzeCoreAsync(catalog.Collections().Single(), new PhotoTestContext(cancellation.Token)));
        Assert.Equal("Interrupted", catalog.LastJobStatus(id)!.State);
        Assert.False(catalog.LastJobStatus(id)!.ContentComparisonComplete);
    }

    private sealed class CapturedProgress : IProgress<ExtensionJobProgress>
    {
        public List<ExtensionJobProgress> Values { get; } = [];
        public void Report(ExtensionJobProgress value) => Values.Add(value);
    }

    private sealed class MetadataHost(PhotoTestHost inner, IToolBroker tools) : IExtensionHost
    {
        public string DataDirectory => inner.DataDirectory;
        public string CacheDirectory => inner.CacheDirectory;
        public DiskAccess Files => ((IExtensionHost)inner).Files;
        public IInventoryReader Inventory => inner.Inventory;
        public IExtensionJobs Jobs => inner.Jobs;
        public IToolBroker Tools => tools;
        public void RevealLocation(string path) => inner.RevealLocation(path);
        public void Log(string eventName, object data) => inner.Log(eventName, data);
    }

    private sealed class UnavailableMetadata(string failure, CancellationTokenSource? cancellation = null) : IToolBroker
    {
        public Task<IReadOnlyList<MediaToolMetadata>> ReadExifMetadataAsync(IReadOnlyList<string> paths, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (failure == "cancel") { cancellation!.Cancel(); token.ThrowIfCancellationRequested(); }
            if (failure == "timeout") throw new OperationCanceledException("Optional metadata deadline elapsed.");
            if (failure == "unavailable") throw new IOException("Optional metadata tool unavailable.");
            return Task.FromResult<IReadOnlyList<MediaToolMetadata>>(failure == "error"
                ? paths.Select(path => new MediaToolMetadata(path, "Untrusted partial metadata", 400, 200, 1, null, "fixture", "Metadata unavailable")).ToArray() : []);
        }
        public Task<string?> ExifToolVersionAsync(CancellationToken token) => Task.FromResult<string?>(null);
    }
}
