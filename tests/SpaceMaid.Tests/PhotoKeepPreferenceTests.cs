using System.IO;
using SpaceMaid.Extensions.Photos;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PhotoKeepPreferenceTests
{
    [Fact]
    public void PreferenceSurvivesReopeningAndRefreshAndAppliesToPhysicalAliasesOnly()
    {
        using var fixture = new TestDirectory(); var database = Path.Join(fixture.Path, "catalog");
        var original = Observation(fixture.Path, "original.jpg", "physical-a", "revision-a");
        var alias = original with { Path = Path.Join(fixture.Path, "alias.jpg") };
        var copy = Observation(fixture.Path, "copy.jpg", "physical-b", "revision-b");
        long collection;
        using (var catalog = new PhotoCatalog(database))
        {
            catalog.Initialize(); collection = catalog.AddCollection(fixture.Path);
            Refresh(catalog, collection, "first", original, alias, copy);
            var row = Rows(catalog, collection).Single(item => item.Path == original.Path);
            catalog.SetKeepPreference(collection, row, true);
        }
        using var reopened = new PhotoCatalog(database); reopened.Initialize();
        Refresh(reopened, collection, "refresh", original, alias, copy);
        var rows = Rows(reopened, collection);
        Assert.All(rows.Where(row => row.ObjectId == original.Identity), row =>
        {
            Assert.True(row.KeepPreferred);
            Assert.Contains("Review preference", row.KeepPreferenceText);
            Assert.StartsWith("Keep", row.GroupText);
        });
        Assert.False(rows.Single(row => row.ObjectId == copy.Identity).KeepPreferred);
        reopened.SetKeepPreference(collection, rows.Single(row => row.Path == alias.Path), false);
        Assert.All(Rows(reopened, collection), row => Assert.False(row.KeepPreferred));
    }

    [Fact]
    public void ChangedRevisionsAndReplacementPathsCannotInheritOrChangeAnEarlierPreference()
    {
        using var fixture = new TestDirectory(); using var catalog = new PhotoCatalog(Path.Join(fixture.Path, "catalog"));
        catalog.Initialize(); var collection = catalog.AddCollection(fixture.Path);
        var observed = Observation(fixture.Path, "photo.jpg", "original-identity", "original-revision");
        Refresh(catalog, collection, "first", observed);
        var earlier = Rows(catalog, collection).Single(); catalog.SetKeepPreference(collection, earlier, true);
        var changed = observed with { Revision = "changed-revision" };
        Refresh(catalog, collection, "changed", changed);
        var current = Rows(catalog, collection).Single(); Assert.False(current.KeepPreferred);
        Assert.Throws<InvalidOperationException>(() => catalog.SetKeepPreference(collection, earlier, true));
        catalog.SetKeepPreference(collection, current, true);
        catalog.SetKeepPreference(collection, earlier, false); // An older view cannot clear a newer decision.
        Assert.True(Rows(catalog, collection).Single().KeepPreferred);
        var replacement = observed with { Identity = "replacement-identity", Revision = "replacement-revision" };
        Refresh(catalog, collection, "replaced", replacement);
        Assert.False(Rows(catalog, collection).Single().KeepPreferred);
        Assert.Throws<InvalidOperationException>(() => catalog.SetKeepPreference(collection, current, true));
    }

    [Fact]
    public void APreferenceDoesNotGrantContentVerificationOrDuplicateEvidence()
    {
        using var fixture = new TestDirectory(); using var catalog = new PhotoCatalog(Path.Join(fixture.Path, "catalog"));
        catalog.Initialize(); var collection = catalog.AddCollection(fixture.Path);
        Refresh(catalog, collection, "first", Observation(fixture.Path, "photo.jpg", "identity", "revision"));
        catalog.SetKeepPreference(collection, Rows(catalog, collection).Single(), true);
        var row = Rows(catalog, collection).Single();
        Assert.True(row.KeepPreferred); Assert.False(row.AnalysisCurrent); Assert.Null(row.Hash);
        Assert.Empty(catalog.Page(collection, PhotoMode.Duplicates, PhotoSort.Name, false, 0).Rows);
        catalog.BeginDiscovery(collection, "interrupted"); catalog.EndDiscovery(collection, "interrupted", false, "Incomplete");
        row = Rows(catalog, collection).Single(); Assert.True(row.KeepPreferred); Assert.False(row.AnalysisCurrent);
    }

    private static PhotoObservation Observation(string root, string name, string identity, string revision)
        => new(Path.Join(root, name), identity, revision, 12, 12, null, 1, true, "Observed", "Viewing image", name);
    private static IReadOnlyList<PhotoRow> Rows(PhotoCatalog catalog, long collection)
        => catalog.Page(collection, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows;
    private static void Refresh(PhotoCatalog catalog, long collection, string generation, params PhotoObservation[] observations)
    {
        catalog.BeginDiscovery(collection, generation); catalog.Observe(collection, generation, observations);
        catalog.EndDiscovery(collection, generation, true, "Complete");
    }
}
