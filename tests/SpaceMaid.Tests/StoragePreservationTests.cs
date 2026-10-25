using System.IO;
using SpaceMaid.Core.Deduplication;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class StoragePreservationTests
{
    [Fact]
    public async Task NtfsCompressionAndUndoRetainBytesIdentityAndWriteTime()
    {
        using var fixture = new TestDirectory();
        string first = Path.Join(fixture.Path, "first.pdb"), second = Path.Join(fixture.Path, "second.pdb");
        byte[] contents = Enumerable.Repeat((byte)65, 128 * 1024).ToArray();
        File.WriteAllBytes(first, contents); File.WriteAllBytes(second, contents); OptimizationTests.Age(fixture.Path);
        var files = Assert.Single((await DuplicateTests.Search(fixture.Path)).Groups).Files;
        var chosen = files.Single(f => f.Path == second);
        var capability = StoragePreservation.Capabilities(second);
        if (!capability.Compression) { Assert.NotEqual("NTFS", capability.FileSystem); return; }
        var result = await StoragePreservation.CompressAsync(chosen);
        Assert.True(result.Completed); Assert.Equal(contents, File.ReadAllBytes(second));
        Assert.Equal(chosen.Revision.Written, File.GetLastWriteTimeUtc(second).ToFileTimeUtc());
        Assert.True((File.GetAttributes(second) & FileAttributes.Compressed) != 0);
        Assert.True(result.AllocatedAfter <= result.AllocatedBefore);
        var refreshed = Assert.Single((await DuplicateTests.Search(fixture.Path)).Groups).Files.Single(f => f.Path == second);
        Assert.Equal(chosen.Revision.Identity, refreshed.Revision.Identity);
        var undone = await StoragePreservation.CompressAsync(refreshed, decompress: true);
        Assert.True(undone.Completed); Assert.Equal(contents, File.ReadAllBytes(second));
        Assert.True((File.GetAttributes(second) & FileAttributes.Compressed) == 0);
        File.WriteAllText(second, "independent update"); Assert.Equal(contents, File.ReadAllBytes(first));
    }

    [Fact]
    public async Task ReplacementOrChangedPayloadCannotUseAConfirmedPreservationAction()
    {
        using var fixture = new TestDirectory();
        string first = Path.Join(fixture.Path, "first.pdb"), second = Path.Join(fixture.Path, "second.pdb");
        File.WriteAllBytes(first, new byte[4096]); File.WriteAllBytes(second, new byte[4096]); OptimizationTests.Age(fixture.Path);
        var chosen = Assert.Single((await DuplicateTests.Search(fixture.Path)).Groups).Files.Single(f => f.Path == second);
        File.Move(second, second + ".original"); File.WriteAllBytes(second, new byte[4096]); OptimizationTests.Age(fixture.Path);
        await Assert.ThrowsAnyAsync<Exception>(() => StoragePreservation.CompressAsync(chosen));
        Assert.True(File.Exists(second)); Assert.True(File.Exists(second + ".original"));
    }

    [Fact]
    public async Task FilesWithAdditionalDataCannotBeChangedByPreservationActions()
    {
        using var fixture = new TestDirectory();
        string first = Path.Join(fixture.Path, "first.pdb"), second = Path.Join(fixture.Path, "second.pdb");
        File.WriteAllBytes(first, new byte[4096]); File.WriteAllBytes(second, new byte[4096]);
        File.WriteAllText(second + ":notes", "keep"); OptimizationTests.Age(fixture.Path);
        var chosen = Assert.Single((await DuplicateTests.Search(fixture.Path)).Groups).Files.Single(f => f.Path == second);
        Assert.False(StoragePreservation.IsColdBuildCandidate(chosen));
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoragePreservation.CompressAsync(chosen));
        Assert.Equal("keep", File.ReadAllText(second + ":notes"));
    }

    [Fact]
    public async Task UnsupportedBlockSharingDoesNotModifyFilesAndCancellationStartsNothing()
    {
        using var fixture = new TestDirectory();
        string first = Path.Join(fixture.Path, "first.bin"), second = Path.Join(fixture.Path, "second.bin");
        File.WriteAllBytes(first, new byte[4096]); File.WriteAllBytes(second, new byte[4096]);
        var files = Assert.Single((await DuplicateTests.Search(fixture.Path)).Groups).Files;
        var capabilities = StoragePreservation.Capabilities(first);
        if (!capabilities.BlockSharing)
            await Assert.ThrowsAsync<InvalidOperationException>(() => StoragePreservation.ShareBlocksAsync(files[0], files[1]));
        else
        {
            var result = await StoragePreservation.ShareBlocksAsync(files[0], files[1]); Assert.True(result.Completed);
            Assert.True(result.SharedLogicalBytes > 0);
            File.WriteAllText(second, "independent ReFS update"); Assert.Equal(new byte[4096], File.ReadAllBytes(first));
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StoragePreservation.ShareBlocksAsync(files[0], files[1], new(true)));
        Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
    }
}
