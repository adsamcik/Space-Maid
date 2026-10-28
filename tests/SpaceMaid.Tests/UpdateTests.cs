using System.IO;
using System.Security.Cryptography;
using SpaceMaid.App;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class UpdateTests
{
    [Fact]
    public async Task FastCompletionDoesNotLeaveTheUpdaterBusyOrPreventTheNextOperation()
    {
        using var fixture = new TestDirectory();
        var manager = new StubUpdateManager(fixture.Path);
        using var updates = new AppUpdates(manager, Path.Join(fixture.Path, "settings.json"), Path.Join(fixture.Path, "Logs"));
        var previous = SynchronizationContext.Current;
        Task check;
        try
        {
            // A posted continuation can finish before its caller resumes.
            // Make that ordering deterministic instead of relying on load.
            SynchronizationContext.SetSynchronizationContext(new ImmediateContext());
            check = updates.CheckAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await check;
        Assert.False(updates.Busy); Assert.Null(updates.Operation);
        await updates.DownloadAsync();
        Assert.True(updates.Ready); Assert.Equal(1, manager.Downloads); Assert.False(updates.Busy);
    }

    private sealed class ImmediateContext : SynchronizationContext
    { public override void Post(SendOrPostCallback callback, object? state) => callback(state); }

    [Theory]
    [InlineData("owner/Space-Maid", "https://github.com/owner/Space-Maid")]
    [InlineData("https://github.com/owner/repo", null)]
    [InlineData("owner/repo?token=secret", null)]
    [InlineData("../repo", null)]
    [InlineData("owner/..", null)]
    [InlineData("", null)]
    public void ReleaseRepositoryIsAnOwnerAndName(string repository, string? expected)
        => Assert.Equal(expected, AppUpdates.RepositoryUrl(repository));

    [Fact]
    public async Task DevelopmentBuildCannotCheckOrDownloadAndPortableDataSurvivesBinaryReplacement()
    {
        using var fixture = new TestDirectory();
        using var updates = new AppUpdates(null, Path.Join(fixture.Path, "settings.json"), Path.Join(fixture.Path, "Logs"));
        Assert.False(updates.Supported); Assert.False(updates.AutomaticCheckDue);
        await updates.CheckAsync(); await updates.DownloadAsync(); Assert.Null(updates.Operation);
        Assert.Equal(Path.Join(fixture.Path, "UserData"), AppData.ResolveRoot(true, fixture.Path, "unused"));
        Assert.Equal(Path.Join(fixture.Path, "SpaceMaid"), AppData.ResolveRoot(false, null, fixture.Path));
        Assert.DoesNotContain("current", AppData.ResolveRoot(true, fixture.Path, "unused"));
    }

    [Fact]
    public async Task CheckOnlyOffersDownloadAndChecksAreRemembered()
    {
        using var fixture = new TestDirectory();
        var manager = new StubUpdateManager(fixture.Path);
        string settings = Path.Join(fixture.Path, "update-settings.json");
        using (var updates = new AppUpdates(manager, settings, Path.Join(fixture.Path, "Logs")))
        {
            Assert.True(updates.AutomaticCheckDue);
            await updates.CheckAsync();
            Assert.Equal("1.1.0", updates.AvailableVersion); Assert.False(updates.Ready);
            Assert.Equal(0, manager.Downloads); Assert.False(updates.AutomaticCheckDue);
            await updates.DownloadAsync();
            Assert.True(updates.Ready); Assert.Equal(1, manager.Downloads);
            Assert.True(updates.SetAutomaticChecks(false));
        }
        using var reopened = new AppUpdates(manager, settings, Path.Join(fixture.Path, "Logs"));
        Assert.False(reopened.AutomaticChecks); Assert.False(reopened.AutomaticCheckDue);
    }

    [Fact]
    public async Task FailedCheckDoesNotRetryAtEveryStartupButManualRetryRemainsAvailable()
    {
        using var fixture = new TestDirectory();
        var manager = new StubUpdateManager(fixture.Path) { CheckError = new IOException("Offline") };
        string settings = Path.Join(fixture.Path, "settings.json");
        using (var updates = new AppUpdates(manager, settings, Path.Join(fixture.Path, "Logs")))
        {
            await updates.CheckAsync(); Assert.NotNull(updates.Error); Assert.False(updates.AutomaticCheckDue);
        }
        using var reopened = new AppUpdates(manager, settings, Path.Join(fixture.Path, "Logs"));
        Assert.False(reopened.AutomaticCheckDue);
        manager.CheckError = null;
        await reopened.CheckAsync(); Assert.Equal("1.1.0", reopened.AvailableVersion);
    }

    [Fact]
    public async Task AutomaticDownloadsRequireSavedOptInAndRespectDailyChecks()
    {
        using var fixture = new TestDirectory();
        var manager = new StubUpdateManager(fixture.Path);
        string settings = Path.Join(fixture.Path, "settings.json");
        using (var updates = new AppUpdates(manager, settings, Path.Join(fixture.Path, "Logs")))
        {
            Assert.False(updates.AutomaticDownloads);
            Assert.True(updates.SetAutomaticDownloads(true));
        }
        using var reopened = new AppUpdates(manager, settings, Path.Join(fixture.Path, "Logs"));
        Assert.True(reopened.AutomaticDownloads);
        await reopened.RunAutomaticAsync();
        Assert.True(reopened.Ready); Assert.Equal(1, manager.Downloads);
        await reopened.RunAutomaticAsync();
        Assert.Equal(1, manager.Checks); Assert.Equal(1, manager.Downloads);
        Assert.True(reopened.SetAutomaticChecks(false));
        await reopened.CheckAsync();
        Assert.Equal(1, manager.Downloads);
    }

    [Fact]
    public void ExistingSettingsKeepAutomaticDownloadsOffAndFailedSavesRevertTheChoice()
    {
        using var fixture = new TestDirectory();
        string settings = Path.Join(fixture.Path, "settings.json");
        File.WriteAllText(settings, "{\"AutomaticChecks\":true,\"LastCheckUtc\":null}");
        using var updates = new AppUpdates(new StubUpdateManager(fixture.Path), settings, Path.Join(fixture.Path, "Logs"));
        Assert.False(updates.AutomaticDownloads);
        File.Delete(settings); Directory.CreateDirectory(settings);
        Assert.False(updates.SetAutomaticDownloads(true));
        Assert.False(updates.AutomaticDownloads); Assert.NotNull(updates.Error);
    }

    [Theory]
    [InlineData("0.9.0")]
    [InlineData("1.0.0")]
    [InlineData("1.1.0-beta.1")]
    public async Task AutomaticDownloadsRejectDowngradesSameVersionsAndPrereleasesOnStable(string version)
    {
        using var fixture = new TestDirectory();
        var manager = new StubUpdateManager(fixture.Path);
        manager.Target.Version = SemanticVersion.Parse(version);
        manager.Target.FileName = $"SpaceMaid-{version}-full.nupkg";
        using var updates = new AppUpdates(manager, Path.Join(fixture.Path, "settings.json"), Path.Join(fixture.Path, "Logs"));
        updates.SetAutomaticDownloads(true);
        await updates.RunAutomaticAsync();
        Assert.NotNull(updates.Error); Assert.Null(updates.AvailableVersion);
        Assert.False(updates.Ready); Assert.Equal(0, manager.Downloads);
    }

    [Fact]
    public async Task CancelledAutomaticDownloadCanBeDownloadedManuallyLater()
    {
        using var fixture = new TestDirectory();
        var manager = new StubUpdateManager(fixture.Path) { HoldDownload = true };
        using var updates = new AppUpdates(manager, Path.Join(fixture.Path, "settings.json"), Path.Join(fixture.Path, "Logs"));
        updates.SetAutomaticDownloads(true);
        var automatic = updates.RunAutomaticAsync();
        await manager.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        updates.Cancel(); await automatic;
        Assert.False(updates.Ready); Assert.False(updates.Busy);
        manager.HoldDownload = false;
        await updates.DownloadAsync(); Assert.True(updates.Ready);
    }

    [Fact]
    public async Task CancellationNeverMakesAnIncompletePackageReady()
    {
        using var fixture = new TestDirectory();
        var manager = new StubUpdateManager(fixture.Path) { HoldDownload = true };
        using var updates = new AppUpdates(manager, Path.Join(fixture.Path, "settings.json"), Path.Join(fixture.Path, "Logs"));
        await updates.CheckAsync();
        var download = updates.DownloadAsync();
        await manager.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(updates.Busy); updates.Cancel(); await download;
        Assert.False(updates.Busy); Assert.False(updates.Ready); Assert.Null(updates.Error);
        manager.HoldDownload = false;
        await updates.DownloadAsync(); Assert.True(updates.Ready);
    }

    [Fact]
    public async Task InvalidReleaseAndMalformedSettingsFailClosed()
    {
        using var fixture = new TestDirectory();
        string settings = Path.Join(fixture.Path, "settings.json");
        File.WriteAllText(settings, "broken-json");
        var manager = new StubUpdateManager(fixture.Path);
        manager.Target.PackageId = "OtherApplication";
        using var updates = new AppUpdates(manager, settings, Path.Join(fixture.Path, "Logs"));
        Assert.False(updates.AutomaticChecks);
        await updates.CheckAsync(); Assert.Null(updates.AvailableVersion); Assert.NotNull(updates.Error);
        Assert.False(updates.Ready); Assert.Equal(0, manager.Downloads);
    }

    [Theory]
    [InlineData("../outside.nupkg", true)]
    [InlineData("package.nupkg", false)]
    [InlineData("package.exe", true)]
    public void UpdateTargetsNeedSafePackageNameAndStrongChecksum(string file, bool hasHash)
    {
        var target = Target(); target.FileName = file; if (!hasHash) target.SHA256 = null!;
        Assert.Throws<InvalidDataException>(() => AppUpdates.ValidateTarget(target, "SpaceMaid"));
    }

    [Fact]
    public async Task CachedPackageIsReverifiedAndTamperingCanBeRetried()
    {
        using var fixture = new TestDirectory();
        string packages = Directory.CreateDirectory(Path.Join(fixture.Path, "packages")).FullName;
        byte[] expected = "trusted package content"u8.ToArray();
        var target = Target(); target.Size = expected.Length;
        target.SHA256 = Convert.ToHexString(SHA256.HashData(expected));
        string cached = Path.Join(packages, target.FileName);
        var locator = new TestVelopackLocator("SpaceMaid", "1.0.0", packages);
        var manager = new VerifiedUpdateManager(new SimpleFileSource(new DirectoryInfo(fixture.Path)), locator);
        var update = new UpdateInfo(target, false, null!, []);
        byte[] tampered = (byte[])expected.Clone(); tampered[0] ^= 1;
        File.WriteAllBytes(cached, tampered);
        await Assert.ThrowsAsync<ChecksumFailedException>(() => manager.DownloadUpdatesAsync(update));
        Assert.False(File.Exists(cached));
        File.WriteAllBytes(cached, expected);
        await manager.DownloadUpdatesAsync(update);
        Assert.Equal(expected, File.ReadAllBytes(cached));
        File.WriteAllBytes(cached, tampered);
        await Assert.ThrowsAsync<ChecksumFailedException>(() => manager.VerifyPreparedAsync(target));
    }

    internal static VelopackAsset Target() => new()
    {
        PackageId = "SpaceMaid", Version = SemanticVersion.Parse("1.1.0"), Type = VelopackAssetType.Full,
        FileName = "SpaceMaid-1.1.0-full.nupkg", Size = 1, SHA256 = new string('A', 64)
    };

    internal sealed class StubUpdateManager : UpdateManager
    {
        internal VelopackAsset Target { get; } = UpdateTests.Target();
        internal int Downloads { get; private set; }
        internal int Checks { get; private set; }
        internal bool HoldDownload { get; set; }
        internal Exception? CheckError { get; set; }
        internal TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StubUpdateManager(string path) : base(new SimpleFileSource(new DirectoryInfo(path)),
            locator: new TestVelopackLocator("SpaceMaid", "1.0.0", path)) { }
        public override Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            Checks++;
            return CheckError is { } error ? Task.FromException<UpdateInfo?>(error)
                : Task.FromResult<UpdateInfo?>(new(Target, false, null!, []));
        }
        public override async Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress = null, CancellationToken cancelToken = default)
        {
            Downloads++; DownloadStarted.TrySetResult();
            if (HoldDownload) await Task.Delay(Timeout.Infinite, cancelToken);
            progress?.Invoke(100);
        }
    }
}
