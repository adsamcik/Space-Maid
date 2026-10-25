using System.IO;
using System.Runtime.InteropServices;
using SpaceMaid.Core.Deduplication;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class DuplicateTests
{
    [Fact]
    public async Task FullBytesSeparateSameSizedDifferentContents()
    {
        using var fixture = new TestDirectory();
        byte[] same = Enumerable.Repeat((byte)7, 4096).ToArray();
        File.WriteAllBytes(Path.Join(fixture.Path, "first.bin"), same);
        File.WriteAllBytes(Path.Join(fixture.Path, "second.bin"), same);
        byte[] other = same.ToArray(); other[^1] = 8;
        File.WriteAllBytes(Path.Join(fixture.Path, "different.bin"), other);
        var result = await Search(fixture.Path);
        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Files.Count); Assert.Equal(4096, group.RedundantLogicalBytes);
        Assert.DoesNotContain(group.Files, f => f.Path.EndsWith("different.bin", StringComparison.Ordinal));
        Assert.True(result.Complete); Assert.True(result.ReadBytes <= 1024 * 1024);
        Assert.Equal(same, File.ReadAllBytes(Path.Join(fixture.Path, "first.bin")));
    }

    [Fact]
    public async Task NativeAliasesAreNotExtraCopiesAndAdditionalStreamsRemainExplicit()
    {
        using var fixture = new TestDirectory();
        string first = Path.Join(fixture.Path, "first.bin"), second = Path.Join(fixture.Path, "second.bin");
        File.WriteAllBytes(first, new byte[4096]); File.WriteAllBytes(second, new byte[4096]);
        Assert.True(CreateHardLinkW(Path.Join(fixture.Path, "alias.bin"), first, 0));
        File.WriteAllText(second + ":notes", "unique attached data");
        var result = await Search(fixture.Path);
        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Files.Count); Assert.True(result.ExistingAliases >= 1);
        Assert.True(group.DefaultStreamsOnly);
        Assert.Contains(group.Files, f => f.SharesFileObject); Assert.Contains(group.Files, f => f.HasAdditionalData);
        Assert.Equal("unique attached data", File.ReadAllText(second + ":notes"));
    }

    [Fact]
    public async Task RestoredWriteTimestampDoesNotReuseAnOldHash()
    {
        using var fixture = new TestDirectory();
        string first = Path.Join(fixture.Path, "first.bin"), second = Path.Join(fixture.Path, "second.bin");
        File.WriteAllBytes(first, new byte[4096]); File.WriteAllBytes(second, new byte[4096]);
        var before = await Search(fixture.Path); Assert.Single(before.Groups);
        DateTime time = File.GetLastWriteTimeUtc(second);
        var changed = new byte[4096]; changed[^1] = 1; File.WriteAllBytes(second, changed); File.SetLastWriteTimeUtc(second, time);
        Assert.Empty((await Search(fixture.Path)).Groups);
    }

    [Fact]
    public async Task ContentAndEntryLimitsRemainVisibleAndCancellationIsPartial()
    {
        using var fixture = new TestDirectory();
        for (int i = 0; i < 4; i++) File.WriteAllBytes(Path.Join(fixture.Path, i + ".bin"), new byte[4096]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var limited = await DuplicateFinder.FindAsync(scan, new() { MinimumBytes = 1, MaximumReadBytes = 1 });
        Assert.False(limited.Complete); Assert.Empty(limited.Groups); Assert.Equal(0, limited.ReadBytes);
        var entries = await DuplicateFinder.FindAsync(scan, new() { MinimumBytes = 1, MaximumFiles = 2 });
        Assert.False(entries.Complete); Assert.Equal(2, entries.ExaminedFiles); Assert.True(entries.NextRecord > 0);
        var cancelled = await DuplicateFinder.FindAsync(scan, new() { MinimumBytes = 1 }, token: new(true));
        Assert.True(cancelled.Cancelled); Assert.False(cancelled.Complete); Assert.Empty(cancelled.Groups);
    }

    [Fact]
    public async Task ExclusionsAreAppliedBeforeContentAccess()
    {
        using var fixture = new TestDirectory();
        string first = Path.Join(fixture.Path, "first.bin"), second = Path.Join(fixture.Path, "second.bin");
        File.WriteAllBytes(first, new byte[4096]); File.WriteAllBytes(second, new byte[4096]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var result = await DuplicateFinder.FindAsync(scan, new() { MinimumBytes = 1, ExcludedPaths = [second] });
        Assert.Empty(result.Groups); Assert.Equal(0, result.ReadBytes);
    }

    [Fact]
    public async Task ContinuationFindsEqualFilesSplitAcrossOneFilePages()
    {
        using var fixture = new TestDirectory();
        File.WriteAllBytes(Path.Join(fixture.Path, "first.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Join(fixture.Path, "second.bin"), new byte[4096]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        using var session = new DuplicateSearchSession(scan, new() { MinimumBytes = 1, MaximumFiles = 1 });
        var first = await session.ContinueAsync(); Assert.Empty(first.Groups); Assert.True(session.CanContinue);
        var second = await session.ContinueAsync(); Assert.Equal(2, Assert.Single(second.Groups).Files.Count);
        Assert.False(session.CanContinue); Assert.True(second.Complete); Assert.Equal(2, session.RetainedFiles);
        Assert.Empty(first.Groups); // Earlier results are immutable observations.
    }

    [Fact]
    public async Task RetainedKeeperIsRevalidatedAndOtherGoodCopiesCanStillMatch()
    {
        using var fixture = new TestDirectory();
        for (int i = 0; i < 3; i++) File.WriteAllBytes(Path.Join(fixture.Path, i + ".bin"), new byte[4096]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        using var session = new DuplicateSearchSession(scan, new() { MinimumBytes = 1, MaximumFiles = 2 });
        var first = await session.ContinueAsync(); var keeper = Assert.Single(first.Groups).Files[0];
        File.Move(keeper.Path, keeper.Path + ".original"); File.WriteAllBytes(keeper.Path, new byte[4096]);
        var next = await session.ContinueAsync(); var current = Assert.Single(next.Groups);
        Assert.Equal(2, current.Files.Count); Assert.DoesNotContain(current.Files, f => f.Path == keeper.Path);
        Assert.False(next.Complete); Assert.NotEmpty(next.Issues);
    }

    [Fact]
    public async Task SessionCapacityAndCancelledPagesStayBoundedAndExplicit()
    {
        using var fixture = new TestDirectory();
        File.WriteAllBytes(Path.Join(fixture.Path, "first.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Join(fixture.Path, "second.bin"), new byte[4096]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        using (var limited = new DuplicateSearchSession(scan, new() { MinimumBytes = 1, MaximumRetainedFiles = 1 }))
        {
            var result = await limited.ContinueAsync(); Assert.Equal(1, limited.RetainedFiles); Assert.False(result.Complete);
            Assert.False(limited.CanContinue); Assert.Contains("Retained-object limit", result.Note);
        }
        using var resumable = new DuplicateSearchSession(scan, new() { MinimumBytes = 1, MaximumFiles = 1 });
        Assert.True((await resumable.ContinueAsync(token: new(true))).Cancelled);
        Assert.True(resumable.CanContinue);
        await resumable.ContinueAsync(); var completed = await resumable.ContinueAsync(); Assert.Single(completed.Groups);
    }

    [Fact]
    public void EffectiveContentLeasesPreventParentRenames()
    {
        using var fixture = new TestDirectory();
        string parent = Directory.CreateDirectory(Path.Join(fixture.Path, "parent")).FullName;
        string file = Path.Join(parent, "file.bin"); File.WriteAllBytes(file, [1, 2, 3]);
        using (var lease = StorageFile.Open(file)) Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-moved"));
        Assert.True(File.Exists(file));
    }

    internal static async Task<DuplicateSearchResult> Search(string root)
    {
        var scan = await new VolumeScanner().ScanAsync(root, preferFast: false);
        return await DuplicateFinder.FindAsync(scan, new() { MinimumBytes = 1, MaximumReadBytes = 1024 * 1024 });
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string name, string existing, nint security);
}
