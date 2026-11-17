using System.IO;
using SpaceMaid.Extensions.Photos;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PhotoDiscoveryRecoveryTests
{
    [Theory]
    [InlineData("Not discovered", null, "Not discovered")]
    [InlineData("Discovering", "Running", "Interrupted")]
    [InlineData("Interrupted discovery · Refresh to validate coverage", "Interrupted", "Interrupted")]
    [InlineData("Discovery failed · Older locations retained", "Failed", "Needs attention")]
    [InlineData("Incomplete coverage · Older locations retained", "NeedsAttention", "Incomplete")]
    public void UncheckedEmptyCollectionOffersDiscoveryRecoveryInsteadOfClaimingAbsence(string status, string? jobState, string badge)
    {
        var collection = new PhotoCollection(1, @"G:\owned-photo-fixture", "Photos", status, null, 0, "Owned folder unavailable.");
        var job = jobState is null ? null : new PhotoJobStatus("Discovery", jobState, 0, "Coverage was not established.");
        var recovery = Assert.IsType<PhotoDiscoveryRecovery>(PhotoDiscoveryRecovery.For(collection, job));
        Assert.Equal(badge, recovery.Badge);
        Assert.Contains("Refresh", recovery.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not establish", recovery.Message);
        Assert.Contains("Owned folder unavailable", recovery.Message);
        Assert.DoesNotContain("No photos", recovery.Title);
        Assert.DoesNotContain("No related", recovery.Title);
    }

    [Fact]
    public async Task FailedRefreshPreservesIndexedRowsAndActualCauseUntilSuccessfulCoverage()
    {
        using var fixture = new TestDirectory();
        string source = Path.Join(fixture.Path, "photos"); Directory.CreateDirectory(source);
        string photo = Path.Join(source, "photo.jpg"); File.WriteAllBytes(photo, [1, 2, 3]);
        using var catalog = new PhotoCatalog(Path.Join(fixture.Path, "catalog")); catalog.Initialize();
        long id = catalog.AddCollection(source);
        var host = new PhotoTestHost(fixture.Path, source, [photo]); var service = new PhotosService(host, catalog);
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        var before = Assert.Single(catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows);
        host.InventoryStub.BeforeRead = _ => throw new IOException("Owned photo folder is disconnected.");
        await Assert.ThrowsAsync<IOException>(() => service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None));
        var retained = Assert.Single(catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows);
        Assert.Equal(before.Path, retained.Path); Assert.Equal(before.ObjectId, retained.ObjectId);
        Assert.False(retained.AnalysisCurrent);
        Assert.Empty(catalog.Page(id, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows);
        var collection = Assert.Single(catalog.Collections()); var job = catalog.LastJobStatus(id);
        Assert.Contains("disconnected", collection.Coverage!); Assert.Contains("disconnected", job!.Message!);
        var recovery = Assert.IsType<PhotoDiscoveryRecovery>(PhotoDiscoveryRecovery.For(collection, job));
        Assert.Contains("Earlier indexed locations remain in Browse", recovery.Message);
        Assert.Contains("disconnected", recovery.Message);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(photo));

        host.InventoryStub.BeforeRead = null; host.InventoryStub.Paths = [];
        await service.DiscoverAsync(collection, null, CancellationToken.None);
        Assert.Empty(catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows);
        Assert.Null(PhotoDiscoveryRecovery.For(catalog.Collections().Single(), catalog.LastJobStatus(id)));
        Assert.Equal(0, host.ToolsStub.Calls);
    }

    [Fact]
    public void CompletedComparisonDoesNotEraseIncompleteNamespaceCoverage()
    {
        var collection = new PhotoCollection(1, @"G:\owned-photo-fixture", "Photos", "Incomplete coverage · Older locations retained", null, 2);
        var job = new PhotoJobStatus("Analysis", "Completed", 2, null);
        var recovery = Assert.IsType<PhotoDiscoveryRecovery>(PhotoDiscoveryRecovery.For(collection, job));
        Assert.Equal("Incomplete", recovery.Badge);
        Assert.Contains("Earlier indexed locations", recovery.Message);
        Assert.Null(PhotoDiscoveryRecovery.For(collection with { Status = "Discovered · 0 metadata-only files" }, job));
    }
}
