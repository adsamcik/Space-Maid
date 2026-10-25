using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Extensions.Photos;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ThumbnailOwnershipTests
{
    private const string Owner = "space-maid.photos";

    [Fact]
    public void BoundedEvictionRemovesOnlyCurrentOwnedOutputAndItsReceipt()
    {
        using var directory = new TestDirectory();
        string source = Path.Join(directory.Path, "original.raw"); File.WriteAllText(source, "Original media");
        var host = new PhotoTestHost(directory.Path, directory.Path, [source]);
        string cache = Path.Join(host.CacheDirectory, "thumbnails-v1"); Directory.CreateDirectory(cache);
        string owned = Path.Join(cache, "owned.png"); File.WriteAllText(owned, "owned thumbnail");
        GeneratedFileOwnership.Record(owned, Owner, [source]);
        string unknown = Path.Join(cache, "legacy.png"), temporary = Path.Join(cache, "legacy.tmp");
        File.WriteAllText(unknown, "Unfamiliar bytes exceeding this test's cache budget"); File.WriteAllText(temporary, "Unfamiliar temporary file");

        new ThumbnailCache(host).TrimDisk(1);

        Assert.False(File.Exists(owned)); Assert.False(File.Exists(GeneratedFileOwnership.ReceiptPath(owned)));
        Assert.Equal("Original media", File.ReadAllText(source));
        Assert.Equal("Unfamiliar bytes exceeding this test's cache budget", File.ReadAllText(unknown));
        Assert.Equal("Unfamiliar temporary file", File.ReadAllText(temporary));
    }

    [Fact]
    public void CopiedAndModifiedOutputsDoNotInheritOwnership()
    {
        using var directory = new TestDirectory();
        string source = Path.Join(directory.Path, "source"), output = Path.Join(directory.Path, "thumbnail.png");
        File.WriteAllText(source, "Source"); File.WriteAllText(output, "AAAA"); GeneratedFileOwnership.Record(output, Owner, [source]);
        string original = output + ".retained"; File.Move(output, original); File.Copy(original, output);
        Assert.False(GeneratedFileOwnership.TryDeleteOwned(output, Owner).OutputRemoved);
        Assert.Equal("AAAA", File.ReadAllText(output));
        File.Delete(output); File.Move(original, output); File.WriteAllText(output, "BBBB");
        Assert.False(GeneratedFileOwnership.TryDeleteOwned(output, Owner).OutputRemoved);
        Assert.Equal("BBBB", File.ReadAllText(output)); Assert.True(File.Exists(GeneratedFileOwnership.ReceiptPath(output)));
    }

    [Fact]
    public void ReceiptCleanupRequiresExactPreviewedReceiptAndAbsentOutput()
    {
        using var directory = new TestDirectory();
        string source = Path.Join(directory.Path, "source"), output = Path.Join(directory.Path, "thumbnail.png");
        File.WriteAllText(source, "Source"); File.WriteAllText(output, "Thumbnail"); GeneratedFileOwnership.Record(output, Owner, [source]);
        string receipt = GeneratedFileOwnership.ReceiptPath(output);
        GeneratedInputRevision revision;
        using (var lease = GeneratedReadLease.Open(receipt)) revision = lease.Revision;
        Assert.False(GeneratedFileOwnership.TryRemoveReceiptAfterDeletion(output, Owner, revision));
        File.Delete(output);
        Assert.True(GeneratedFileOwnership.TryRemoveReceiptAfterDeletion(output, Owner, revision));
        Assert.False(File.Exists(receipt));

        File.WriteAllText(output, "New thumbnail"); GeneratedFileOwnership.Record(output, Owner, [source]);
        using (var lease = GeneratedReadLease.Open(receipt)) revision = lease.Revision;
        File.Move(receipt, receipt + ".retained"); File.Copy(receipt + ".retained", receipt); File.Delete(output);
        Assert.False(GeneratedFileOwnership.TryRemoveReceiptAfterDeletion(output, Owner, revision));
        Assert.True(File.Exists(receipt));
    }

    [Fact]
    public async Task UnfamiliarExactCacheKeyCollisionUsesSourceWithoutTouchingOrReplacingBytes()
    {
        using var directory = new TestDirectory();
        var fixture = CreatePhoto(directory.Path);
        const string unfamiliar = "Copied file at an exact computed thumbnail name";
        File.WriteAllText(fixture.CachePath, unfamiliar);
        var written = File.GetLastWriteTimeUtc(fixture.CachePath);

        var result = await new ThumbnailCache(fixture.Host).GetAsync(fixture.Row, CancellationToken.None);

        Assert.NotNull(result.Image);
        Assert.Equal(unfamiliar, File.ReadAllText(fixture.CachePath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(fixture.CachePath));
        Assert.False(File.Exists(GeneratedFileOwnership.ReceiptPath(fixture.CachePath)));
    }

    [Fact]
    public async Task CorruptOwnedPreviewIsRecreatedWithAReceiptAndCanBeReused()
    {
        using var directory = new TestDirectory();
        var fixture = CreatePhoto(directory.Path);
        File.WriteAllText(fixture.CachePath, "Corrupt bytes from the producer");
        GeneratedFileOwnership.Record(fixture.CachePath, Owner, [fixture.Row.Path]);
        var first = await new ThumbnailCache(fixture.Host).GetAsync(fixture.Row, CancellationToken.None);
        Assert.NotNull(first.Image);
        using (var owned = GeneratedFileOwnership.OpenOwned(fixture.CachePath, Owner, [fixture.Row.Path]))
        { Assert.NotNull(ThumbnailCache.Decode(owned.Stream)); owned.Verify(); }
        var second = await new ThumbnailCache(fixture.Host).GetAsync(fixture.Row, CancellationToken.None);
        Assert.NotNull(second.Image);
        Assert.Contains(fixture.Host.Logs, log => log.Event == "photos-thumbnail" && log.Data.GetProperty("Cache").GetString() == "disk");
    }

    private static (PhotoTestHost Host, PhotoRow Row, string CachePath) CreatePhoto(string directory)
    {
        string source = Path.Join(directory, "source.png");
        var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 32 * 4], 32 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(source)) encoder.Save(output);
        MediaRevision revision;
        using (var metadata = NativeMediaFile.Open(source, false)) revision = metadata.Revision;
        var row = new PhotoRow(revision.Identity, source, "source.png", "Viewing image", revision.Length, null,
            revision.Length, revision.Key, true, revision.Links, "Verified", "PNG", 32, 32, 1, null, null, null, "", null, 0)
            { AnalysisCurrent = true };
        var host = new PhotoTestHost(directory, directory, [source]);
        string cache = Path.Join(host.CacheDirectory, "thumbnails-v1"); Directory.CreateDirectory(cache);
        using var stream = File.OpenRead(source);
        var codec = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).CodecInfo;
        string key = ThumbnailCache.DecoderKey(ThumbnailCache.Key(row), codec.ContainerFormat, codec.Version.ToString());
        return (host, row, Path.Join(cache, key + ".png"));
    }
}
