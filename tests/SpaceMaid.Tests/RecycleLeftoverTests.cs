using System.IO;
using SpaceMaid.Core.IO;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class RecycleLeftoverTests
{
    [Fact]
    [Trait("Stage", "System")]
    public void RecoveryAndDeletionAffectOnlyApprovedUnpairedEntries()
    {
        using var fixture = new TestDirectory();
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "account")).FullName;
        string recovery = Directory.CreateDirectory(Path.Join(fixture.Path, "recovery")).FullName;
        File.WriteAllText(Path.Join(bin, "$Rpaired.txt"), "paired");
        File.WriteAllText(Path.Join(bin, "$ipaired.txt"), "index");
        File.WriteAllText(Path.Join(bin, "$Rkeep.txt"), "keep");
        File.WriteAllText(Path.Join(bin, "$Rrecover.txt"), "recover");
        File.WriteAllText(Path.Join(bin, "$Rdelete.txt"), "delete");
        string folder = Directory.CreateDirectory(Path.Join(bin, "$Rfolder")).FullName;
        File.WriteAllText(Path.Join(folder, "inside.txt"), "delete folder");

        var review = Read(bin);
        Assert.Equal(4, review.Entries.Count);
        var recovered = Apply(review, recovery, false, "$Rrecover.txt");
        Assert.True(Assert.Single(recovered).Completed, recovered[0].Issue);
        Assert.Equal("recover", File.ReadAllText(Path.Join(recovery, "Recovered-recover.txt")));
        var deleted = Apply(review, recovery, true, "$Rdelete.txt", "$Rfolder");
        Assert.All(deleted, result => Assert.True(result.Completed, result.Issue));
        Assert.False(File.Exists(Path.Join(bin, "$Rdelete.txt")));
        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(Path.Join(recovery, "Recovered-delete.txt")));
        Assert.False(Directory.Exists(Path.Join(recovery, "Recovered-folder")));
        Assert.Equal("keep", File.ReadAllText(Path.Join(bin, "$Rkeep.txt")));
        Assert.Equal("paired", File.ReadAllText(Path.Join(bin, "$Rpaired.txt")));
    }

    [Fact]
    [Trait("Stage", "System")]
    public void ChangedRecordsAndDestinationCollisionsKeepTheSources()
    {
        using var fixture = new TestDirectory();
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "account")).FullName;
        string recovery = Directory.CreateDirectory(Path.Join(fixture.Path, "recovery")).FullName;
        File.WriteAllText(Path.Join(bin, "$Rmate.txt"), "mate");
        File.WriteAllText(Path.Join(bin, "$Rreplace.txt"), "replace");
        File.WriteAllText(Path.Join(bin, "$Rcollision.txt"), "collision");
        var review = Read(bin);
        File.WriteAllText(Path.Join(bin, "$Imate.txt"), "new record");
        File.Move(Path.Join(bin, "$Rreplace.txt"), Path.Join(bin, "old.txt"));
        File.WriteAllText(Path.Join(bin, "$Rreplace.txt"), "new");
        File.WriteAllText(Path.Join(recovery, "Recovered-collision.txt"), "destination");

        var results = Apply(review, recovery, true, "$Rmate.txt", "$Rreplace.txt", "$Rcollision.txt");
        Assert.All(results, result => { Assert.False(result.Completed); Assert.Null(result.RecoveryPath); });
        Assert.Equal("mate", File.ReadAllText(Path.Join(bin, "$Rmate.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Join(bin, "$Rreplace.txt")));
        Assert.Equal("collision", File.ReadAllText(Path.Join(bin, "$Rcollision.txt")));
        Assert.Equal("destination", File.ReadAllText(Path.Join(recovery, "Recovered-collision.txt")));
    }

    [Fact]
    [Trait("Stage", "System")]
    public void ChangedDescendantPreventsPermanentDeletionAndKeepsData()
    {
        using var fixture = new TestDirectory();
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "account")).FullName;
        string recovery = Directory.CreateDirectory(Path.Join(fixture.Path, "recovery")).FullName;
        string folder = Directory.CreateDirectory(Path.Join(bin, "$Rfolder")).FullName;
        string nested = Directory.CreateDirectory(Path.Join(folder, "nested")).FullName;
        string file = Path.Join(nested, "file.txt");
        File.WriteAllText(file, "before");
        DateTime lastWrite = File.GetLastWriteTimeUtc(file);
        var review = Read(bin);
        File.WriteAllText(file, "edited");
        File.SetLastWriteTimeUtc(file, lastWrite);

        var result = Assert.Single(Apply(review, recovery, true, "$Rfolder"));
        Assert.False(result.Completed);
        string kept = Assert.IsType<string>(result.RecoveryPath);
        Assert.Contains("changed after review", result.Issue, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("edited", File.ReadAllText(Path.Join(kept, "nested", "file.txt")));
    }

    [Fact]
    [Trait("Stage", "System")]
    public void RecentEntriesAreListedWithoutMutationOptions()
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "$Rrecent.txt"), "recent");
        var review = DiskAccess.Default.ReviewRecycleLeftoversAt(fixture.Path, DateTime.UtcNow.AddDays(-1), default);
        var row = Assert.Single(review.Entries);
        Assert.False(row.CanRecover);
        Assert.False(row.CanDelete);
    }

    private static DiskAccess.RecycleLeftoverReview Read(string bin) =>
        DiskAccess.Default.ReviewRecycleLeftoversAt(bin, DateTime.UtcNow.AddMinutes(1), default);

    private static List<DiskAccess.RecycleLeftoverResult> Apply(DiskAccess.RecycleLeftoverReview review, string destination, bool delete, params string[] names)
    {
        var results = new List<DiskAccess.RecycleLeftoverResult>();
        review.Apply(names.Select(name => Assert.Single(review.Entries, entry => entry.Name == name)).ToArray(),
            destination, delete, new InlineProgress<DiskAccess.RecycleLeftoverResult>(results.Add), default);
        Assert.Equal(names.Length, results.Count);
        return results;
    }
}
