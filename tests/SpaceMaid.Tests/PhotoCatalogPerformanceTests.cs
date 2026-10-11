using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using SpaceMaid.Extensions.Contracts;
using SpaceMaid.Extensions.Photos;
using Xunit;
using Xunit.Abstractions;

namespace SpaceMaid.Tests;

public sealed class PhotoCatalogPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void SyntheticBoundedCatalogWritesReportFreshAndWarmThroughput()
    {
        const int rows = 16384, batchSize = 512;
        using var directory = new TestDirectory();
        var batches = Enumerable.Range(0, rows).Select(index =>
        {
            int identity = index % 10 == 9 ? index - 1 : index;
            long length = 4096 + identity % 1024;
            return new PhotoObservation(Path.Join(@"G:\synthetic-photos", $"folder-{index / 64:000}", $"photo-{index:00000}.jpg"),
                "object-" + identity, "revision-" + identity, identity % 17 == 0 ? null : length, length,
                index % 10 == 9 ? null : 8192, index % 10 is 8 or 9 ? 2u : 1u, true, "Metadata observed", "Viewing image", "PHOTO-" + index);
        }).Chunk(batchSize).ToArray();
        // Warm the provider/JIT separately. These are fresh database inserts
        // and same-database refreshes, not controlled cold-device I/O tests.
        using (var warmup = new PhotoCatalog(Path.Join(directory.Path, "warmup")))
        { warmup.Initialize(); long collection = warmup.AddCollection(@"G:\synthetic-photos"); warmup.BeginDiscovery(collection, "warmup"); warmup.Observe(collection, "warmup", batches[0]); }
        using var catalog = new PhotoCatalog(Path.Join(directory.Path, "measured"));
        string version = catalog.Initialize(); long id = catalog.AddCollection(@"G:\synthetic-photos");
        Measure("fresh-insert", "first"); Measure("warm-refresh", "second");
        Assert.Equal(rows, catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Total);
        using var connection = Open(Path.Join(directory.Path, "measured"));
        using var count = connection.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM objects";
        Assert.Equal(rows - rows / 10, Convert.ToInt32(count.ExecuteScalar()));
        using var settings = connection.CreateCommand(); settings.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", settings.ExecuteScalar()); settings.CommandText = "PRAGMA synchronous;";
        output.WriteLine($"CATALOG_SETTINGS sqlite={version} journal=wal synchronous={settings.ExecuteScalar()} rows={rows} objects={rows - rows / 10} batches={batches.Length} batch_size={batchSize} provider_warmup=512 cold_device_cache=uncontrolled");

        void Measure(string phase, string generation)
        {
            catalog.BeginDiscovery(id, generation);
            long allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            foreach (var batch in batches) catalog.Observe(id, generation, batch);
            watch.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            catalog.EndDiscovery(id, generation, true, "Complete");
            output.WriteLine($"CATALOG_WRITE phase={phase} rows={rows} batches={batches.Length} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} rows_per_second={rows / watch.Elapsed.TotalSeconds:F0} managed_bytes={allocated}");
        }
    }

    [Fact]
    public void BatchesPreserveCaseSensitiveAliasesGenerationAllocationAndRefreshInvalidation()
    {
        using var directory = new TestDirectory(); string database = Path.Join(directory.Path, "catalog");
        using var catalog = new PhotoCatalog(database); catalog.Initialize();
        long first = catalog.AddCollection(@"G:\synthetic-one"), second = catalog.AddCollection(@"G:\synthetic-two");
        var revision = new MediaRevision("shared", 13, 10, 11, 0, 2);
        var upper = new PhotoObservation(@"G:\synthetic-one\Photo.jpg", "shared", revision.Key, 13, 17, null, 2, true, "Observed", "Viewing image", "PHOTO");
        var lower = upper with { Path = @"G:\synthetic-one\photo.jpg", AllocatedBytes = 4096 };
        catalog.BeginDiscovery(first, "one"); catalog.Observe(first, "one", [upper]); catalog.Observe(first, "one", [lower]);
        catalog.Observe(first, "one", [upper with { AllocatedBytes = 8192 }, lower with { AllocatedBytes = null }]);
        var aliases = catalog.Page(first, PhotoMode.Browse, PhotoSort.Name, false, 0);
        Assert.Equal(2, aliases.Rows.Count); Assert.Single(aliases.Rows.Select(row => row.ObjectId).Distinct());
        Assert.All(aliases.Rows, row => { Assert.Equal(8192, row.AllocatedBytes); Assert.Equal(17, row.LogicalBytes); });
        Assert.Contains(aliases.Rows, row => row.Path == upper.Path); Assert.Contains(aliases.Rows, row => row.Path == lower.Path);
        catalog.StoreAnalysis("shared", revision, new(upper.Path, "JPEG", 100, 200, 1, null, "fixture", null), "fixture-hash", "Verified");
        Assert.All(catalog.Page(first, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows, row => Assert.True(row.AnalysisCurrent));
        catalog.BeginDiscovery(second, "other"); catalog.Observe(second, "other", [upper with { Path = @"G:\synthetic-two\alias.jpg", AllocatedBytes = null }]);
        Assert.All(catalog.Page(first, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows, row =>
        { Assert.Null(row.AllocatedBytes); Assert.Null(row.Hash); Assert.False(row.AnalysisCurrent); });
        catalog.BeginDiscovery(first, "refresh"); catalog.Observe(first, "refresh", [upper with { AllocatedBytes = 1024 }]);
        catalog.EndDiscovery(first, "refresh", false, "Incomplete");
        Assert.Equal(2, catalog.Page(first, PhotoMode.Browse, PhotoSort.Name, false, 0).Total);
        Assert.All(catalog.Page(first, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows, row => Assert.Equal(1024, row.AllocatedBytes));
        using var connection = Open(database); using var evidence = connection.CreateCommand();
        evidence.CommandText = "SELECT COUNT(*) FROM objects WHERE content_revision IS NOT NULL OR hash_revision IS NOT NULL OR metadata_revision IS NOT NULL";
        Assert.Equal(0, Convert.ToInt32(evidence.ExecuteScalar()));
        catalog.EndDiscovery(first, "refresh", true, "Complete");
        Assert.Equal(upper.Path, Assert.Single(catalog.Page(first, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows).Path);
        Assert.Single(catalog.Page(second, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows);
    }

    [Fact]
    public void FailedBatchRollsBackObjectAndLocationWritesTogether()
    {
        using var directory = new TestDirectory(); string database = Path.Join(directory.Path, "catalog");
        using var catalog = new PhotoCatalog(database); catalog.Initialize(); long collection = catalog.AddCollection(@"G:\synthetic");
        catalog.BeginDiscovery(collection, "scope");
        var valid = new PhotoObservation(@"G:\synthetic\valid.jpg", "valid", "revision", null, 10, null, 1, false, "Metadata only", "Viewing image", "VALID");
        Assert.Throws<SqliteException>(() => catalog.Observe(collection, "scope", [valid, valid with { Identity = "broken", Path = @"G:\synthetic\broken.jpg", Status = null! }]));
        Assert.Empty(catalog.Page(collection, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows);
        using var connection = Open(database); using var count = connection.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM objects";
        Assert.Equal(0, Convert.ToInt32(count.ExecuteScalar()));
        catalog.Observe(collection, "scope", [valid]);
        var row = Assert.Single(catalog.Page(collection, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows);
        Assert.Null(row.DefaultBytes); Assert.Null(row.AllocatedBytes); Assert.False(row.ContentAvailable);
    }

    private static SqliteConnection Open(string directory)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Join(directory, "photos.db"), Pooling = false }.ToString());
        connection.Open(); return connection;
    }
}
