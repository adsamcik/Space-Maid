using System.IO;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.IO;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class RecycleLeftoverTests
{
    [Fact]
    [Trait("Stage", "System")]
    public void CapturedShapesDistinguishSuggestedPackagesFromMixedToolData()
    {
        using var fixture = new TestDirectory();
        foreach (string root in new[] { "$Rcache.nuget", "$Rmixed.nuget", "$Rexport.nuget", "$Rphotos.nuget" })
        {
            string version = Directory.CreateDirectory(Path.Join(fixture.Path, root, "packages", "demo", "1.0.0")).FullName;
            File.WriteAllText(Path.Join(version, ".nupkg.metadata"), "{}");
            File.WriteAllText(Path.Join(version, "demo.1.0.0.nupkg"), "package fixture");
        }
        File.WriteAllText(Path.Join(fixture.Path, "$Rmixed.nuget", "NuGet.Config"), "local settings");
        File.WriteAllText(Path.Join(fixture.Path, "$Rexport.nuget", "packages", "demo", "1.0.0", "customer-export.csv"), "local file");
        Directory.CreateDirectory(Path.Join(fixture.Path, "$Rphotos.nuget", "packages", "demo", "1.0.0", "family-photos"));
        string rustup = Directory.CreateDirectory(Path.Join(fixture.Path, "$Rrust.rustup", "toolchains")).Parent!.FullName;
        File.WriteAllText(Path.Join(rustup, "settings.toml"), "local settings");
        string image = Directory.CreateDirectory(Path.Join(fixture.Path, "$Rimage", "google_apis", "x86_64")).FullName;
        foreach (string file in new[] { "system.img", "package.xml", "source.properties" })
            File.WriteAllText(Path.Join(image, file), "fixture");
        foreach (string dir in new[] { "caches", "compile-server", "index", "LocalHistory" })
            Directory.CreateDirectory(Path.Join(fixture.Path, "$Ride", dir));
        Directory.CreateDirectory(Path.Join(fixture.Path, "$Runsupported.nuget", "pictures"));

        var rows = Read(fixture.Path).Entries.ToDictionary(row => row.Name);
        RecycleLeftoverKind Classify(string name) => RecycleLeftoverAnalysis.Classify(rows[name], default);
        Assert.Equal(RecycleLeftoverKind.NuGetPackages, Classify("$Rcache.nuget"));
        Assert.Equal(RecycleLeftoverKind.NuGetData, Classify("$Rmixed.nuget"));
        Assert.Equal(RecycleLeftoverKind.NuGetData, Classify("$Rexport.nuget"));
        Assert.Equal(RecycleLeftoverKind.NuGetData, Classify("$Rphotos.nuget"));
        Assert.Equal(RecycleLeftoverKind.Rustup, Classify("$Rrust.rustup"));
        Assert.Equal(RecycleLeftoverKind.AndroidImages, Classify("$Rimage"));
        Assert.Equal(RecycleLeftoverKind.IdeData, Classify("$Ride"));
        Assert.Equal(RecycleLeftoverKind.Other, Classify("$Runsupported.nuget"));
        Assert.Equal("packages", Assert.Single(RecycleLeftoverAnalysis.LargestContents(rows["$Rcache.nuget"], 1)).Name);
    }

    [Fact]
    [Trait("Stage", "System")]
    public void LeftoverCheckMatchesOnlyTopLevelUnpairedPayloads()
    {
        using var fixture = new TestDirectory();
        string bin = Path.Join(fixture.Path, "account");
        var files = DiskAccess.Default;
        Assert.False(files.HasRecycleLeftoversAt(bin, default));
        string folder = Directory.CreateDirectory(Path.Join(bin, "$Rpaired")).FullName;
        File.WriteAllText(Path.Join(folder, "$Runmatched.txt"), "part of the paired folder");
        File.WriteAllText(Path.Join(bin, "$ipaired"), "record");
        File.WriteAllText(Path.Join(bin, "desktop.ini"), "metadata");
        Assert.False(files.HasRecycleLeftoversAt(bin, default));

        string payload = Path.Join(bin, "$Rleftover.txt");
        File.WriteAllText(payload, "unmatched");
        Assert.True(files.HasRecycleLeftoversAt(bin, default));
        Assert.Equal("unmatched", File.ReadAllText(payload));
        Assert.True(Directory.Exists(folder));
    }

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

    [Fact]
    [Trait("Stage", "System")]
    public void DirectDeletionRemovesOnlyApprovedOrphansInTheCapturedAccount()
    {
        using var fixture = new TestDirectory();
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "$Recycle.Bin", "S-current")).FullName;
        string other = Directory.CreateDirectory(Path.Join(fixture.Path, "$Recycle.Bin", "S-other")).FullName;
        File.WriteAllText(Path.Join(other, "$Rother"), "keep other account");
        File.WriteAllText(Path.Join(bin, "$Rpaired"), "keep paired");
        File.WriteAllText(Path.Join(bin, "$Ipaired"), "record");
        File.WriteAllText(Path.Join(bin, "$Rkeep"), "keep unapproved");
        File.WriteAllText(Path.Join(bin, "$Rfile"), "delete");
        string nested = Directory.CreateDirectory(Path.Join(bin, "$Rfolder", "nested")).FullName;
        File.WriteAllText(Path.Join(nested, "content.txt"), "delete nested content");
        var review = Read(bin);

        var results = Delete(review, "$Rfile", "$Rfolder");

        Assert.All(results, result => { Assert.True(result.Completed, result.Issue); Assert.Null(result.RecoveryPath); });
        Assert.False(File.Exists(Path.Join(bin, "$Rfile")));
        Assert.False(Directory.Exists(Path.Join(bin, "$Rfolder")));
        Assert.Equal("keep paired", File.ReadAllText(Path.Join(bin, "$Rpaired")));
        Assert.Equal("record", File.ReadAllText(Path.Join(bin, "$Ipaired")));
        Assert.Equal("keep unapproved", File.ReadAllText(Path.Join(bin, "$Rkeep")));
        Assert.Equal("keep other account", File.ReadAllText(Path.Join(other, "$Rother")));
    }

    [Fact]
    [Trait("Stage", "System")]
    public void DirectDeletionKeepsChangedPairedAndLockedEntriesInPlace()
    {
        using var fixture = new TestDirectory();
        string bin = fixture.Path;
        foreach (string name in new[] { "$Rpaired", "$Rreplaced", "$Rlocked" }) File.WriteAllText(Path.Join(bin, name), "before");
        string folder = Directory.CreateDirectory(Path.Join(bin, "$Rfolder", "nested")).FullName;
        string child = Path.Join(folder, "content.txt");
        File.WriteAllText(child, "before");
        DateTime written = File.GetLastWriteTimeUtc(child);
        var review = Read(bin);
        File.WriteAllText(Path.Join(bin, "$Ipaired"), "new record");
        File.Move(Path.Join(bin, "$Rreplaced"), Path.Join(bin, "original"));
        File.WriteAllText(Path.Join(bin, "$Rreplaced"), "replacement");
        File.WriteAllText(child, "edited");
        File.SetLastWriteTimeUtc(child, written);
        using var held = File.Open(Path.Join(bin, "$Rlocked"), FileMode.Open, FileAccess.Read, FileShare.Read);

        var results = Delete(review, "$Rpaired", "$Rreplaced", "$Rlocked", "$Rfolder");

        Assert.All(results, result => { Assert.False(result.Completed); Assert.False(result.MutationAttempted); Assert.Null(result.RecoveryPath); });
        Assert.Equal("before", File.ReadAllText(Path.Join(bin, "$Rpaired")));
        Assert.Equal("replacement", File.ReadAllText(Path.Join(bin, "$Rreplaced")));
        Assert.Equal("edited", File.ReadAllText(child));
        Assert.True(File.Exists(Path.Join(bin, "$Rlocked")));
    }

    [Fact]
    [Trait("Stage", "System")]
    public void DirectDeletionRejectsForeignApprovalAndStopsBetweenEntries()
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "$Rfirst"), "first");
        File.WriteAllText(Path.Join(fixture.Path, "$Rsecond"), "second");
        var review = Read(fixture.Path);
        var updates = new List<DiskAccess.RecycleLeftoverResult>();
        Assert.Throws<IOException>(() => review.Delete(Read(fixture.Path).Entries, new InlineProgress<DiskAccess.RecycleLeftoverResult>(updates.Add), default));
        Assert.Empty(updates);
        using var stop = new CancellationTokenSource();
        var approved = new[] { "$Rfirst", "$Rsecond" }.Select(name => review.Entries.Single(entry => entry.Name == name)).ToArray();
        Assert.ThrowsAny<OperationCanceledException>(() => review.Delete(approved,
            new InlineProgress<DiskAccess.RecycleLeftoverResult>(result => { updates.Add(result); stop.Cancel(); }), stop.Token));
        Assert.True(Assert.Single(updates).Completed);
        Assert.False(File.Exists(Path.Join(fixture.Path, "$Rfirst")));
        Assert.Equal("second", File.ReadAllText(Path.Join(fixture.Path, "$Rsecond")));
    }

    private static List<DiskAccess.RecycleLeftoverResult> Delete(DiskAccess.RecycleLeftoverReview review, params string[] names)
    {
        var results = new List<DiskAccess.RecycleLeftoverResult>();
        review.Delete(names.Select(name => Assert.Single(review.Entries, entry => entry.Name == name)).ToArray(),
            new InlineProgress<DiskAccess.RecycleLeftoverResult>(results.Add), default);
        Assert.Equal(names.Length, results.Count);
        return results;
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
