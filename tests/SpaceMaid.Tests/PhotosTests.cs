using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using SpaceMaid.Extensions.Contracts;
using SpaceMaid.Extensions.Photos;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PhotosTests
{
    [Fact]
    public async Task DiscoveryIsMetadataOnlyAndCompletenessUsesFinalCoverage()
    {
        using var directory = new TestDirectory();
        var source = Path.Join(directory.Path, "collection"); Directory.CreateDirectory(source);
        var image = Path.Join(source, "image.jpg"); await File.WriteAllBytesAsync(image, [1, 2, 3]);
        using var catalog = new PhotoCatalog(Path.Join(directory.Path, "catalog"));
        var native = catalog.Initialize(); Assert.False(string.IsNullOrWhiteSpace(native));
        var id = catalog.AddCollection(source); var collection = catalog.Collections().Single();
        var host = new PhotoTestHost(directory.Path, source, [image]);
        var service = new PhotosService(host, catalog); await service.DiscoverAsync(collection, null, CancellationToken.None);
        var page = catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0);
        Assert.Single(page.Rows); Assert.Equal(3, page.Rows[0].DefaultBytes); Assert.False(page.Rows[0].AnalysisCurrent);
        Assert.StartsWith("Discovered", catalog.Collections().Single().Status); Assert.Equal(0, host.ToolsStub.Calls);
        Assert.Equal(0, host.JobsStub.Calls); Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(image));
        host.InventoryStub.Paths = [];
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        Assert.Empty(catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows);
    }

    [Fact]
    public async Task BroadCollectionsExcludeExtensionGeneratedCachesAndOutOfScopeLocations()
    {
        using var directory = new TestDirectory();
        var image = Path.Join(directory.Path, "image.jpg"); await File.WriteAllBytesAsync(image, [1, 2, 3]);
        var host = new PhotoTestHost(directory.Path, directory.Path, [image]);
        Directory.CreateDirectory(host.CacheDirectory); Directory.CreateDirectory(host.DataDirectory);
        var cached = Path.Join(host.CacheDirectory, "thumbnail.png"); var generated = Path.Join(host.DataDirectory, "preview.jpg");
        await File.WriteAllBytesAsync(cached, [1]); await File.WriteAllBytesAsync(generated, [1]);
        host.InventoryStub.Paths = [image, cached, generated];
        using var catalog = new PhotoCatalog(Path.Join(directory.Path, "db")); catalog.Initialize(); var id = catalog.AddCollection(directory.Path);
        await new PhotosService(host, catalog).DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        Assert.Equal(image, catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows.Single().Path);
        Assert.Equal(0, host.ToolsStub.Calls);
        using var outside = new TestDirectory(); var outOfScope = Path.Join(outside.Path, "outside.jpg"); await File.WriteAllBytesAsync(outOfScope, [1]);
        host.InventoryStub.Paths = [image, outOfScope];
        await new PhotosService(host, catalog).DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        Assert.Equal(image, catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows.Single().Path);
        Assert.StartsWith("Incomplete coverage", catalog.Collections().Single().Status);
    }

    [Fact]
    public async Task ExactDuplicatesUseFullDefaultStreamsAndCoalesceNativeHardLinks()
    {
        using var directory = new TestDirectory();
        var source = Path.Join(directory.Path, "collection"); Directory.CreateDirectory(source);
        var first = Path.Join(source, "first.jpg"); var second = Path.Join(source, "second.jpg"); var alias = Path.Join(source, "alias.jpg");
        byte[] contents = [12, 3, 4, 5, 6, 77, 98, 110];
        await File.WriteAllBytesAsync(first, contents); await File.WriteAllBytesAsync(second, contents);
        Assert.True(CreateHardLinkW(alias, first, 0), new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
        await File.WriteAllBytesAsync(first + ":edits", [1, 2, 3, 4, 5]);
        using var catalog = new PhotoCatalog(Path.Join(directory.Path, "catalog")); catalog.Initialize();
        var id = catalog.AddCollection(source); var host = new PhotoTestHost(directory.Path, source, [first, second, alias]);
        host.InventoryStub.AdditionalLogical[first] = 5; host.InventoryStub.AdditionalLogical[alias] = 5;
        var service = new PhotosService(host, catalog);
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        Assert.Empty(catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows);
        await service.AnalyzeAsync(catalog.Collections().Single(), null, CancellationToken.None);
        var duplicatePage = catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0);
        Assert.Equal(3, duplicatePage.Rows.Count);
        Assert.Equal(2, duplicatePage.Rows.Select(x => x.ObjectId).Distinct().Count());
        Assert.All(duplicatePage.Rows, row => { Assert.Equal(2, row.GroupCount); Assert.Equal(Convert.ToHexString(SHA256.HashData(contents)), row.Hash); Assert.True(row.AnalysisCurrent); });
        Assert.Equal(2, host.ToolsStub.FilesInspected); // Aliases are not independent content work.
        Assert.Contains(duplicatePage.Rows, row => row.LogicalBytes == 13 && row.DefaultBytes == 8);
        Assert.Equal(contents, await File.ReadAllBytesAsync(first)); Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(first + ":edits"));
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        Assert.Empty(catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows);
        Assert.All(catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows, row => Assert.False(row.AnalysisCurrent));
    }

    [Fact]
    public async Task InterruptedOrIncompleteRefreshInvalidatesAllRetainedContentEvidenceBeforeFirstBatch()
    {
        using var directory = new TestDirectory(); var source = Path.Join(directory.Path, "photos"); Directory.CreateDirectory(source);
        var first = Path.Join(source, "one.jpg"); var second = Path.Join(source, "two.jpg");
        await File.WriteAllBytesAsync(first, [1, 2, 3]); await File.WriteAllBytesAsync(second, [1, 2, 3]);
        using var catalog = new PhotoCatalog(Path.Join(directory.Path, "db")); catalog.Initialize(); var id = catalog.AddCollection(source);
        var host = new PhotoTestHost(directory.Path, source, [first, second]); var service = new PhotosService(host, catalog);
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None); await service.AnalyzeAsync(catalog.Collections().Single(), null, CancellationToken.None);
        Assert.Equal(2, catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows.Count);
        using var cancelled = new CancellationTokenSource();
        host.InventoryStub.BeforeRead = _ => { cancelled.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.DiscoverAsync(catalog.Collections().Single(), null, cancelled.Token));
        var retained = catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows;
        Assert.Equal(2, retained.Count); Assert.All(retained, row => { Assert.False(row.AnalysisCurrent); Assert.Contains("Earlier observation", row.Status); });
        Assert.Empty(catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows);
        var jobs = host.JobsStub.Calls;
        Assert.Null((await new ThumbnailCache(host).GetAsync(retained[0], CancellationToken.None)).Image); Assert.Equal(jobs, host.JobsStub.Calls);
        host.InventoryStub.BeforeRead = null;
        await service.AnalyzeAsync(catalog.Collections().Single(), null, CancellationToken.None);
        Assert.Equal(2, catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows.Count);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.DiscoverAsync(catalog.Collections().Single(), null, cancelled.Token));
        Assert.All(catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows, row => Assert.False(row.AnalysisCurrent));
        Assert.Empty(catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows);
        Assert.Contains("Interrupted", catalog.LastJob(id));
        await service.AnalyzeAsync(catalog.Collections().Single(), null, CancellationToken.None);
        host.InventoryStub.Paths = []; host.InventoryStub.Complete = false;
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        Assert.Equal(2, catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows.Count);
        Assert.All(catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows, row => Assert.False(row.AnalysisCurrent));
        Assert.Empty(catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows);
    }

    [Fact]
    public async Task HardLinkAliasesAloneAreNeverDuplicateFilesAndChangedLocationNeedsRefresh()
    {
        using var directory = new TestDirectory(); var source = Path.Join(directory.Path, "photos"); Directory.CreateDirectory(source);
        var first = Path.Join(source, "one.jpg"); var alias = Path.Join(source, "two.jpg");
        await File.WriteAllBytesAsync(first, [1, 2, 3]); Assert.True(CreateHardLinkW(alias, first, 0));
        using var catalog = new PhotoCatalog(Path.Join(directory.Path, "db")); catalog.Initialize(); var id = catalog.AddCollection(source);
        var host = new PhotoTestHost(directory.Path, source, [first, alias]); var service = new PhotosService(host, catalog);
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        await service.AnalyzeAsync(catalog.Collections().Single(), null, CancellationToken.None);
        Assert.Empty(catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows); Assert.Equal(1, host.ToolsStub.FilesInspected);
        File.Delete(first); File.Delete(alias); await File.WriteAllBytesAsync(first, [4, 5, 6]);
        await service.AnalyzeAsync(catalog.Collections().Single(), null, CancellationToken.None);
        Assert.All(catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows, row => Assert.Contains("Unresolved", row.Status));
    }

    [Fact]
    public void CatalogPagesStableSortsAndRetainsOlderLocationsOnIncompleteRefresh()
    {
        using var directory = new TestDirectory(); using var catalog = new PhotoCatalog(Path.Join(directory.Path, "db")); catalog.Initialize();
        var id = catalog.AddCollection(directory.Path); catalog.BeginDiscovery(id, "first");
        catalog.Observe(id, "first", Enumerable.Range(0, 205).Select(i => new PhotoObservation(Path.Join(directory.Path, $"photo-{i:000}.jpg"), $"id-{i:000}", $"rev-{i}", i, i, i, 1, true, "Observed", "Viewing image", $"PHOTO-{i:000}")).ToArray());
        catalog.EndDiscovery(id, "first", true, "Complete");
        var first = catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0); var second = catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 1); var last = catalog.Page(id, PhotoMode.Browse, PhotoSort.Size, true, 2);
        Assert.Equal(100, first.Rows.Count); Assert.Equal(100, second.Rows.Count); Assert.Equal(5, last.Rows.Count);
        Assert.Empty(first.Rows.Select(x => x.Path).Intersect(second.Rows.Select(x => x.Path)));
        Assert.Equal("photo-000.jpg", first.Rows[0].Name); Assert.Equal("photo-100.jpg", second.Rows[0].Name); Assert.Equal(4, last.Rows[0].DefaultBytes);
        catalog.BeginDiscovery(id, "interrupted"); catalog.EndDiscovery(id, "interrupted", false, "Incomplete");
        Assert.Equal(205, catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Total);
        var job = catalog.StartJob(id, "Analysis"); catalog.JobState(job, "Running", 17); catalog.Initialize();
        Assert.Contains("Interrupted", catalog.LastJob(id)); Assert.Contains("17", catalog.LastJob(id));
    }

    [Fact]
    public void SizeBucketsSeekVerifiedSizesAndRequireDistinctObjectsWithinSelectedCollection()
    {
        using var directory = new TestDirectory(); var database = Path.Join(directory.Path, "db");
        using var catalog = new PhotoCatalog(database); catalog.Initialize(); var id = catalog.AddCollection(directory.Path);
        var other = catalog.AddCollection(Path.Join(directory.Path, "other"));
        var observations = Enumerable.Range(0, 205).Select(i => new PhotoObservation(Path.Join(directory.Path, $"photo-{i:000}.jpg"), $"id-{i:000}", "observed", i, i, null, 1, true, "Observed", "Viewing image", $"PHOTO-{i:000}")).ToList();
        observations.Add(new(Path.Join(directory.Path, "duplicate.jpg"), "duplicate", "observed", 25, 25, null, 1, true, "Observed", "Viewing image", "DUPLICATE"));
        observations.Add(observations[25] with { Path = Path.Join(directory.Path, "alias.jpg") });
        catalog.BeginDiscovery(id, "scope"); catalog.Observe(id, "scope", observations); catalog.EndDiscovery(id, "scope", true, "Complete");
        foreach (var observation in observations.DistinctBy(x => x.Identity))
            catalog.StoreAnalysis(observation.Identity, new(observation.Identity, observation.DefaultBytes!.Value, 1, 1, 0, 1), null, null, "Default size verified");
        var outsider = new PhotoObservation(Path.Join(directory.Path, "other", "outside.jpg"), "outside", "observed", 100, 100, null, 1, true, "Observed", "Viewing image", "OUTSIDE");
        catalog.BeginDiscovery(other, "other"); catalog.Observe(other, "other", [outsider]); catalog.EndDiscovery(other, "other", true, "Complete");
        catalog.StoreAnalysis(outsider.Identity, new(outsider.Identity, 100, 1, 1, 0, 1), null, null, "Default size verified");
        var candidates = catalog.AnalysisBatch(id, null, true);
        Assert.Equal(new[] { "duplicate", "id-025" }, candidates.Select(x => x.ObjectId).ToArray());
        Assert.DoesNotContain(candidates, row => row.ObjectId == "id-100");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Join(database, "photos.db"), Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "EXPLAIN QUERY PLAN " + PhotoCatalog.AnalysisQuery(true);
        command.Parameters.AddWithValue("$collection", id); command.Parameters.AddWithValue("$after", "");
        using var reader = command.ExecuteReader(); var plan = new List<string>(); while (reader.Read()) plan.Add(reader.GetString(3));
        for (var repository = new DirectoryInfo(AppContext.BaseDirectory); repository is not null; repository = repository.Parent)
        {
            if (!File.Exists(Path.Join(repository.FullName, "SpaceMaid.slnx"))) continue;
            var artifact = Path.Join(repository.FullName, "artifacts", "extensions", "photos-analysis-query-plan.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!); File.WriteAllLines(artifact, plan); break;
        }
        Assert.Contains(plan, line => line.StartsWith("SEARCH p", StringComparison.Ordinal) && line.Contains("object_verified_sizes (default_bytes=?)", StringComparison.Ordinal));
        Assert.Contains(plan, line => line.StartsWith("SEARCH x", StringComparison.Ordinal) && line.Contains("COVERING INDEX", StringComparison.Ordinal) && line.Contains("object_id=?", StringComparison.Ordinal));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.AnalysisBatch(id, null, true, cancelled.Token));
    }

    [Fact]
    public void FamiliesArePossibleBasenameEvidenceWithinOneFolderAndDoNotImplyDuplicates()
    {
        using var directory = new TestDirectory(); using var catalog = new PhotoCatalog(Path.Join(directory.Path, "db")); catalog.Initialize();
        var id = catalog.AddCollection(directory.Path); catalog.BeginDiscovery(id, "run");
        string[] paths = [Path.Join(directory.Path, "photo.cr3"), Path.Join(directory.Path, "photo.jpg"), Path.Join(directory.Path, "photo.cr3.xmp"), Path.Join(directory.Path, "elsewhere", "photo.jpg"), Path.Join(directory.Path, "other.jpg")];
        catalog.Observe(id, "run", paths.Select((path, i) => new PhotoObservation(path, "id-" + i, "revision", 7, 7, 7, 1, true, "Observed", PhotoKinds.Role(path)!, PhotoKinds.Stem(path))).ToArray());
        catalog.EndDiscovery(id, "run", true, "Complete");
        var families = catalog.Page(id, PhotoMode.Families, PhotoSort.Name, false, 0);
        Assert.Equal(3, families.Rows.Count); Assert.All(families.Rows, row => Assert.Contains("Possible relationship", row.GroupLabel));
        Assert.DoesNotContain(families.Rows, row => row.Path.Contains("elsewhere", StringComparison.Ordinal));
        Assert.Empty(catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows);
    }

    [Fact]
    public async Task StableLeasePreventsWritesAndRenameWhileFullHashIsRead()
    {
        using var directory = new TestDirectory(); var parent = Path.Join(directory.Path, "photos"); Directory.CreateDirectory(parent);
        var path = Path.Join(parent, "image.jpg"); byte[] contents = [10, 11, 12];
        await File.WriteAllBytesAsync(path, contents);
        using (var lease = NativeMediaFile.Open(path, true))
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [2, 3, 4]));
            Assert.Throws<IOException>(() => File.Move(path, path + ".renamed"));
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-renamed"));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(contents)), await PhotosService.HashAsync(lease, new PhotoTestContext(CancellationToken.None)));
        }
        await File.WriteAllBytesAsync(path, [2, 3, 4]); Assert.Equal(new byte[] { 2, 3, 4 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AnalyzeCancellationKeepsInterruptedStateAndDoesNotPublishCompletedEvidence()
    {
        using var directory = new TestDirectory(); var path = Path.Join(directory.Path, "image.jpg"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
        using var catalog = new PhotoCatalog(Path.Join(directory.Path, "db")); catalog.Initialize(); var id = catalog.AddCollection(directory.Path);
        var host = new PhotoTestHost(directory.Path, directory.Path, [path]); var service = new PhotosService(host, catalog);
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.AnalyzeCoreAsync(catalog.Collections().Single(), new PhotoTestContext(cancelled.Token)));
        Assert.Contains("Interrupted", catalog.LastJob(id)); Assert.False(catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows[0].AnalysisCurrent);
    }

    [Fact]
    public async Task WindowsCloudPlaceholdersRemainMetadataOnlyWithoutToolOrThumbnailReads()
    {
        using var directory = new TestDirectory(); var source = Path.Join(directory.Path, "cloud"); Directory.CreateDirectory(source);
        var path = Path.Join(source, "photo.jpg"); await File.WriteAllBytesAsync(path, new byte[4096]);
        using var registered = CloudFilesTests.RegisterSyncedFixture(source, path);
        NativeMediaFile? unexpected = null;
        try { Assert.Throws<IOException>(() => unexpected = NativeMediaFile.Open(path, true)); }
        finally { unexpected?.Dispose(); }
        using var catalog = new PhotoCatalog(Path.Join(directory.Path, "db")); catalog.Initialize(); var id = catalog.AddCollection(source);
        var host = new PhotoTestHost(directory.Path, source, [path]); var service = new PhotosService(host, catalog);
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        await service.AnalyzeAsync(catalog.Collections().Single(), null, CancellationToken.None);
        var row = catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows.Single();
        Assert.False(row.ContentAvailable); Assert.False(row.AnalysisCurrent); Assert.Equal(0, host.ToolsStub.FilesInspected);
        var calls = host.JobsStub.Calls;
        Assert.Null((await new ThumbnailCache(host).GetAsync(row, CancellationToken.None)).Image); Assert.Equal(calls, host.JobsStub.Calls);
        Assert.Equal(4096, new FileInfo(path).Length);
    }

    [Fact]
    public async Task ThumbnailsRequireAnalyzeAndRespectDecodedBoundsAndRevisionKeys()
    {
        using var directory = new TestDirectory(); var path = Path.Join(directory.Path, "image.png");
        var pixels = Enumerable.Repeat((byte)100, 400 * 200 * 4).ToArray(); var image = BitmapSource.Create(400, 200, 96, 96, PixelFormats.Bgra32, null, pixels, 400 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var output = File.Create(path)) encoder.Save(output);
        using var catalog = new PhotoCatalog(Path.Join(directory.Path, "db")); catalog.Initialize(); var id = catalog.AddCollection(directory.Path);
        var host = new PhotoTestHost(directory.Path, directory.Path, [path]); var service = new PhotosService(host, catalog); await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        var row = catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows[0]; var cache = new ThumbnailCache(host);
        Assert.Null((await cache.GetAsync(row, CancellationToken.None)).Image); Assert.Equal(0, host.JobsStub.Calls);
        await service.AnalyzeAsync(catalog.Collections().Single(), null, CancellationToken.None); row = catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows[0];
        var preview = await cache.GetAsync(row, CancellationToken.None); Assert.NotNull(preview.Image);
        Assert.InRange(preview.Image.PixelWidth, 1, 160); Assert.InRange(preview.Image.PixelHeight, 1, 160); Assert.True(cache.MemoryBytes <= ThumbnailCache.MemoryLimit);
        Assert.NotEqual(ThumbnailCache.Key(row), ThumbnailCache.Key(row with { SidecarRevision = "new-edit" }));
        Assert.NotEqual(ThumbnailCache.Key(row), ThumbnailCache.Key(row with { Revision = "changed-source" }));
        var container = Guid.NewGuid();
        Assert.NotEqual(ThumbnailCache.DecoderKey(ThumbnailCache.Key(row), container, "1"), ThumbnailCache.DecoderKey(ThumbnailCache.Key(row), container, "2"));
        Assert.Equal("File changed · Refresh and Analyze to enable preview", (await cache.GetAsync(row with { Revision = "changed-source" }, CancellationToken.None)).Status);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFile, string existing, nint security);
}

internal sealed class PhotoTestHost : IExtensionHost
{
    public PhotoTestHost(string directory, string root, IReadOnlyList<string> files)
    { DataDirectory = Path.Join(directory, "data"); CacheDirectory = Path.Join(directory, "cache"); InventoryStub = new(root, files); }
    public string DataDirectory { get; }
    public string CacheDirectory { get; }
    public PhotoTestInventory InventoryStub { get; }
    public PhotoTestTools ToolsStub { get; } = new();
    public PhotoTestJobs JobsStub { get; } = new();
    public IInventoryReader Inventory => InventoryStub;
    public IExtensionJobs Jobs => JobsStub;
    public IToolBroker Tools => ToolsStub;
    public void RevealLocation(string path) { }
    public List<(string Event, System.Text.Json.JsonElement Data)> Logs { get; } = [];
    public void Log(string eventName, object data) => Logs.Add((eventName, System.Text.Json.JsonSerializer.SerializeToElement(data)));
}
internal sealed class PhotoTestInventory(string root, IReadOnlyList<string> paths) : IInventoryReader
{
    public IReadOnlyList<string> Paths { get; set; } = paths;
    public Dictionary<string, int> AdditionalLogical { get; } = new(StringComparer.Ordinal);
    public Func<CancellationToken, Task>? BeforeRead { get; set; }
    public Func<int, Task>? BeforeBatch { get; set; }
    public Action<string>? AfterResolve { get; set; }
    public int BatchSize { get; set; } = int.MaxValue;
    public bool Complete { get; set; } = true;
    private readonly Guid _generation = Guid.NewGuid();
    public async IAsyncEnumerable<InventoryBatch> ReadAsync(string requested, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token, bool preferExistingInventory = false)
    {
        Assert.Equal(root, requested); await Task.Yield(); token.ThrowIfCancellationRequested();
        if (BeforeRead is not null) await BeforeRead(token);
        token.ThrowIfCancellationRequested();
        for (var offset = 0; offset < Paths.Count; offset += Math.Min(BatchSize, Paths.Count - offset))
        {
            if (BeforeBatch is not null) await BeforeBatch(offset);
            token.ThrowIfCancellationRequested();
            yield return new(new(root, DateTime.UtcNow, false, true, true, false, []), Paths.Skip(offset).Take(BatchSize).Select((path, i) => new InventoryItem(new(_generation, offset + i), offset + i, Path.GetFileName(path), false, new FileInfo(path).Length + AdditionalLogical.GetValueOrDefault(path), new FileInfo(path).Length, true, 0, File.GetLastWriteTimeUtc(path).ToFileTimeUtc(), null, null)).ToArray());
        }
        yield return new(new(root, DateTime.UtcNow, Complete, true, true, false, Complete ? [] : ["An owned fixture scope was unavailable."]), []);
    }
    public string? ResolveLocation(InventoryReference reference)
    {
        if (reference.Generation != _generation || reference.EntryIndex >= Paths.Count) return null;
        var path = Paths[reference.EntryIndex]; AfterResolve?.Invoke(path); return path;
    }
}
internal sealed class PhotoTestJobs : IExtensionJobs
{
    public int Calls { get; private set; }
    public async Task RunAsync(string workflow, JobResource resource, Func<IExtensionJobContext, Task> work, IProgress<ExtensionJobProgress>? progress, CancellationToken token)
    { Calls++; await Task.Run(() => work(new PhotoTestContext(token, progress)), token); }
}
internal sealed class PhotoTestContext(CancellationToken token, IProgress<ExtensionJobProgress>? progress = null) : IExtensionJobContext
{
    public CancellationToken Cancellation => token;
    public Task CheckpointAsync() { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public void Report(ExtensionJobProgress value) => progress?.Report(value);
}
internal sealed class PhotoTestTools : IToolBroker
{
    public int Calls { get; private set; }
    public int FilesInspected { get; private set; }
    public Task<IReadOnlyList<MediaToolMetadata>> ReadExifMetadataAsync(IReadOnlyList<string> paths, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Calls++; FilesInspected += paths.Count; return Task.FromResult<IReadOnlyList<MediaToolMetadata>>(paths.Select(x => new MediaToolMetadata(x, "fixture", 400, 200, 1, null, "test")).ToArray()); }
    public Task<string?> ExifToolVersionAsync(CancellationToken token) => Task.FromResult<string?>("test");
}
