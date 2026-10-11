using System.IO;
using System.Text.Json;
using SpaceMaid.App;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class DriveHistoryTests
{
    [Fact]
    public async Task SelectionAndScanHintsRoundTripWithoutLosingTheLastChosenFolder()
    {
        using var directory = new TestDirectory();
        string file = Path.Join(directory.Path, "history.json");
        var history = new DriveHistoryStore(file);
        await history.LoadAsync();
        Assert.Null(history.Error);
        history.RememberSelection("volume-one", @"G:\", @"G:\Projects\App");
        history.RecordScan("volume-one", @"G:\", @"G:\", new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc), true, false,
            [new("Build files", 80), new("Packages", 120), new("Worktrees", 40), new("Small", 10), new("Empty", 0), new("Invalid", -1)]);
        history.RememberSelection("volume-one", @"G:\", @"G:\Photos");
        await history.SaveAsync();
        Assert.Null(history.Error);

        var restored = new DriveHistoryStore(file);
        await restored.LoadAsync();
        Assert.Null(restored.Error);
        Assert.Equal("volume-one", restored.LastVolumeId);
        var entry = Assert.IsType<DriveHistoryEntry>(restored.Find("volume-one", @"G:\"));
        Assert.Equal(@"G:\Photos", entry.LastPath);
        Assert.Equal(@"G:\", entry.ScanPath);
        Assert.Equal(new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc), entry.ScannedAtUtc);
        Assert.True(entry.HasAllocationData);
        Assert.False(entry.PartialScan);
        Assert.Equal(new[] { "Packages", "Build files", "Worktrees" }, entry.Recommendations.Select(value => value.Name));
        Assert.All(entry.Recommendations, value => Assert.True(value.Bytes > 0));
    }

    [Fact]
    public void VolumeIdentityRebasesPathsWithoutSharingAnotherVolumesHistory()
    {
        var history = new DriveHistoryStore("unused.json");
        history.RememberSelection("volume-one", @"G:\", @"G:\Projects\App");
        history.RecordScan("volume-one", @"G:\", @"G:\Projects", DateTime.UtcNow, true, true, [new("Build files", 100)]);
        var rebased = Assert.IsType<DriveHistoryEntry>(history.Find("volume-one", @"H:\"));
        Assert.Equal(@"H:\Projects\App", rebased.LastPath);
        Assert.Equal(@"H:\Projects", rebased.ScanPath);
        Assert.Equal(@"H:\", rebased.RootPath);
        Assert.True(rebased.PartialScan);
        Assert.Null(history.Find("volume-two", @"G:\"));
        history.RememberSelection("volume-one", @"H:\", @"H:\Photos");
        var selected = history.Find("volume-one", @"H:\")!;
        Assert.Equal(@"H:\Photos", selected.LastPath);
        Assert.Equal(@"H:\Projects", selected.ScanPath);
        rebased.Recommendations[0] = new("Changed outside store", 999);
        Assert.Equal("Build files", selected.Recommendations[0].Name);
    }

    [Fact]
    public async Task HistoryKeepsTwentyFourRecentVolumesAndBoundedJson()
    {
        using var directory = new TestDirectory();
        string file = Path.Join(directory.Path, "history.json");
        var history = new DriveHistoryStore(file);
        for (int i = 0; i < 30; i++) history.RememberSelection("volume-" + i, @"G:\", @"G:\Projects");
        Assert.Null(history.Find("volume-0", @"G:\"));
        Assert.NotNull(history.Find("volume-29", @"G:\"));
        await history.SaveAsync();
        using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(file)))
            Assert.Equal(24, json.RootElement.GetProperty("Entries").GetArrayLength());

        // Path lengths are bounded in characters, and the persisted budget also covers JSON escaping.
        string longUnicodePath = @"G:\" + new string('漢', 3500);
        for (int i = 0; i < 24; i++)
        {
            history.RememberSelection("unicode-" + i, @"G:\", longUnicodePath);
            history.RecordScan("unicode-" + i, @"G:\", longUnicodePath, DateTime.UtcNow, true, false, [new("Build files", 100)]);
        }
        await history.SaveAsync();
        Assert.Null(history.Error);
        Assert.InRange(new FileInfo(file).Length, 1, 128 * 1024);
        var restored = new DriveHistoryStore(file);
        await restored.LoadAsync();
        Assert.Null(restored.Error);
        Assert.Equal("unicode-23", restored.LastVolumeId);
        Assert.Equal(longUnicodePath, restored.Find("unicode-23", @"G:\")!.LastPath);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\":2,\"Entries\":[]}")]
    [InlineData("{\"Version\":1,\"Entries\":null}")]
    public async Task CorruptOrUnsupportedHistoryGivesAnEmptyStateAndAnError(string contents)
    {
        using var directory = new TestDirectory();
        string file = Path.Join(directory.Path, "history.json");
        await File.WriteAllTextAsync(file, contents);
        var history = new DriveHistoryStore(file);
        history.RememberSelection("volume-one", @"G:\", @"G:\Projects");
        await history.LoadAsync();
        Assert.NotNull(history.Error);
        Assert.Null(history.LastVolumeId);
        Assert.Null(history.Find("volume-one", @"G:\"));
        history.RememberSelection("volume-two", @"H:\", @"H:\");
        await history.SaveAsync();
        Assert.Null(history.Error);
        Assert.NotNull(history.Find("volume-two", @"H:\"));
    }

    [Fact]
    public async Task OversizedOrEscapingHistoryIsRejectedAndBadSelectionsAreIgnored()
    {
        using var directory = new TestDirectory();
        string file = Path.Join(directory.Path, "history.json");
        var history = new DriveHistoryStore(file);
        await File.WriteAllTextAsync(file, new string(' ', 128 * 1024 + 1));
        await history.LoadAsync();
        Assert.NotNull(history.Error);
        var outside = new DriveHistoryEntry("volume-one", @"G:\", @"C:\Windows", DateTime.UtcNow,
            DateTime.UtcNow, @"G:\Projects", true, false, [new("Build files", 100)]);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { Version = 1, LastVolumeId = "volume-one", Entries = new[] { outside } }));
        await history.LoadAsync();
        Assert.NotNull(history.Error);
        Assert.Null(history.Find("volume-one", @"G:\"));
        history.RememberSelection("volume-one", @"G:\", @"C:\Windows");
        history.RememberSelection("volume-one", @"G:\", @"G:relative");
        history.RememberSelection("volume-one", @"G:\Projects", @"G:\Projects\App");
        Assert.Null(history.Find("volume-one", @"G:\"));
        history.RememberSelection("volume-one", @"G:\", @"G:\Projects\..\Photos");
        Assert.Equal(@"G:\Photos", history.Find("volume-one", @"G:\")!.LastPath);
    }

    [Fact]
    public async Task ConcurrentSavesPublishCompleteJsonAndWriteFailuresLeaveHintsAvailable()
    {
        using var directory = new TestDirectory();
        string file = Path.Join(directory.Path, "history.json");
        var history = new DriveHistoryStore(file);
        history.RememberSelection("volume-one", @"G:\", @"G:\Projects");
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => history.SaveAsync()));
        Assert.Null(history.Error);
        var restored = new DriveHistoryStore(file);
        await restored.LoadAsync();
        Assert.Null(restored.Error);
        Assert.NotNull(restored.Find("volume-one", @"G:\"));
        Assert.Single(Directory.EnumerateFiles(directory.Path));

        var blocked = new DriveHistoryStore(Path.Join(file, "blocked.json"));
        blocked.RememberSelection("volume-one", @"G:\", @"G:\Projects");
        await blocked.SaveAsync();
        Assert.NotNull(blocked.Error);
        Assert.NotNull(blocked.Find("volume-one", @"G:\"));
    }
}
