using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.IO;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class StartupScanningPreferenceTests
{
    [Fact]
    public void StandardChoiceSurvivesReloadAndDoesNotRequireAnotherOffer()
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "new-profile", "startup.json");
        var files = new DiskAccess(() => null);
        Assert.True(StartupScanningPreference.Load(path, files).ShouldOffer);
        Assert.True(StartupScanningPreference.RememberStandard(path, files).Dismissed);
        byte[] saved = File.ReadAllBytes(path);
        var restored = StartupScanningPreference.Load(path, files);
        Assert.True(restored.Dismissed);
        Assert.False(restored.ShouldOffer);
        Assert.Null(restored.Error);
        Assert.True(StartupScanningPreference.RememberStandard(path, files).Dismissed);
        Assert.Equal(saved, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"Version\":2,\"StandardScanningChosen\":true}")]
    [InlineData("{\"Version\":1,\"StandardScanningChosen\":false}")]
    public void UnreadPreferencesUseStandardWithoutReplacingOriginals(string contents)
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "startup.json");
        File.WriteAllText(path, contents);
        var files = new DiskAccess(() => null);
        var choice = StartupScanningPreference.Load(path, files);
        Assert.False(choice.ShouldOffer);
        Assert.NotNull(choice.Error);
        Assert.NotNull(StartupScanningPreference.RememberStandard(path, files).Error);
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Fact]
    public void TemporaryReadFailureDoesNotBlockStandardScanningOrOverwriteTheChoice()
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "startup.json");
        var files = new DiskAccess(() => null);
        Assert.True(StartupScanningPreference.RememberStandard(path, files).Dismissed);
        byte[] saved = File.ReadAllBytes(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var unavailable = StartupScanningPreference.Load(path, files);
            Assert.False(unavailable.ShouldOffer);
            Assert.NotNull(unavailable.Error);
            Assert.NotNull(StartupScanningPreference.RememberStandard(path, files).Error);
        }
        Assert.True(StartupScanningPreference.Load(path, files).Dismissed);
        Assert.Equal(saved, File.ReadAllBytes(path));
    }
}
