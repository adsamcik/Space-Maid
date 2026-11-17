using System.IO;
using System.Text.Json;
using SpaceMaid.Core.IO;
using SpaceMaid.Extensions.Apps;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class AppFolderSettingsTests
{
    [Fact]
    public void AddedUnavailableFoldersSurviveRestartAndShortcutRemovalDoesNotTouchFiles()
    {
        using var fixture = new TestDirectory();
        string settingsPath = Path.Join(fixture.Path, "app-folders.json");
        string installed = Directory.CreateDirectory(Path.Join(fixture.Path, "Installed app")).FullName;
        string marker = Path.Join(installed, "app.exe"); File.WriteAllBytes(marker, [1, 2, 3]);
        string offline = Path.Join(fixture.Path, "Temporarily unavailable app");
        var settings = new AppFolderSettings(new DiskAccess(), settingsPath);
        Assert.True(settings.Change(installed, true)); Assert.True(settings.Change(offline, true));
        Assert.True(settings.Change(installed, true)); Assert.Equal(2, settings.Folders.Count);
        var restarted = new AppFolderSettings(new DiskAccess(), settingsPath);
        Assert.True(restarted.Reload()); Assert.Equal(new[] { installed, offline }, restarted.Folders);
        Assert.True(restarted.Change(installed, false));
        Assert.Equal(offline, Assert.Single(restarted.Folders)); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(marker));
        Assert.False(Directory.Exists(offline));
    }

    [Fact]
    public void UnreadableOrCorruptSettingsPreserveKnownFoldersAndOriginalBytesUntilRepair()
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "app-folders.json"), first = Path.Join(fixture.Path, "First"), next = Path.Join(fixture.Path, "Next");
        var settings = new AppFolderSettings(new DiskAccess(), path);
        Assert.True(settings.Change(first, true)); byte[] original = File.ReadAllBytes(path);
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(settings.Reload()); Assert.NotNull(settings.Error); Assert.False(settings.CanChange);
            Assert.False(settings.Change(next, true)); Assert.Equal(first, Assert.Single(settings.Folders));
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        byte[] corrupt = "not saved folder json"u8.ToArray(); File.WriteAllBytes(path, corrupt);
        Assert.False(settings.Reload()); Assert.False(settings.Change(first, false));
        Assert.Equal(first, Assert.Single(settings.Folders)); Assert.Equal(corrupt, File.ReadAllBytes(path));
        File.WriteAllBytes(path, original);
        Assert.True(settings.Reload()); Assert.Null(settings.Error); Assert.True(settings.CanChange);
        Assert.True(settings.Change(next, true)); Assert.Equal(2, settings.Folders.Count);
    }

    [Fact]
    public void ChangesReloadDurablePreferencesAndRejectInvalidOrOversizedInput()
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "app-folders.json");
        var first = new AppFolderSettings(new DiskAccess(), path); var second = new AppFolderSettings(new DiskAccess(), path);
        Assert.True(first.Reload()); Assert.True(second.Change(Path.Join(fixture.Path, "External"), true));
        Assert.True(first.Change(Path.Join(fixture.Path, "Local"), true)); Assert.Equal(2, first.Folders.Count);
        var original = File.ReadAllBytes(path);
        Assert.False(first.Change(Path.GetPathRoot(fixture.Path)!, true)); Assert.True(first.CanChange); Assert.Equal(original, File.ReadAllBytes(path));
        File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Folders = new[] { "relative" } }));
        Assert.False(first.Reload()); Assert.Equal(2, first.Folders.Count);
        File.WriteAllBytes(path, new byte[64 * 1024 + 1]); Assert.False(first.Reload()); Assert.Equal(2, first.Folders.Count);
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }
}
