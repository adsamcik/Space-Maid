using System.Diagnostics;
using System.IO;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CloudDirectoryTests
{
    [Fact]
    [Trait("Stage", "System")]
    public void OrdinaryFoldersExposeMetadataWithoutGuessingOnlineOnlySupport()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "Proton Drive")).FullName;
        File.WriteAllText(Path.Join(folder, "not-traversed.txt"), "nested content");
        string file = Path.Join(fixture.Path, "OneDrive-backup.bin");
        File.WriteAllBytes(file, new byte[4096]);

        var listing = CloudFiles.ReadDirectory(fixture.Path, default);

        Assert.True(listing.Complete);
        Assert.Empty(listing.Warnings);
        Assert.Equal(new[] { "Proton Drive", "OneDrive-backup.bin" }, listing.Entries.Select(entry => entry.Name));
        Assert.True(listing.Entries[0].IsDirectory);
        Assert.Equal(4096, listing.Entries[1].LogicalBytes);
        Assert.All(listing.Entries, entry =>
        {
            Assert.Null(entry.LocalBytes);
            Assert.False(entry.CanRequestOnlineOnly);
        });
        Assert.Throws<IOException>(() => CloudFiles.Prepare(fixture.Path, default));
        Assert.Equal(4096, new FileInfo(file).Length);
        Assert.Equal("nested content", File.ReadAllText(Path.Join(folder, "not-traversed.txt")));
    }

    [Fact]
    [Trait("Stage", "System")]
    public void RegisteredRootSupportsBrowsingAndReviewWithoutBeingAPlaceholder()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "Photos")).FullName;
        string file = Path.Join(folder, "photo.jpg");
        File.WriteAllBytes(file, new byte[4096]);
        string pending = Path.Join(folder, "pending.jpg");
        File.WriteAllBytes(pending, new byte[8192]);
        using var registration = CloudFilesTests.RegisterSyncedFixture(fixture.Path, file);

        var root = Assert.IsType<CloudFileState>(CloudFiles.Inspect(fixture.Path));
        Assert.True(root.IsDirectory);
        var rootListing = CloudFiles.ReadDirectory(fixture.Path, default);
        var directory = Assert.Single(rootListing.Entries);
        Assert.True(directory.IsDirectory);
        Assert.True(directory.CanRequestOnlineOnly);
        Assert.Null(directory.LocalBytes);
        var listing = CloudFiles.ReadDirectory(folder, default);
        Assert.True(listing.Complete);
        var resident = Assert.Single(listing.Entries, entry => entry.Name == "photo.jpg");
        Assert.Equal(4096, resident.LocalBytes);
        Assert.Equal(4096, resident.LogicalBytes);
        Assert.True(resident.CanRequestOnlineOnly);
        var unsynced = Assert.Single(listing.Entries, entry => entry.Name == "pending.jpg");
        Assert.Null(unsynced.LocalBytes);
        Assert.False(unsynced.CanRequestOnlineOnly);

        var plan = CloudFiles.Prepare(fixture.Path, default);
        Assert.Equal(1, plan.EligibleCount);
        Assert.Equal(file, Assert.Single(plan.Targets).Path);
        Assert.Equal(4096, plan.LocalBytes);
        Assert.Equal(1, plan.SkippedCount);
        Assert.NotEqual(2, CloudFiles.Inspect(file)!.PinState);
        Assert.Equal(4096, CloudFiles.Inspect(file)!.LocalBytes);
        Assert.Equal(8192, new FileInfo(pending).Length);
    }

    [Fact]
    [Trait("Stage", "System")]
    public void LinkedFoldersAreShownButCannotBeTraversedOrPrepared()
    {
        using var fixture = new TestDirectory();
        using var outside = new TestDirectory();
        string linked = Path.Join(fixture.Path, "linked-cloud");
        File.WriteAllText(Path.Join(outside.Path, "original.txt"), "keep");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/d", "/c", "mklink", "/J", linked, outside.Path },
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var entry = Assert.Single(CloudFiles.ReadDirectory(fixture.Path, default).Entries);
            Assert.True(entry.IsDirectory);
            Assert.False(entry.CanRequestOnlineOnly);
            Assert.Contains("not followed", entry.Availability);
            Assert.Throws<IOException>(() => CloudFiles.ReadDirectory(linked, default));
            Assert.Throws<IOException>(() => CloudFiles.Prepare(linked, default));
            Assert.Equal("keep", File.ReadAllText(Path.Join(outside.Path, "original.txt")));
        }
        finally { Directory.Delete(linked); }
    }

    [Fact]
    [Trait("Stage", "System")]
    public void LargeDirectoriesHaveAnExplicitBoundAndIncompleteCoverage()
    {
        using var fixture = new TestDirectory();
        for (int i = 0; i < 5001; i++) File.WriteAllBytes(Path.Join(fixture.Path, $"file-{i:D4}.bin"), []);

        var listing = CloudFiles.ReadDirectory(fixture.Path, default);

        Assert.Equal(5000, listing.Entries.Count);
        Assert.False(listing.Complete);
        Assert.Contains("5,000", Assert.Single(listing.Warnings));
        Assert.Equal(5001, Directory.GetFiles(fixture.Path).Length);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"\\server\share\cloud")]
    public void OnlyFullyQualifiedLocalPathsAreAccepted(string path)
    {
        Assert.Throws<IOException>(() => CloudFiles.ReadDirectory(path, default));
        Assert.Throws<IOException>(() => CloudFiles.Prepare(path, default));
    }

    [Fact]
    public void CancellationPrecedesFilesystemAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CloudFiles.ReadDirectory(@"Z:\not-accessed", cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => CloudFiles.Prepare(@"Z:\not-accessed", cancellation.Token));
    }
}
