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
    public async Task FailedChecksUsePersistedBoundedBackoffAndSuccessRestoresDailyFreshness()
    {
        using var fixture = new TestDirectory();
        var clock = new UpdateClock();
        var manager = new StubUpdateManager(fixture.Path) { CheckError = new IOException("Offline") };
        string settings = Path.Join(fixture.Path, "settings.json");
        foreach (int minutes in new[] { 15, 30, 60, 120, 120 })
        {
            using (var updates = new AppUpdates(manager, settings, Path.Join(fixture.Path, "Logs"), clock))
            {
                Assert.True(updates.AutomaticCheckDue);
                await updates.RunAutomaticAsync();
                Assert.NotNull(updates.Error); Assert.True(updates.CheckRetryAvailable);
                Assert.Equal(clock.Now.AddMinutes(minutes), updates.NextAutomaticCheckUtc);
            }
            using var reopenedFailure = new AppUpdates(manager, settings, Path.Join(fixture.Path, "ReopenedLogs"), clock);
            Assert.True(reopenedFailure.CheckRetryAvailable); Assert.False(reopenedFailure.AutomaticCheckDue);
            int checks = manager.Checks;
            await reopenedFailure.RunAutomaticAsync(); Assert.Equal(checks, manager.Checks);
            clock.Now = clock.Now.AddMinutes(minutes).AddTicks(-1);
            Assert.False(reopenedFailure.AutomaticCheckDue);
            clock.Now = clock.Now.AddTicks(1);
            Assert.True(reopenedFailure.AutomaticCheckDue);
        }
        using var reopened = new AppUpdates(manager, settings, Path.Join(fixture.Path, "Logs"), clock);
        manager.CheckError = null;
        await reopened.CheckAsync(); Assert.Equal("1.1.0", reopened.AvailableVersion);
        Assert.False(reopened.CheckRetryAvailable); Assert.True(reopened.AvailableVerified);
        Assert.Equal(clock.Now.AddDays(1), reopened.NextAutomaticCheckUtc);
        Assert.False(reopened.AutomaticCheckDue);
        clock.Now = clock.Now.AddDays(1); Assert.True(reopened.AutomaticCheckDue);
    }

    [Fact]
    public async Task ManualRetryBypassesBackoffAndEarlierAvailabilityCannotAuthorizeANewDownload()
    {
        using var fixture = new TestDirectory();
        var clock = new UpdateClock();
        var manager = new StubUpdateManager(fixture.Path);
        using var updates = new AppUpdates(manager, Path.Join(fixture.Path, "settings.json"), Path.Join(fixture.Path, "Logs"), clock);
        await updates.CheckAsync(); Assert.True(updates.AvailableVerified);
        manager.CheckError = new IOException("Offline");
        await updates.CheckAsync();
        Assert.Equal("1.1.0", updates.AvailableVersion); Assert.False(updates.AvailableVerified);
        Assert.False(updates.AutomaticCheckDue); Assert.Contains("Earlier result", updates.Status);
        await updates.DownloadAsync(); Assert.Equal(0, manager.Downloads); Assert.False(updates.Ready);
        manager.CheckError = null;
        await updates.CheckAsync(); // Explicit retry is allowed before the backoff expires.
        Assert.True(updates.AvailableVerified); Assert.False(updates.CheckRetryAvailable);
        await updates.DownloadAsync(); Assert.True(updates.Ready); Assert.Equal(1, manager.Downloads);
        manager.CheckError = new IOException("Offline again");
        await updates.CheckAsync();
        Assert.True(updates.Ready); Assert.False(updates.AvailableVerified);
        Assert.Equal("1.1.0", updates.AvailableVersion); // A verified downloaded receipt remains usable offline.
        Assert.Equal(1, manager.Downloads);
    }

    [Fact]
    public void InterruptedCheckUsesPersistedAttemptBackoffAndFutureClockCannotSuppressChecks()
    {
        using var fixture = new TestDirectory();
        var clock = new UpdateClock();
        string settings = Path.Join(fixture.Path, "settings.json");
        File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(new
        { AutomaticChecks = true, LastCheckUtc = (DateTimeOffset?)null, LastAttemptUtc = clock.Now, FailedChecks = 1 }));
        using (var updates = new AppUpdates(new StubUpdateManager(fixture.Path), settings, Path.Join(fixture.Path, "Logs"), clock))
        { Assert.False(updates.AutomaticCheckDue); Assert.True(updates.CheckRetryAvailable); }
        clock.Now = clock.Now.AddHours(-1);
        using var rewound = new AppUpdates(new StubUpdateManager(fixture.Path), settings, Path.Join(fixture.Path, "RewoundLogs"), clock);
        Assert.True(rewound.AutomaticCheckDue);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(15, true)]
    [InlineData(60, true)]
    [InlineData(-60, true)]
    public void LegacyCheckTimestampIsAnUnknownAttemptWithBoundedRetry(int ageMinutes, bool due)
    {
        using var fixture = new TestDirectory();
        var clock = new UpdateClock();
        string settings = Path.Join(fixture.Path, "settings.json");
        var legacyAttempt = clock.Now.AddMinutes(-ageMinutes);
        File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(new
        { AutomaticChecks = true, AutomaticDownloads = false, LastCheckUtc = legacyAttempt }));
        using (var updates = new AppUpdates(new StubUpdateManager(fixture.Path), settings, Path.Join(fixture.Path, "Logs"), clock))
        {
            Assert.Equal(due, updates.AutomaticCheckDue);
            Assert.Contains("No successful check recorded", updates.CheckHistory);
            Assert.DoesNotContain("Last successful check:", updates.CheckHistory);
            Assert.False(updates.AutomaticDownloads); Assert.False(updates.Ready);
            if (ageMinutes >= 0)
            {
                Assert.Equal(legacyAttempt.AddMinutes(15), updates.NextAutomaticCheckUtc);
                Assert.True(updates.CheckRetryAvailable);
                Assert.Contains("outcome unverified", updates.Status);
            }
            Assert.True(updates.SetAutomaticChecks(true)); // Persist the migrated representation.
        }
        using var reopened = new AppUpdates(new StubUpdateManager(fixture.Path), settings, Path.Join(fixture.Path, "ReopenedLogs"), clock);
        Assert.Equal(due, reopened.AutomaticCheckDue);
        Assert.DoesNotContain("Last successful check:", reopened.CheckHistory);
        Assert.False(reopened.AutomaticDownloads);
    }

    [Fact]
    public async Task RecheckedLegacyAttemptPersistsGenuineSuccessfulDailyFreshness()
    {
        using var fixture = new TestDirectory();
        var clock = new UpdateClock();
        string settings = Path.Join(fixture.Path, "settings.json");
        File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(new
        { AutomaticChecks = true, LastCheckUtc = clock.Now }));
        using (var updates = new AppUpdates(new StubUpdateManager(fixture.Path), settings, Path.Join(fixture.Path, "Logs"), clock))
        {
            Assert.False(updates.AutomaticCheckDue);
            await updates.CheckAsync(); // A deliberate retry can resolve the unknown outcome immediately.
            Assert.True(updates.AvailableVerified); Assert.False(updates.CheckRetryAvailable);
            Assert.Contains("Last successful check:", updates.CheckHistory);
            Assert.Equal(clock.Now.AddDays(1), updates.NextAutomaticCheckUtc);
        }
        using var reopened = new AppUpdates(new StubUpdateManager(fixture.Path), settings, Path.Join(fixture.Path, "ReopenedLogs"), clock);
        Assert.False(reopened.AutomaticCheckDue); Assert.False(reopened.CheckRetryAvailable);
        Assert.Contains("Last successful check:", reopened.CheckHistory);
        Assert.Equal(clock.Now.AddDays(1), reopened.NextAutomaticCheckUtc);
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
    [InlineData(1439, 2880, false)]
    [InlineData(2880, 1439, false)]
    [InlineData(1440, 1440, true)]
    [InlineData(1441, 1441, true)]
    [InlineData(-60, 2880, false)]
    [InlineData(2880, -60, false)]
    [InlineData(null, 2880, false)]
    [InlineData(2880, null, false)]
    public async Task AutomaticDownloadRequiresBothReleaseAndPackageToBe24HoursOld(int? publishedMinutes, int? changedMinutes, bool allowed)
    {
        using var fixture = new TestDirectory();
        var clock = new UpdateClock();
        var manager = new StubUpdateManager(fixture.Path);
        manager.Release.PublishedUtc = publishedMinutes is { } p ? clock.Now.AddMinutes(-p) : null;
        manager.Release.PackageModifiedUtc[manager.Target.FileName] = changedMinutes is { } m ? clock.Now.AddMinutes(-m) : null;
        using var updates = new AppUpdates(manager, Path.Join(fixture.Path, "settings.json"), Path.Join(fixture.Path, "Logs"), clock);
        updates.SetAutomaticDownloads(true);
        await updates.RunAutomaticAsync();
        Assert.Equal(allowed, updates.Ready); Assert.Equal(allowed ? 1 : 0, manager.Downloads);
        Assert.Equal("1.1.0", updates.AvailableVersion); Assert.Null(updates.Error);
        if (!allowed)
        {
            Assert.Contains(publishedMinutes is null || changedMinutes is null ? "release age unavailable" : "24-hour protection", updates.Status);
            await updates.DownloadAsync(); // A deliberate manual update can bypass the waiting period.
            Assert.True(updates.Ready); Assert.Equal(1, manager.Downloads);
        }
    }

    [Fact]
    public async Task ProtectionPeriodIncludesTheExact24HourBoundaryAndRechecksPackageChanges()
    {
        using var fixture = new TestDirectory();
        var clock = new UpdateClock();
        var manager = new StubUpdateManager(fixture.Path);
        manager.Release.PublishedUtc = clock.Now.AddHours(-24).AddTicks(1);
        manager.Release.PackageModifiedUtc[manager.Target.FileName] = clock.Now.AddHours(-48);
        using var updates = new AppUpdates(manager, Path.Join(fixture.Path, "settings.json"), Path.Join(fixture.Path, "Logs"), clock);
        updates.SetAutomaticDownloads(true);
        await updates.CheckAsync(); Assert.Equal(0, manager.Downloads);
        clock.Now = clock.Now.AddTicks(1);
        await updates.CheckAsync(); Assert.Equal(1, manager.Downloads); Assert.True(updates.Ready);
        var replacement = Target(); replacement.SHA256 = new string('B', 64);
        manager.Target = FixtureSource.Attach(replacement, manager.Release);
        // A new upload resets the wait, even on the same old release tag.
        manager.Release.PackageModifiedUtc[manager.Target.FileName] = clock.Now;
        await updates.CheckAsync(); Assert.Equal(1, manager.Downloads); Assert.False(updates.Ready);
        clock.Now = clock.Now.AddHours(24);
        await updates.RunAutomaticAsync(); Assert.Equal(2, manager.Downloads); Assert.True(updates.Ready);
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
        internal VelopackAsset Target { get; set; }
        internal GitHubUpdateSource.TimedRelease Release { get; }
        internal int Downloads { get; private set; }
        internal int Checks { get; private set; }
        internal bool HoldDownload { get; set; }
        internal Exception? CheckError { get; set; }
        internal TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StubUpdateManager(string path) : base(new SimpleFileSource(new DirectoryInfo(path)),
            locator: new TestVelopackLocator("SpaceMaid", "1.0.0", path))
        {
            var target = UpdateTests.Target();
            var old = DateTimeOffset.UtcNow.AddDays(-2);
            Release = new(new GithubRelease(), old, new() { [target.FileName] = old });
            Target = FixtureSource.Attach(target, Release);
        }
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

    private sealed class FixtureSource : GithubSource
    {
        private FixtureSource() : base("https://github.com/owner/Space-Maid", null, true) { }
        internal static VelopackAsset Attach(VelopackAsset asset, GithubRelease release) => new GitBaseAsset(asset, release);
    }

    private sealed class UpdateClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
