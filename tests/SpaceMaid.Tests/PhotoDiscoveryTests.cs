using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.Extensions.Photos;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PhotoDiscoveryTests
{
    [Fact]
    public void DiscoveryReaderReusesBoundedParentGuardsAndReadsFreshRevisions()
    {
        using var fixture = new TestDirectory();
        var firstParent = Path.Join(fixture.Path, "photos", "first");
        var secondParent = Path.Join(fixture.Path, "photos", "second");
        Directory.CreateDirectory(firstParent); Directory.CreateDirectory(secondParent);
        var first = Path.Join(firstParent, "one.jpg"); var sibling = Path.Join(firstParent, "two.jpg"); var next = Path.Join(secondParent, "three.jpg");
        File.WriteAllBytes(first, [1, 2, 3]); File.WriteAllBytes(sibling, [4]); File.WriteAllBytes(next, [5]);
        using (var reader = new NativeMediaFile.DiscoveryReader())
        {
            var revision = reader.Read(first);
            using (var ordinary = NativeMediaFile.Open(first, false)) Assert.Equal(ordinary.Revision, revision);
            var opened = reader.ParentHandleOpens;
            reader.Read(sibling); Assert.Equal(opened, reader.ParentHandleOpens);
            // Discovery releases each file handle immediately and never caches
            // a revision/availability verdict, even within the same directory.
            File.WriteAllBytes(first, [7, 8, 9, 10, 11]);
            var changed = reader.Read(first); Assert.Equal(5, changed.Length); Assert.NotEqual(revision.Key, changed.Key);
            Assert.Throws<IOException>(() => Directory.Move(firstParent, firstParent + "-moved"));
            reader.Read(next); Assert.Equal(opened + 1, reader.ParentHandleOpens); Assert.Equal(opened, reader.PeakParentHandles);
            Assert.Equal(4, reader.FilesInspected); Assert.True(reader.ParentChecks >= opened * 4);
            Directory.Move(firstParent, firstParent + "-moved");
            Assert.Throws<IOException>(() => Directory.Move(secondParent, secondParent + "-moved"));
        }
        Directory.Move(secondParent, secondParent + "-moved");
    }

    [Fact]
    public async Task DiscoveryPublishesBoundedBatchesRetainsInterruptedRowsAndReleasesHandles()
    {
        using var fixture = new TestDirectory(); var source = Path.Join(fixture.Path, "photos"); Directory.CreateDirectory(source);
        var paths = Enumerable.Range(0, 520).Select(i => Path.Join(source, $"photo-{i:000}.jpg")).ToArray();
        foreach (var path in paths) File.WriteAllBytes(path, [1, 2, 3]);
        using var catalog = new PhotoCatalog(Path.Join(fixture.Path, "db")); catalog.Initialize(); var id = catalog.AddCollection(source);
        var host = new PhotoTestHost(fixture.Path, source, paths); host.InventoryStub.BatchSize = 64;
        using var cancelled = new CancellationTokenSource();
        long committedAtCancellation = 0;
        host.InventoryStub.BeforeBatch = offset =>
        {
            if (offset == 512)
            {
                // Discovery may flush a partial batch after one second. Keep
                // exactly the rows already committed, regardless of throughput.
                committedAtCancellation = catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Total;
                Assert.InRange(committedAtCancellation, 1, 512);
                cancelled.Cancel();
            }
            return Task.CompletedTask;
        };
        var service = new PhotosService(host, catalog);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.DiscoverAsync(catalog.Collections().Single(), null, cancelled.Token));
        Assert.Equal(committedAtCancellation, catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Total);
        Assert.Contains("Interrupted", catalog.LastJob(id));
        var diagnostic = host.Logs.Last(x => x.Event == "photos-discovery").Data;
        Assert.Equal("Interrupted", diagnostic.GetProperty("State").GetString()); Assert.False(diagnostic.GetProperty("Complete").GetBoolean());
        Assert.Equal(committedAtCancellation, diagnostic.GetProperty("Committed").GetInt64());
        Directory.Move(source, source + "-moved"); Directory.Move(source + "-moved", source);
        host.InventoryStub.BeforeBatch = null;
        await service.DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        var page = catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0); Assert.Equal(520, page.Total);
        Assert.All(page.Rows, row => { Assert.False(row.AnalysisCurrent); Assert.True(row.ContentAvailable); });
        Assert.Equal(0, host.ToolsStub.Calls); Assert.Equal(0, host.JobsStub.Calls);
        diagnostic = host.Logs.Last(x => x.Event == "photos-discovery").Data;
        Assert.True(diagnostic.GetProperty("Complete").GetBoolean()); Assert.Equal(520, diagnostic.GetProperty("FilesInspected").GetInt64());
        Assert.True(diagnostic.GetProperty("ParentHandleOpens").GetInt64() < 520);
        Assert.True(diagnostic.GetProperty("MetadataMs").GetDouble() > 0); Assert.True(diagnostic.GetProperty("CatalogMs").GetDouble() > 0);
    }

    [Fact]
    public void DirectorySharingConflictFallsBackToFreshAttributeOnlyDiscovery()
    {
        using var fixture = new TestDirectory(); var source = Path.Join(fixture.Path, "photos"); Directory.CreateDirectory(source);
        var path = Path.Join(source, "image.jpg"); File.WriteAllBytes(path, [1, 2, 3]);
        // No deletion occurs: an owned DELETE-access lease simply prevents a
        // retained LIST_DIRECTORY handle from denying delete sharing.
        using var directory = CreateFileW(source, 0x10080, 7, 0, 3, 0x02300000, 0);
        Assert.False(directory.IsInvalid, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
        using var reader = new NativeMediaFile.DiscoveryReader();
        var first = reader.Read(path);
        using (var ordinary = NativeMediaFile.Open(path, false)) Assert.Equal(ordinary.Revision, first);
        File.WriteAllBytes(path, [7, 8, 9, 10, 11]);
        var second = reader.Read(path); Assert.Equal(5, second.Length); Assert.NotEqual(first.Key, second.Key);
        Assert.Equal(2, reader.UncachedFiles); Assert.Equal(2, reader.FilesInspected);
    }

    [Fact]
    public async Task FileRemovedAfterEnumerationNeverAcquiresContentAuthorization()
    {
        using var fixture = new TestDirectory(); var path = Path.Join(fixture.Path, "locked.jpg"); File.WriteAllBytes(path, [1, 2, 3]);
        using var catalog = new PhotoCatalog(Path.Join(fixture.Path, "db")); catalog.Initialize(); var id = catalog.AddCollection(fixture.Path);
        var host = new PhotoTestHost(fixture.Path, fixture.Path, [path]);
        host.InventoryStub.AfterResolve = File.Delete;
        await new PhotosService(host, catalog).DiscoverAsync(catalog.Collections().Single(), null, CancellationToken.None);
        var row = Assert.Single(catalog.Page(id, PhotoMode.Browse, PhotoSort.Name, false, 0).Rows);
        Assert.StartsWith("uncertain:", row.ObjectId); Assert.Null(row.DefaultBytes); Assert.False(row.ContentAvailable); Assert.False(row.AnalysisCurrent);
        Assert.Equal(0, host.ToolsStub.Calls);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
