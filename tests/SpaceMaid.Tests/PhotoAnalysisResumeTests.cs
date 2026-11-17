using System.IO;
using SpaceMaid.Core.IO;
using SpaceMaid.Extensions.Contracts;
using SpaceMaid.Extensions.Photos;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PhotoAnalysisResumeTests
{
    [Fact]
    public async Task InterruptedAnalysisReusesCommittedComparisonsAndMetadataAfterCatalogReopens()
    {
        using var fixture = new AnalysisFixture(40);
        await fixture.DiscoverAsync();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.AnalyzeCoreAsync(fixture.Collection,
            new ReportingContext(cancellation.Token, progress =>
            {
                if (progress.Phase == "Comparing full default streams" && progress.Completed >= 16) cancellation.Cancel();
            })));
        Assert.Equal("Interrupted", fixture.Catalog.LastJobStatus(fixture.Collection.Id)!.State);
        Assert.Equal(16, fixture.Catalog.Page(fixture.Collection.Id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Total);
        Assert.Equal(40, fixture.Tools.FilesInspected);

        using var reopened = new PhotoCatalog(fixture.Database); reopened.Initialize();
        await new PhotosService(fixture.Host, reopened).AnalyzeCoreAsync(reopened.Collections().Single(), new PhotoTestContext(CancellationToken.None));
        var resumed = fixture.Inner.Logs.Last(log => log.Event == "photos-analysis").Data;
        Assert.Equal(16, resumed.GetProperty("ReusedHashes").GetInt64());
        Assert.Equal(24, resumed.GetProperty("Hashed").GetInt64());
        Assert.Equal(24 * 3, resumed.GetProperty("BytesRead").GetInt64());
        Assert.Equal(40, resumed.GetProperty("ReusedMetadata").GetInt64());
        Assert.Equal(40, fixture.Tools.FilesInspected);
        Assert.Equal(40, reopened.Page(fixture.Collection.Id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Total);
        Assert.True(reopened.LastJobStatus(fixture.Collection.Id)!.ContentComparisonComplete);
    }

    [Fact]
    public async Task ChangedSourceAndSidecarAreRecomputedWithoutDiscardingUnchangedPhotoEvidence()
    {
        using var fixture = new AnalysisFixture(3, sidecar: true);
        await fixture.DiscoverAsync(); await fixture.AnalyzeAsync();
        var previous = fixture.Catalog.Page(fixture.Collection.Id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows.ToDictionary(row => row.Path);
        foreach (string path in fixture.Paths.Skip(1))
        {
            byte[] bytes = path.EndsWith(".xmp", StringComparison.Ordinal) ? [7, 8, 9] : [4, 5, 6];
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        }
        await fixture.AnalyzeAsync();
        var current = fixture.Catalog.Page(fixture.Collection.Id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows.ToDictionary(row => row.Path);
        Assert.Equal(previous[fixture.Paths[0]].Hash, current[fixture.Paths[0]].Hash);
        Assert.NotEqual(previous[fixture.Paths[0]].SidecarRevision, current[fixture.Paths[0]].SidecarRevision);
        Assert.NotEqual(ThumbnailCache.Key(previous[fixture.Paths[0]]), ThumbnailCache.Key(current[fixture.Paths[0]]));
        Assert.All(fixture.Paths.Skip(1), path => Assert.NotEqual(previous[path].Hash, current[path].Hash));
        Assert.Empty(fixture.Catalog.Page(fixture.Collection.Id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows);
        var resumed = fixture.Inner.Logs.Last(log => log.Event == "photos-analysis").Data;
        Assert.Equal(1, resumed.GetProperty("ReusedHashes").GetInt64());
        Assert.Equal(6, resumed.GetProperty("BytesRead").GetInt64());
        Assert.Equal(5, fixture.Tools.FilesInspected);
    }

    [Fact]
    public async Task ChangedToolVersionRefreshesMetadataWithoutRehashingUnchangedDefaultStreams()
    {
        using var fixture = new AnalysisFixture(2);
        await fixture.DiscoverAsync(); await fixture.AnalyzeAsync();
        fixture.Tools.Version = "fixture-v2"; fixture.Tools.Width = 999;
        await fixture.AnalyzeAsync();
        Assert.Equal(4, fixture.Tools.FilesInspected);
        Assert.All(fixture.Catalog.Page(fixture.Collection.Id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows,
            row => { Assert.Equal(999, row.Width); Assert.NotNull(row.Hash); });
        var resumed = fixture.Inner.Logs.Last(log => log.Event == "photos-analysis").Data;
        Assert.Equal(2, resumed.GetProperty("ReusedHashes").GetInt64());
        Assert.Equal(0, resumed.GetProperty("ReusedMetadata").GetInt64());
        Assert.Equal(0, resumed.GetProperty("BytesRead").GetInt64());
    }

    [Fact]
    public void MetadataOnlyUpdatePreservesOnlyStillCurrentHashEvidence()
    {
        using var directory = new TestDirectory(); using var catalog = new PhotoCatalog(directory.Path); catalog.Initialize();
        long id = catalog.AddCollection(@"G:\owned-analysis-resume");
        var revision = new MediaRevision("physical", 3, 1, 2, 0, 1);
        catalog.BeginDiscovery(id, "fixture");
        catalog.Observe(id, "fixture", [new(@"G:\owned-analysis-resume\image.jpg", revision.Identity, revision.Key, 3, 3, 3, 1, true, "Observed", "Viewing image", "IMAGE")]);
        catalog.EndDiscovery(id, "fixture", true, "Complete");
        catalog.StoreAnalysis(revision.Identity, revision, new(@"G:\owned-analysis-resume\image.jpg", "JPEG", 1, 1, 1, null, "v1"), "verified-hash", "Verified");
        catalog.StoreAnalysis(revision.Identity, revision, new(@"G:\owned-analysis-resume\image.jpg", "JPEG", 2, 2, 1, null, "v2"), null, "Metadata refreshed");
        Assert.Equal("verified-hash", catalog.AnalysisBatch(id, null).Single().Hash);
        catalog.MarkUnresolved(revision.Identity, "Unavailable");
        catalog.StoreAnalysis(revision.Identity, revision, null, null, "Source reopened");
        Assert.Null(catalog.AnalysisBatch(id, null).Single().Hash);
        catalog.StoreHash(revision.Identity, revision, "new-verification");
        catalog.StoreAnalysis(revision.Identity, revision with { Changed = 3 }, null, null, "Source changed");
        Assert.Null(catalog.AnalysisBatch(id, null).Single().Hash);
    }

    private sealed class AnalysisFixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        public string Database { get; }
        public string[] Paths { get; }
        public PhotoCatalog Catalog { get; }
        public PhotoTestHost Inner { get; }
        public ResumeTools Tools { get; } = new();
        public ResumeHost Host { get; }
        public PhotosService Service { get; }
        public PhotoCollection Collection => Catalog.Collections().Single();
        public AnalysisFixture(int count, bool sidecar = false)
        {
            string source = Directory.CreateDirectory(Path.Join(_directory.Path, "photos")).FullName;
            Paths = Enumerable.Range(0, count).Select(i => Path.Join(source, sidecar && i == count - 1 ? "photo-00.xmp" : $"photo-{i:D2}.jpg")).ToArray();
            foreach (string path in Paths) File.WriteAllBytes(path, [1, 2, 3]);
            Database = Path.Join(_directory.Path, "catalog"); Catalog = new(Database); Catalog.Initialize(); Catalog.AddCollection(source);
            Inner = new(_directory.Path, source, Paths); Host = new(Inner, Tools); Service = new(Host, Catalog);
        }
        public Task DiscoverAsync() => Service.DiscoverAsync(Collection, null, CancellationToken.None);
        public Task AnalyzeAsync() => Service.AnalyzeCoreAsync(Collection, new PhotoTestContext(CancellationToken.None));
        public void Dispose() { Catalog.Dispose(); _directory.Dispose(); }
    }

    private sealed class ReportingContext(CancellationToken token, Action<ExtensionJobProgress> report) : IExtensionJobContext
    {
        public CancellationToken Cancellation => token;
        public Task CheckpointAsync() { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public void Report(ExtensionJobProgress value) => report(value);
    }
    private sealed class ResumeHost(PhotoTestHost inner, ResumeTools tools) : IExtensionHost
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
    private sealed class ResumeTools : IToolBroker
    {
        public string Version { get; set; } = "fixture-v1";
        public int Width { get; set; } = 400;
        public int FilesInspected { get; private set; }
        public Task<string?> ExifToolVersionAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult<string?>(Version); }
        public Task<IReadOnlyList<MediaToolMetadata>> ReadExifMetadataAsync(IReadOnlyList<string> paths, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); FilesInspected += paths.Count;
            return Task.FromResult<IReadOnlyList<MediaToolMetadata>>(paths.Select(path => new MediaToolMetadata(path, "fixture", Width, 200, 1, null, Version)).ToArray());
        }
    }
}
