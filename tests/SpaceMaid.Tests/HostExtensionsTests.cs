using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class HostExtensionsTests
{
    [Fact]
    public void ToolMetadataKeepsCaseSensitivePathsDistinctAndRejectsAmbiguousFallback()
    {
        string lower = @"C:\Photos\photo.png", upper = @"C:\Photos\PHOTO.PNG";
        Assert.Equal(lower, ExifToolBroker.MatchReturnedPath([lower, upper], "C:/Photos/photo.png"));
        Assert.Equal(upper, ExifToolBroker.MatchReturnedPath([lower, upper], "C:/Photos/PHOTO.PNG"));
        Assert.Equal(lower, ExifToolBroker.MatchReturnedPath([lower], "c:/photos/PHOTO.png"));
        Assert.Throws<InvalidDataException>(() => ExifToolBroker.MatchReturnedPath([lower, upper], "c:/photos/Photo.png"));
        Assert.Throws<InvalidDataException>(() => ExifToolBroker.MatchReturnedPath([lower], "C:/Other/photo.png"));
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("missing lists")]
    [InlineData("null lists")]
    [InlineData("invalid directory")]
    public async Task InvalidSettingsBlockAllExtensionsAndExplicitRepairDoesNotEnableOthers(string kind)
    {
        using var fixture = new TestDirectory();
        string bundles = Directory.CreateDirectory(Path.Join(fixture.Path, "bundles")).FullName;
        string data = Directory.CreateDirectory(Path.Join(fixture.Path, "data")).FullName;
        MakeManifest(Path.Join(bundles, "first"), "test.first", 1);
        MakeManifest(Path.Join(bundles, "second"), "test.second", 1);
        string settings = kind switch
        {
            "oversized" => new string(' ', 65537),
            "missing lists" => "{}",
            "null lists" => "{\"Directories\":null,\"DisabledIds\":[]}",
            _ => "{\"Directories\":[{\"Directory\":\"relative\"}],\"DisabledIds\":[]}"
        };
        string settingsPath = Path.Join(data, "extensions.json"); File.WriteAllText(settingsPath, settings);
        await using var runtime = new ExtensionRuntime(() => null, () => false, _ => { }, data, bundles, Path.Join(fixture.Path, "logs"), administrator: false);
        await runtime.InitializeAsync();
        Assert.NotNull(runtime.SettingsError);
        Assert.All(runtime.Entries, entry => { Assert.Equal("Unavailable", entry.Status); Assert.Null(entry.View); Assert.False(runtime.IsEnabled(entry)); });
        Assert.Equal(settings, File.ReadAllText(settingsPath));
        Assert.False(Directory.Exists(Path.Join(data, "test.first")));
        Assert.False(Directory.Exists(Path.Join(data, "test.second")));
        var first = Assert.Single(runtime.Entries, entry => entry.Id == "test.first");
        runtime.SetEnabled(first, true);
        Assert.Null(runtime.SettingsError); Assert.True(runtime.IsEnabled(first));
        Assert.False(runtime.IsEnabled(Assert.Single(runtime.Entries, entry => entry.Id == "test.second")));
        var saved = JsonSerializer.Deserialize<ExtensionSettings>(File.ReadAllText(settingsPath))!;
        Assert.Contains("test.second", saved.DisabledIds); Assert.DoesNotContain("test.first", saved.DisabledIds);
    }

    [Fact]
    public async Task InvalidTrustedDirectoryReportsFailureWithoutChangingSavedSettings()
    {
        using var fixture = new TestDirectory();
        string bundles = Directory.CreateDirectory(Path.Join(fixture.Path, "bundles")).FullName;
        string data = Directory.CreateDirectory(Path.Join(fixture.Path, "data")).FullName;
        string settingsPath = Path.Join(data, "extensions.json");
        string original = JsonSerializer.Serialize(new ExtensionSettings([], ["test.disabled"])); File.WriteAllText(settingsPath, original);
        await using var runtime = new ExtensionRuntime(() => null, () => false, _ => { }, data, bundles, Path.Join(fixture.Path, "logs"), administrator: false);
        await runtime.InitializeAsync();
        string ordinary = Directory.CreateDirectory(Path.Join(fixture.Path, "ordinary")).FullName;
        Assert.False(runtime.TryAddTrustedDirectory(ordinary, out var manifest, out var error));
        Assert.Null(manifest); Assert.Contains("extension.json", error);
        string incompatible = Path.Join(fixture.Path, "incompatible"); MakeManifest(incompatible, "test.invalid", 99);
        Assert.False(runtime.TryAddTrustedDirectory(incompatible, out manifest, out error));
        Assert.Null(manifest); Assert.Contains("API version 99", error);
        string missing = Path.Join(fixture.Path, "missing"); MakeManifest(missing, "test.missing", 1);
        Assert.False(runtime.TryAddTrustedDirectory(missing, out manifest, out error));
        Assert.Null(manifest); Assert.Contains("DLL is missing", error);
        Assert.Equal(original, File.ReadAllText(settingsPath));
    }

    [Fact]
    public async Task DisabledAndIncompatibleExtensionsDoNotExecuteAndFailuresStayVisible()
    {
        using var fixture = new TestDirectory();
        string bundles = Directory.CreateDirectory(Path.Join(fixture.Path, "bundles")).FullName;
        string data = Directory.CreateDirectory(Path.Join(fixture.Path, "data")).FullName;
        MakeManifest(Path.Join(bundles, "disabled"), "test.disabled", 1);
        MakeManifest(Path.Join(bundles, "incompatible"), "test.incompatible", 99);
        MakeManifest(Path.Join(bundles, "missing"), "test.missing", 1);
        File.WriteAllText(Path.Join(data, "extensions.json"), JsonSerializer.Serialize(new ExtensionSettings([], ["test.disabled"])));
        await using var runtime = new ExtensionRuntime(() => null, () => false, _ => { }, data, bundles, Path.Join(fixture.Path, "logs"), administrator: false);
        await runtime.InitializeAsync();
        Assert.StartsWith("Disabled", Assert.Single(runtime.Entries, value => value.Id == "test.disabled").Status);
        Assert.Contains("API version 99", Assert.Single(runtime.Entries, value => value.Directory == Path.Join(bundles, "incompatible")).Error);
        Assert.Contains("DLL is missing", Assert.Single(runtime.Entries, value => value.Id == "test.missing").Error);
        Assert.All(runtime.Entries, value => Assert.Null(value.View));
        Assert.False(Directory.Exists(Path.Join(data, "test.disabled")));
    }

    [Fact]
    public async Task ElevatedHostBlocksExtensionsBeforeLoadingEntryDll()
    {
        using var fixture = new TestDirectory();
        string bundles = Directory.CreateDirectory(Path.Join(fixture.Path, "bundles")).FullName;
        MakeManifest(Path.Join(bundles, "photo"), "test.photo", 1);
        await using var runtime = new ExtensionRuntime(() => null, () => false, _ => { }, Path.Join(fixture.Path, "data"), bundles, Path.Join(fixture.Path, "logs"), administrator: true);
        await runtime.InitializeAsync();
        Assert.Contains("without administrator", Assert.Single(runtime.Entries).Error);
        Assert.Null(Assert.Single(runtime.Entries).View);
    }

    [Fact]
    public async Task JobAdmissionIsBoundedAndCancellationDrainsQueuedWork()
    {
        await using var jobs = new ExtensionJobs((_, _) => { });
        jobs.SetDiscoveryBusy(true);
        using var cancellation = new CancellationTokenSource();
        int started = 0;
        var tasks = Enumerable.Range(0, 128).Select(index => jobs.ForOwner("test").RunAsync("job" + index, JobResource.Thumbnail,
            _ => { Interlocked.Increment(ref started); return Task.CompletedTask; }, null, cancellation.Token)).ToArray();
        Assert.Throws<InvalidOperationException>(() => { _ = jobs.ForOwner("test").RunAsync("overflow", JobResource.Content, _ => Task.CompletedTask, null, cancellation.Token); });
        Assert.Equal(0, Volatile.Read(ref started));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(tasks));
        Assert.All(jobs.Recent, value => Assert.Equal(ExtensionJobState.Cancelled, value.State));
    }

    [Fact]
    public async Task ThumbnailLaneLimitsConcurrentWorkToTwo()
    {
        await using var jobs = new ExtensionJobs((_, _) => { });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maximum = 0;
        var tasks = Enumerable.Range(0, 10).Select(_ => jobs.ForOwner("test").RunAsync("thumbnail", JobResource.Thumbnail, async context =>
        {
            int count = Interlocked.Increment(ref active);
            int observed;
            do { observed = Volatile.Read(ref maximum); } while (count > observed && Interlocked.CompareExchange(ref maximum, count, observed) != observed);
            if (count == 2) entered.TrySetResult();
            await release.Task.WaitAsync(context.Cancellation);
            Interlocked.Decrement(ref active);
        }, null, CancellationToken.None)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref active));
        release.SetResult(); await Task.WhenAll(tasks);
        Assert.Equal(2, maximum);
    }

    [Fact]
    public async Task UnresolvedSuccessfulJobsRemainNeedsAttentionAndHistoryStaysBounded()
    {
        await using var jobs = new ExtensionJobs((_, _) => { });
        for (int index = 0; index < 161; index++)
            await jobs.ForOwner("photos").RunAsync("analysis", JobResource.Content, context =>
            {
                context.Report(new("Inspecting metadata", 3, Message: "One source remains unresolved", NeedsAttention: true));
                context.Report(new("Analysis complete", 3));
                return Task.CompletedTask;
            }, null, CancellationToken.None);
        Assert.Equal(160, jobs.Recent.Count);
        Assert.All(jobs.Recent, job => Assert.Equal(ExtensionJobState.NeedsAttention, job.State));
        Assert.All(jobs.Recent, job => Assert.Equal("Analysis complete", job.Progress?.Phase));
    }

    [Fact]
    public async Task CollectionDiscoveryPausesThumbnailsWithoutClearingForegroundPause()
    {
        await using var jobs = new ExtensionJobs((_, _) => { });
        var discoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDiscovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thumbnailEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = jobs.ForOwner("photos").RunAsync("discover", JobResource.Content, async context =>
        {
            using var lease = jobs.BeginDiscovery();
            await context.CheckpointAsync(); // Own metadata discovery must not deadlock.
            discoveryEntered.SetResult(); await releaseDiscovery.Task;
        }, null, CancellationToken.None);
        await discoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var thumbnail = jobs.ForOwner("photos").RunAsync("thumbnail", JobResource.Thumbnail, _ =>
        { thumbnailEntered.SetResult(); return Task.CompletedTask; }, null, CancellationToken.None);
        Assert.NotSame(thumbnailEntered.Task, await Task.WhenAny(thumbnailEntered.Task, Task.Delay(100)));
        jobs.SetDiscoveryBusy(true); releaseDiscovery.SetResult(); await discovery;
        Assert.NotSame(thumbnailEntered.Task, await Task.WhenAny(thumbnailEntered.Task, Task.Delay(100)));
        jobs.SetDiscoveryBusy(false);
        await thumbnail.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(thumbnailEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ScopedRefreshReadsCurrentNamesAndFinalCoverageAndInvalidatesTransientHandles()
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "old.jpg"), "first");
        var snapshot = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        File.WriteAllText(Path.Join(fixture.Path, "new.jpg"), "second");
        await using var jobs = new ExtensionJobs((_, _) => { });
        var inventory = new ExtensionInventory(() => snapshot, () => false, jobs.BeginDiscovery);
        var names = new List<string>(); InventoryReference oldReference = default;
        InventoryCoverage? final = null;
        await foreach (var batch in inventory.ReadAsync(fixture.Path, CancellationToken.None))
        {
            Assert.True(batch.Items.Count <= 512);
            foreach (var item in batch.Items)
            { names.Add(item.Name); oldReference = item.Reference; Assert.NotNull(inventory.ResolveLocation(item.Reference)); }
            final = batch.Coverage;
        }
        Assert.Contains("new.jpg", names); Assert.True(final!.Complete); Assert.False(final.HasAllocationData);
        await foreach (var batch in inventory.ReadAsync(fixture.Path, CancellationToken.None, preferExistingInventory: true))
        { Assert.Null(inventory.ResolveLocation(oldReference)); }
    }

    [Fact]
    public void ToolInputLeaseRejectsWritersAndNeverChangesSourceBytes()
    {
        using var fixture = new TestDirectory();
        string photo = Path.Join(fixture.Path, "sample.png"); byte[] source = [1, 2, 3, 4]; File.WriteAllBytes(photo, source);
        using (var lease = new ToolInputLease([photo]))
            Assert.Throws<IOException>(() => { using var writer = File.Open(photo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        Assert.Equal(source, File.ReadAllBytes(photo));
        Assert.Throws<ArgumentException>(() => ExifToolBroker.ValidateInput(photo + "\n-ver"));
    }

    [LocalExifToolFact]
    [Trait("Stage", "System")]
    public async Task RealPersistentExifToolVerifiesCopiedPngAndReusesOwnedProcess()
    {
        using var fixture = new TestDirectory();
        string photo = Path.Join(fixture.Path, "sample.png");
        byte[] source = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        File.WriteAllBytes(photo, source);
        var logs = new List<string>(); int ownedProcessId = 0;
        await using var broker = new ExifToolBroker(_ => Task.CompletedTask, (eventName, data) =>
        {
            logs.Add(eventName);
            if (eventName == "extension-tool-start") ownedProcessId = JsonSerializer.SerializeToElement(data).GetProperty("Id").GetInt32();
        }, administrator: false);
        string? version = await broker.ExifToolVersionAsync(CancellationToken.None);
        Assert.NotNull(version);
        var first = Assert.Single(await broker.ReadExifMetadataAsync([photo], CancellationToken.None));
        var second = Assert.Single(await broker.ReadExifMetadataAsync([photo], CancellationToken.None));
        Assert.Equal("PNG", first.Format); Assert.Equal(1, first.Width); Assert.Equal(1, first.Height); Assert.Null(first.Error);
        Assert.Equal(first, second); Assert.Single(logs, value => value == "extension-tool-start");
        Assert.Equal(source, File.ReadAllBytes(photo));
        using var ownedProcess = Process.GetProcessById(ownedProcessId);
        await broker.DisposeAsync(); Assert.True(ownedProcess.HasExited);
    }

    [LocalExifToolFact]
    [Trait("Stage", "System")]
    public async Task BrokerShutdownCancelsPausedRequestsBeforeWaitingForRequestLane()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new ExifToolBroker(async token =>
        { waiting.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }, (_, _) => { }, administrator: false);
        var request = broker.ExifToolVersionAsync(CancellationToken.None);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await broker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    [Trait("Stage", "System")]
    public async Task RealCloudPlaceholderIsRejectedBeforeToolLaunch()
    {
        using var fixture = new TestDirectory();
        string source = Directory.CreateDirectory(Path.Join(fixture.Path, "cloud")).FullName;
        string file = Path.Join(source, "photo.jpg"); File.WriteAllBytes(file, new byte[4096]);
        using var registered = CloudFilesTests.RegisterSyncedFixture(source, file);
        ToolInputLease? unexpected = null;
        try { Assert.Throws<IOException>(() => unexpected = new ToolInputLease([file])); }
        finally { unexpected?.Dispose(); }
        var logs = new List<string>();
        await using var broker = new ExifToolBroker(_ => Task.CompletedTask, (eventName, _) => logs.Add(eventName), administrator: false);
        if (File.Exists(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ExifTool", "ExifTool.exe")))
            await Assert.ThrowsAsync<IOException>(() => broker.ReadExifMetadataAsync([file], CancellationToken.None));
        Assert.DoesNotContain("extension-tool-start", logs);
        Assert.Equal(4096, CloudFiles.Inspect(file)!.LocalBytes);
    }

    private static void MakeManifest(string directory, string id, int version)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, "extension.json"), JsonSerializer.Serialize(new ExtensionManifest(id, id, "1.0.0", version, "missing.dll", "Missing.Extension")));
    }
    private sealed class LocalExifToolFact : FactAttribute
    {
        public LocalExifToolFact()
        {
            string path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ExifTool", "ExifTool.exe");
            if (!File.Exists(path)) Skip = "The approved local ExifTool prerequisite is unavailable.";
        }
    }
}
