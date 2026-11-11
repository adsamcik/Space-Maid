using System.IO;
using System.Text;
using SpaceMaid.Core.IO;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class RecycleRecordTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void KnownRecordsAreKeptForWindows(int version) =>
        Assert.Equal(DiskAccess.RecycleRecordState.Valid, DiskAccess.InspectRecycleRecord(ValidRecord(version)));

    [Theory]
    [MemberData(nameof(CorruptedRecords))]
    public void BrokenKnownRecordsAreRecognized(byte[] bytes) =>
        Assert.Equal(DiskAccess.RecycleRecordState.Corrupt, DiskAccess.InspectRecycleRecord(bytes));

    public static IEnumerable<object[]> CorruptedRecords()
    {
        yield return [Array.Empty<byte>()];
        yield return [new byte[7]];
        yield return [new byte[24]];
        yield return [ValidRecord(1)[..24]];
        yield return [ValidRecord(2)[..25]];
        yield return [ValidRecord(2)[..^2]];
        byte[] badLength = ValidRecord(2); Array.Fill(badLength, (byte)0xff, 24, 4); yield return [badLength];
        byte[] noTerminator = ValidRecord(2); noTerminator[^2] = 1; yield return [noTerminator];
        byte[] emptyName = ValidRecord(1); Array.Clear(emptyName, 24, 2); yield return [emptyName];
    }

    [Fact]
    public void UnfamiliarVersionIsNotTreatedAsCorruption()
    {
        byte[] bytes = ValidRecord(2); bytes[0] = 3;
        Assert.Equal(DiskAccess.RecycleRecordState.Unsupported, DiskAccess.InspectRecycleRecord(bytes));
    }

    [Fact]
    public void DotNetDateLimitsAndPayloadSizeDoNotAuthorizeDeletion()
    {
        byte[] bytes = ValidRecord();
        BitConverter.GetBytes(3_000_000_000_000_000_000L).CopyTo(bytes, 16);
        BitConverter.GetBytes(long.MaxValue).CopyTo(bytes, 8);
        Assert.Equal(DiskAccess.RecycleRecordState.Valid, DiskAccess.InspectRecycleRecord(bytes));
    }

    // Fixtures use the published v1 fixed path and v2 counted UTF-16 layouts.
    internal static byte[] ValidRecord(int version = 2)
    {
        const string path = "C:\\Users\\Fixture\\original.txt\0";
        byte[] bytes = new byte[version == 1 ? 544 : 28 + path.Length * 2];
        BitConverter.GetBytes((long)version).CopyTo(bytes, 0);
        BitConverter.GetBytes(7L).CopyTo(bytes, 8);
        BitConverter.GetBytes(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()).CopyTo(bytes, 16);
        if (version == 2) BitConverter.GetBytes(path.Length).CopyTo(bytes, 24);
        Encoding.Unicode.GetBytes(path).CopyTo(bytes, version == 1 ? 24 : 28);
        return bytes;
    }

    [Fact]
    [Trait("Stage", "System")]
    public void DeletesCorruptedPairsWhileKeepingValidUnknownAndUnapprovedRecords()
    {
        using var fixture = new TestDirectory();
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "$Recycle.Bin", "S-current")).FullName;
        foreach (string name in new[] { "v1", "v2", "broken", "unknown", "keep", "directoryRecord" })
            File.WriteAllText(Path.Join(bin, "$R" + name), "payload");
        File.WriteAllBytes(Path.Join(bin, "$Iv1"), ValidRecord(1));
        File.WriteAllBytes(Path.Join(bin, "$iv2"), ValidRecord(2));
        File.WriteAllBytes(Path.Join(bin, "$Ibroken"), ValidRecord(2)[..25]);
        File.WriteAllBytes(Path.Join(bin, "$Ikeep"), []);
        byte[] future = ValidRecord(2); future[0] = 3;
        File.WriteAllBytes(Path.Join(bin, "$Iunknown"), future);
        Directory.CreateDirectory(Path.Join(bin, "$IdirectoryRecord"));
        string folder = Directory.CreateDirectory(Path.Join(bin, "$Rfolder", "nested")).FullName;
        File.WriteAllText(Path.Join(folder, "content.txt"), "nested payload");
        File.WriteAllBytes(Path.Join(bin, "$Ifolder"), []);

        Assert.True(DiskAccess.Default.HasRecycleLeftoversAt(bin, default));
        var review = Read(bin);
        Assert.DoesNotContain(review.Entries, entry => entry.Name is "$Rv1" or "$Rv2");
        Assert.False(review.Entries.Single(entry => entry.Name == "$Runknown").CanDelete);
        Assert.False(review.Entries.Single(entry => entry.Name == "$RdirectoryRecord").CanDelete);
        var approved = review.Entries.Where(entry => entry.Name is "$Rbroken" or "$Rfolder").ToArray();
        Assert.Equal(2, approved.Length);
        Assert.All(approved, entry => { Assert.True(entry.HasCorruptedRecord); Assert.True(entry.CanDelete, entry.Blocked); });
        var results = new List<DiskAccess.RecycleLeftoverResult>();
        review.Delete(approved, new InlineProgress<DiskAccess.RecycleLeftoverResult>(results.Add), default);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Completed, result.Issue));
        Assert.False(File.Exists(Path.Join(bin, "$Rbroken")));
        Assert.False(Directory.Exists(Path.Join(bin, "$Rfolder")));
        Assert.False(File.Exists(Path.Join(bin, "$Ibroken")));
        Assert.False(File.Exists(Path.Join(bin, "$Ifolder")));
        foreach (string name in new[] { "v1", "v2", "unknown", "keep" })
        {
            Assert.Equal("payload", File.ReadAllText(Path.Join(bin, "$R" + name)));
            Assert.True(File.Exists(Path.Join(bin, "$I" + name)));
        }
    }

    [Theory]
    [InlineData("repaired")]
    [InlineData("replaced")]
    [InlineData("removed")]
    [InlineData("locked")]
    [InlineData("named-stream")]
    [Trait("Stage", "System")]
    public void ChangedOrUnavailableRecordsKeepThePayload(string change)
    {
        using var fixture = new TestDirectory();
        string payload = Path.Join(fixture.Path, "$Rentry"), mate = Path.Join(fixture.Path, "$Ientry");
        File.WriteAllText(payload, "keep this payload");
        File.WriteAllBytes(mate, ValidRecord(2)[..25]);
        var review = Read(fixture.Path);
        using var held = change == "locked" ? File.Open(mate, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite) : null;
        switch (change)
        {
            case "repaired": File.WriteAllBytes(mate, ValidRecord()); break;
            case "replaced": File.Move(mate, mate + "-old"); File.WriteAllBytes(mate, []); break;
            case "removed": File.Delete(mate); break;
            case "named-stream": File.WriteAllText(mate + ":note", "keep stream"); break;
        }
        var results = new List<DiskAccess.RecycleLeftoverResult>();
        review.Delete(review.Entries, new InlineProgress<DiskAccess.RecycleLeftoverResult>(results.Add), default);
        var result = Assert.Single(results);
        Assert.False(result.Completed);
        Assert.False(result.MutationAttempted);
        Assert.Equal("keep this payload", File.ReadAllText(payload));
        if (change != "removed") Assert.True(File.Exists(mate));
    }

    [Fact]
    [Trait("Stage", "System")]
    public void RecentAndUnreadableRecordsAreKeptAndFailedPayloadDeletionKeepsItsRecord()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "$Rfolder")).FullName;
        string child = Path.Join(folder, "file.txt"), mate = Path.Join(fixture.Path, "$Ifolder");
        File.WriteAllText(child, "keep"); File.WriteAllBytes(mate, []);
        var recent = Assert.Single(DiskAccess.Default.ReviewRecycleLeftoversAt(fixture.Path, DateTime.UtcNow.AddDays(-1), default).Entries);
        Assert.False(recent.CanDelete); Assert.Contains("record recently changed", recent.Blocked);
        using (File.Open(mate, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unreadable = Assert.Single(Read(fixture.Path).Entries);
            Assert.False(unreadable.HasCorruptedRecord);
            Assert.False(unreadable.CanDelete);
        }
        var review = Read(fixture.Path);
        using var held = File.Open(child, FileMode.Open, FileAccess.Read, FileShare.Read);
        var results = new List<DiskAccess.RecycleLeftoverResult>();
        review.Delete(review.Entries, new InlineProgress<DiskAccess.RecycleLeftoverResult>(results.Add), default);
        Assert.False(Assert.Single(results).Completed);
        Assert.Equal("keep", File.ReadAllText(child));
        Assert.True(File.Exists(mate));
    }

    [Fact]
    [Trait("Stage", "System")]
    public void RecoveryMovesPayloadAndRemovesOnlyItsCapturedCorruptRecord()
    {
        using var fixture = new TestDirectory();
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "bin")).FullName;
        string destination = Directory.CreateDirectory(Path.Join(fixture.Path, "recovery")).FullName;
        File.WriteAllText(Path.Join(bin, "$Rentry"), "recover me");
        File.WriteAllBytes(Path.Join(bin, "$Ientry"), []);
        var review = Read(bin);
        var results = new List<DiskAccess.RecycleLeftoverResult>();
        review.Apply(review.Entries, destination, false, new InlineProgress<DiskAccess.RecycleLeftoverResult>(results.Add), default);
        var result = Assert.Single(results);
        Assert.True(result.Completed, result.Issue);
        Assert.Equal("recover me", File.ReadAllText(result.RecoveryPath!));
        Assert.Empty(Directory.GetFileSystemEntries(bin));
    }

    private static DiskAccess.RecycleLeftoverReview Read(string bin) =>
        DiskAccess.Default.ReviewRecycleLeftoversAt(bin, DateTime.UtcNow.AddMinutes(1), default);
}
