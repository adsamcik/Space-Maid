using System.IO;
using System.ComponentModel;
using Microsoft.Data.Sqlite;
using SpaceMaid.Extensions.Photos;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PhotoCollectionRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedAdmittedCallbacksCannotPublishAfterAnotherCatalogRepairsOrRemovesTheRoot(bool remove)
    {
        using var fixture = new TestDirectory(); string database = Path.Join(fixture.Path, "catalog");
        using var admitted = new PhotoCatalog(database); admitted.Initialize();
        string root = Path.Join(fixture.Path, "root"); long id = admitted.AddCollection(root);
        long other = admitted.AddCollection(Path.Join(fixture.Path, "other"));
        var revision = new MediaRevision("physical", 3, 1, 2, 0, 1);
        var observed = new PhotoObservation(Path.Join(root, "photo.jpg"), revision.Identity, revision.Key, 3, 3, null, 1, true, "Observed", "Viewing image", "PHOTO");
        Observe(admitted, other, observed);
        var captured = admitted.Collections().Single(item => item.Id == id);
        string job = admitted.StartDiscovery(captured, "admitted");
        admitted.ObserveCollection(captured, job, "admitted", [observed]);
        using var recovery = new PhotoCatalog(database); recovery.Initialize();
        var current = recovery.Collections().Single(item => item.Id == id);
        if (remove) { recovery.RemoveCollection(current); id = recovery.AddCollection(root); }
        else recovery.ChangeCollectionRoot(current, root); // Same-path replacement must also revoke the incarnation.

        Assert.Throws<InvalidOperationException>(() => admitted.ObserveCollection(captured, job, "admitted", [observed]));
        Assert.False(admitted.TryEndCollectionDiscovery(captured, job, "admitted", true, "Complete"));
        Assert.False(admitted.TryCollectionJobState(captured, job, "Running", 1));
        Assert.Throws<InvalidOperationException>(() => admitted.AnalysisCollectionBatch(captured, job, null, false, default));
        Assert.Throws<InvalidOperationException>(() => admitted.StoreCollectionAnalysis(captured, job,
            [new(revision.Identity, revision.Key, revision, null, "stale-hash", "Verified")], default));
        var recovered = recovery.Collections().Single(item => item.Id == id);
        Assert.Equal("Not discovered", recovered.Status); Assert.Equal(0, recovered.Count); Assert.Null(recovery.LastJobStatus(id));
        Assert.Null(Row(recovery, other).Hash); Assert.False(Row(recovery, other).AnalysisCurrent);
    }

    [Fact]
    public void SamePathRepairRevokesQueuedSnapshotAuthority()
    {
        using var fixture = new TestDirectory(); using var catalog = Catalog(fixture);
        catalog.AddCollection(fixture.Path); var before = catalog.Collections().Single();
        catalog.ChangeCollectionRoot(before, fixture.Path); var current = catalog.Collections().Single();
        Assert.NotEqual(before.RootVersion, current.RootVersion);
        Assert.Throws<InvalidOperationException>(() => catalog.StartCollectionJob(before, "Analysis"));
        string job = catalog.StartCollectionJob(current, "Analysis"); catalog.JobState(job, "Completed", 0);
    }

    [Fact]
    public void ExistingCatalogMigrationKeepsLocationsAndPreferences()
    {
        using var fixture = new TestDirectory(); string database = Path.Join(fixture.Path, "catalog"); long collection;
        using (var original = new PhotoCatalog(database))
        {
            original.Initialize(); collection = original.AddCollection(fixture.Path);
            Observe(original, collection, new(Path.Join(fixture.Path, "photo.jpg"), "id", "revision", 3, 3, null, 1, true, "Observed", "Viewing image", "PHOTO"));
            original.SetKeepPreference(collection, Row(original, collection), true);
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Join(database, "photos.db") }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE collections DROP COLUMN root_version; DROP TABLE collection_sequence;"; command.ExecuteNonQuery();
        }
        using var migrated = new PhotoCatalog(database); migrated.Initialize();
        Assert.True(Row(migrated, collection).KeepPreferred); Assert.Null(migrated.Collections().Single().RootVersion);
        Assert.True(migrated.AddCollection(Path.Join(fixture.Path, "next")) > collection);
    }

    [Fact]
    public void RootRepairClearsOnlySelectedLocationsAndPreservesSharedObjectsAndKeepPreferences()
    {
        using var fixture = new TestDirectory(); using var catalog = Catalog(fixture);
        string original = Directory.CreateDirectory(Path.Join(fixture.Path, "original")).FullName;
        string repaired = Directory.CreateDirectory(Path.Join(fixture.Path, "repaired")).FullName;
        string photo = Path.Join(original, "photo.jpg"); byte[] bytes = [1, 2, 3]; File.WriteAllBytes(photo, bytes);
        long first = catalog.AddCollection(original), other = catalog.AddCollection(fixture.Path);
        var revision = new MediaRevision("physical", bytes.Length, 1, 2, 0, 1);
        var observed = new PhotoObservation(photo, revision.Identity, revision.Key, bytes.Length, bytes.Length, null, 1, true, "Observed", "Viewing image", "PHOTO");
        Observe(catalog, first, observed); Observe(catalog, other, observed);
        catalog.StoreAnalysisBatch([new(revision.Identity, revision.Key, revision, null, "verified-hash", "Verified")]);
        catalog.SetKeepPreference(first, Row(catalog, first), true);
        var before = catalog.Collections().Single(item => item.Id == first);
        catalog.ChangeCollectionRoot(before, repaired);
        var changed = catalog.Collections().Single(item => item.Id == first);
        Assert.Equal(repaired, changed.Root); Assert.Equal("Not discovered", changed.Status); Assert.Equal(0, changed.Count);
        Assert.Empty(catalog.Page(first, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows);
        Assert.True(Row(catalog, other).AnalysisCurrent); Assert.True(Row(catalog, other).KeepPreferred);
        Assert.Equal(bytes, File.ReadAllBytes(photo)); Assert.Empty(Directory.EnumerateFileSystemEntries(repaired));
        Assert.Throws<InvalidOperationException>(() => catalog.StartDiscovery(before, "stale"));
        Observe(catalog, first, observed with { Path = Path.Join(repaired, "photo.jpg") });
        Assert.True(Row(catalog, first).KeepPreferred); Assert.False(Row(catalog, first).AnalysisCurrent);
    }

    [Fact]
    public void IndexRemovalPreservesSourcesAndPreferencesAndNeverReusesRemovedIds()
    {
        using var fixture = new TestDirectory(); string database = Path.Join(fixture.Path, "catalog");
        string source = Directory.CreateDirectory(Path.Join(fixture.Path, "source")).FullName;
        string file = Path.Join(source, "photo.jpg"); File.WriteAllBytes(file, [9, 8, 7]);
        PhotoCollection removed;
        var observation = new PhotoObservation(file, "physical", "revision", 3, 3, null, 1, true, "Observed", "Viewing image", "PHOTO");
        using (var catalog = new PhotoCatalog(database))
        {
            catalog.Initialize(); long id = catalog.AddCollection(source); Observe(catalog, id, observation);
            catalog.SetKeepPreference(id, Row(catalog, id), true);
            removed = catalog.Collections().Single(); catalog.RemoveCollection(removed);
            Assert.Empty(catalog.Collections()); Assert.Null(catalog.LastJobStatus(removed.Id));
        }
        using var reopened = new PhotoCatalog(database); reopened.Initialize();
        long added = reopened.AddCollection(source); Assert.True(added > removed.Id);
        Assert.Throws<InvalidOperationException>(() => reopened.StartCollectionJob(removed, "Analysis"));
        Observe(reopened, added, observation); Assert.True(Row(reopened, added).KeepPreferred);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(file));
    }

    [Fact]
    public void ActiveJobsAndDestinationCollisionsCannotDiscardAnIndex()
    {
        using var fixture = new TestDirectory(); using var catalog = Catalog(fixture);
        string original = Path.Join(fixture.Path, "first"), destination = Path.Join(fixture.Path, "second");
        long id = catalog.AddCollection(original); catalog.AddCollection(destination);
        var collection = catalog.Collections().Single(item => item.Id == id);
        string job = catalog.StartCollectionJob(collection, "Analysis");
        Assert.Throws<InvalidOperationException>(() => catalog.RemoveCollection(collection));
        Assert.Throws<InvalidOperationException>(() => catalog.ChangeCollectionRoot(collection, Path.Join(fixture.Path, "third")));
        Assert.Throws<InvalidOperationException>(() => catalog.StartDiscovery(collection, "concurrent"));
        catalog.JobState(job, "Interrupted", 0);
        Assert.Throws<InvalidOperationException>(() => catalog.ChangeCollectionRoot(collection, destination));
        Assert.Equal(original, catalog.Collections().Single(item => item.Id == id).Root);
        Assert.Equal(2, catalog.Collections().Count);
    }

    [Fact]
    [Trait("Stage", "System")]
    public async Task RootChoiceRequiresAnAvailableDirectFolderAndNeverChangesItsContents()
    {
        using var fixture = new TestDirectory(); using var catalog = Catalog(fixture);
        string original = Directory.CreateDirectory(Path.Join(fixture.Path, "original")).FullName;
        string destination = Directory.CreateDirectory(Path.Join(fixture.Path, "destination")).FullName;
        string photo = Path.Join(destination, "photo.jpg"); File.WriteAllBytes(photo, [1, 2, 3]);
        catalog.AddCollection(original); var captured = catalog.Collections().Single();
        var service = new PhotosService(new PhotoTestHost(fixture.Path, original, []), catalog);
        await Assert.ThrowsAsync<Win32Exception>(() => service.ChangeCollectionRootAsync(captured, Path.Join(fixture.Path, "missing"), default));
        Assert.Equal(original, catalog.Collections().Single().Root);
        await service.ChangeCollectionRootAsync(captured, destination, default);
        Assert.Equal(destination, catalog.Collections().Single().Root); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(photo));
        await service.RemoveCollectionAsync(catalog.Collections().Single(), default);
        Assert.True(Directory.Exists(original)); Assert.True(Directory.Exists(destination)); Assert.True(File.Exists(photo));
    }

    private static PhotoCatalog Catalog(TestDirectory fixture)
    { var catalog = new PhotoCatalog(Path.Join(fixture.Path, "catalog")); catalog.Initialize(); return catalog; }
    private static PhotoRow Row(PhotoCatalog catalog, long collection) => Assert.Single(catalog.Page(collection, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows);
    private static void Observe(PhotoCatalog catalog, long collection, PhotoObservation observation)
    { catalog.BeginDiscovery(collection, "generation"); catalog.Observe(collection, "generation", [observation]); catalog.EndDiscovery(collection, "generation", true, "Complete"); }
}
