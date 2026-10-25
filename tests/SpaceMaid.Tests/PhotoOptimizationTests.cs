using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using SpaceMaid.Extensions.Contracts;
using SpaceMaid.Extensions.Photos;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PhotoOptimizationTests
{
    [Fact]
    public void LatePagesRetainSortAndCollectionScopedDuplicateAndSidecarEvidence()
    {
        using var directory = new TestDirectory();
        using var catalog = new PhotoCatalog(directory.Path); catalog.Initialize();
        long collection = catalog.AddCollection(@"G:\photos"), outside = catalog.AddCollection(@"G:\other");
        var observations = Enumerable.Range(0, 205).Select(i => new PhotoObservation(
            $@"G:\photos\folder-{i:D3}\image.jpg", $"object-{i:D3}", "observed", 4096, 4096, null, 1, true,
            "Observed", "Viewing image", "IMAGE")).ToList();
        observations.Add(observations[0] with { Path = @"G:\photos\alias.jpg", Stem = "ALIAS" });
        observations.Add(new(@"G:\photos\folder-204\image.xmp", "sidecar", "sidecar-revision", 10, 10, null, 1, true, "Observed", "Edit sidecar", "IMAGE"));
        catalog.BeginDiscovery(collection, "one"); catalog.Observe(collection, "one", observations); catalog.EndDiscovery(collection, "one", true, "Complete");
        catalog.BeginDiscovery(outside, "outside");
        catalog.Observe(outside, "outside", [observations[0] with { Identity = "outside", Path = @"G:\other\image.jpg" }]);
        var updates = Enumerable.Range(0, 205).Select(i => new PhotoAnalysisUpdate($"object-{i:D3}", "observed",
            new($"object-{i:D3}", 4096, 1, 1, 0, 1), new(observations[i].Path, "JPEG", 400, 200, 6, "2026:10:25 12:00:00", "fixture"), "same-hash", "Verified")).ToArray();
        Assert.Equal(205, catalog.StoreAnalysisBatch(updates));
        catalog.StoreAnalysis("outside", new("outside", 4096, 1, 1, 0, 1), null, "same-hash", "Verified");

        var page = catalog.Page(collection, PhotoMode.Browse, PhotoSort.Name, false, 2);
        Assert.Equal(207, page.Total);
        Assert.Equal(Enumerable.Range(199, 6).Select(i => $"object-{i:D3}"), page.Rows.Where(row => row.Name == "image.jpg").Select(row => row.ObjectId));
        Assert.All(page.Rows.Where(row => row.Name == "image.jpg"), row =>
        { Assert.Equal(205, row.GroupCount); Assert.Equal(6, row.Orientation); Assert.True(row.AnalysisCurrent); });
        Assert.Equal("sidecar-revision", page.Rows.Single(row => row.ObjectId == "object-204").SidecarRevision);
        var duplicates = catalog.Page(collection, PhotoMode.Duplicates, PhotoSort.Name, false, 2);
        Assert.Equal(206, duplicates.Total); Assert.Equal(6, duplicates.Rows.Count);
        Assert.All(duplicates.Rows, row => Assert.Equal(205, row.GroupCount));
        Assert.Equal(2, catalog.Page(collection, PhotoMode.Families, PhotoSort.Name, false, 0).Total);
        Assert.Equal("sidecar", catalog.Page(collection, PhotoMode.Browse, PhotoSort.Name, true, 0).Rows[0].ObjectId);
    }

    [Fact]
    public void AnalysisWritesAreAtomicAndDoNotOverwriteNewerObservations()
    {
        using var directory = new TestDirectory(); using var catalog = new PhotoCatalog(directory.Path); catalog.Initialize();
        long collection = catalog.AddCollection(@"G:\photos");
        var observations = new[] { "first", "second" }.Select(id => new PhotoObservation($@"G:\photos\{id}.jpg", id, "observed", 3, 3, null, 1, true, "Observed", "Viewing image", id)).ToArray();
        catalog.BeginDiscovery(collection, "one"); catalog.Observe(collection, "one", observations);
        var first = new PhotoAnalysisUpdate("first", "observed", new("first", 3, 1, 1, 0, 1),
            new(observations[0].Path, "JPEG", 10, 20, 1, null, "fixture"), null, "Verified");
        var second = first with { ObjectId = "second", Revision = new("second", 3, 1, 1, 0, 1) };
        Assert.Throws<SqliteException>(() => catalog.StoreAnalysisBatch([first, second with { Status = null! }]));
        Assert.All(catalog.Page(collection, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows, row => Assert.False(row.AnalysisCurrent));
        catalog.Observe(collection, "one", [observations[1] with { Revision = "new-observation" }]);
        Assert.Equal(1, catalog.StoreAnalysisBatch([first, second]));
        Assert.Equal(1, catalog.StoreAnalysisBatch([first with { ExpectedRevision = first.Revision!.Key, HashOnly = true, Hash = "verified-hash", Status = "Default stream verified" }]));
        var verified = catalog.Page(collection, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows.Single(row => row.ObjectId == "first");
        Assert.Equal("verified-hash", verified.Hash); Assert.Equal(10, verified.Width);
        Assert.False(catalog.Page(collection, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows.Single(row => row.ObjectId == "second").AnalysisCurrent);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.StoreAnalysisBatch([first], cancelled.Token));
        Assert.Equal("verified-hash", catalog.Page(collection, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows.Single(row => row.ObjectId == "first").Hash);
    }

    [Fact]
    public void CancellingAnExpensivePageReleasesTheCatalogForTheNextRequest()
    {
        using var directory = new TestDirectory(); using var catalog = new PhotoCatalog(directory.Path); catalog.Initialize();
        long collection = catalog.AddCollection(@"G:\photos"); catalog.BeginDiscovery(collection, "one");
        foreach (var indices in Enumerable.Range(0, 4096).Chunk(512))
            catalog.Observe(collection, "one", indices.Select(i => new PhotoObservation($@"G:\photos\{i:D4}.jpg", "id-" + i,
                "observed", 3, 3, null, 1, true, "Observed", "Viewing image", "SAME")).ToArray());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var watch = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => catalog.Page(collection, PhotoMode.Families, PhotoSort.Name, false, 0, cancellation.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Cancelled page took {watch.Elapsed}.");
        Assert.Equal(4096, catalog.Page(collection, PhotoMode.Browse, PhotoSort.Name, false, 0).Total);
    }
}
