using System.IO;
using System.Xml.Linq;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CleanupSchedulingTests
{
    [Fact]
    public void CacheOnlyWeeklyReviewDoesNotRequireDeveloperProviderAndReportsAvailableDiscovery()
    {
        var definition = Definition(@"G:\fixture") with { Build = false, Cache = true };
        LoadedExtension[] entries = [new("space-maid.developer", "Developer", @"G:\fixture\developer", "Disabled"),
            new("space-maid.photos", "Photos", @"G:\fixture\photos", "Loaded", SupportsOpportunities: true)];
        Assert.Null(CleanupScheduledEntrypoint.DiscoveryNote(definition, entries, ""));
        Assert.Contains("not included", CleanupScheduledEntrypoint.DiscoveryNote(definition with { Build = true }, entries, ""));
        Assert.Contains("unavailable", CleanupScheduledEntrypoint.DiscoveryNote(definition, entries, "1 cleanup extension unavailable"));
    }

    [Fact]
    public async Task SavingAndDeletingRulesCannotSupersedeAnAlreadyClaimedRun()
    {
        using var fixture = new TestDirectory(); var operations = new FileOperations();
        var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var service = Service(store, new FakeScheduler()); var definition = await service.SaveAsync(Definition(fixture.Path));
        var pending = new CleanupRunRecord { Definition = definition, ScanRootPath = fixture.Path, State = CleanupRunState.Review };
        store.SaveRun(pending);
        var claimed = pending with { State = CleanupRunState.Queued, ApprovalClaimId = Guid.NewGuid() };
        Assert.True(store.TryQueueRun(claimed));
        var running = claimed with { State = CleanupRunState.Running }; store.SaveRun(running);
        await service.SaveAsync(definition with { Name = "Updated cleanup" });
        Assert.Equal(CleanupRunState.Running, store.LoadRun(pending.Id)!.State);
        await service.DeleteAsync(definition.Id);
        var retained = store.LoadRun(pending.Id)!; Assert.Equal(CleanupRunState.Running, retained.State);
        Assert.Equal(claimed.ApprovalClaimId, retained.ApprovalClaimId);
    }

    [Fact]
    public async Task BackgroundDiscoveryLoadsClassifiersWithoutCreatingAnyExtensionViews()
    {
        using var fixture = new TestDirectory(); var project = CleanupRulePlanTests.Project(fixture.Path, "project", 60);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        await using var runtime = new ExtensionRuntime(() => scan, () => false, _ => { }, Path.Join(fixture.Path, "data"),
            Path.Join(AppContext.BaseDirectory, "Extensions"), Path.Join(fixture.Path, "logs"), administrator: false);
        await runtime.InitializeDiscoveryAsync();
        Assert.All(runtime.Entries, entry => Assert.Null(entry.View));
        Assert.Equal("Not needed for background discovery", Assert.Single(runtime.Entries, entry => entry.Id == "space-maid.opportunities").Status);
        Assert.Equal("Loaded", Assert.Single(runtime.Entries, entry => entry.Id == "space-maid.developer").Status);
        Assert.Contains(runtime.Opportunities.Find(scan), finding => finding.Path == project.Output && finding.IsBuildOutput);
    }

    [Fact]
    public void MissingLocalHourAdvancesToFirstValidInstantAndRepeatedHourUsesOneIdentity()
    {
        var schedule = new CleanupSchedule(DayOfWeek.Sunday, new TimeOnly(2, 30, 45), "Central Europe Standard Time", false);
        var missing = CleanupScheduleClock.OnDate(schedule, new DateOnly(2026, 3, 29));
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), missing.ScheduledForUtc);
        Assert.Equal("2026-03-29", missing.Id);
        var repeated = CleanupScheduleClock.OnDate(schedule, new DateOnly(2026, 10, 25));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 45, TimeSpan.Zero), repeated.ScheduledForUtc);
        Assert.Equal(repeated.Id, CleanupScheduleClock.Latest(schedule, repeated.ScheduledForUtc.AddHours(1)).Id);
        Assert.Equal(repeated.ScheduledForUtc, CleanupScheduleClock.Latest(schedule, repeated.ScheduledForUtc.AddHours(1)).ScheduledForUtc);
    }

    [Fact]
    public async Task SavingDoesNotEnableUntilTheMatchingTaskHasBeenVerified()
    {
        using var fixture = new TestDirectory(); var operations = new FileOperations();
        var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var definition = Definition(fixture.Path); var native = new FakeScheduler();
        native.OnRegister = _ => Assert.False(Assert.Single(store.LoadDefinitions()).Enabled);
        native.OnMatch = _ => { Assert.Null(Assert.Single(store.LoadDefinitions()).RegisteredRevision); return true; };
        var service = Service(store, native);
        var saved = await service.SaveAsync(definition);
        Assert.True(saved.Enabled); Assert.Equal(saved.Revision, saved.RegisteredRevision);
        var restored = Assert.Single(store.LoadDefinitions()); Assert.Equal(saved.Id, restored.Id);
        Assert.Equal(saved.Revision, restored.RegisteredRevision); Assert.True(restored.Enabled); Assert.Empty(store.LoadRuns());
    }

    [Fact]
    public async Task RegistrationFailureKeepsTheCleanupSavedAndInactive()
    {
        using var fixture = new TestDirectory(); var operations = new FileOperations();
        var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var native = new FakeScheduler { OnRegister = _ => throw new InvalidOperationException("Fixture registration denied") };
        var saved = await Service(store, native).SaveAsync(Definition(fixture.Path));
        Assert.False(saved.Enabled); Assert.Null(saved.RegisteredRevision); Assert.Contains("scheduling is off", saved.ScheduleError);
        var restored = Assert.Single(store.LoadDefinitions()); Assert.Equal(saved.Id, restored.Id);
        Assert.False(restored.Enabled); Assert.Equal(saved.ScheduleError, restored.ScheduleError); Assert.Empty(store.LoadRuns());
    }

    [Fact]
    public async Task ANewerSavedRevisionCannotBeRevivedByAnOlderRegistration()
    {
        using var fixture = new TestDirectory(); var operations = new FileOperations();
        var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var native = new FakeScheduler { OnRegister = definition => store.SaveDefinition(definition with { Revision = definition.Revision + 1, Enabled = false }) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(store, native).SaveAsync(Definition(fixture.Path)));
        var current = Assert.Single(store.LoadDefinitions()); Assert.Equal(2, current.Revision); Assert.False(current.Enabled);
    }

    [Fact]
    public async Task ReconciliationRecordsMissedOccurrencesOnceWithoutPreparingOrRunningCleanup()
    {
        using var fixture = new TestDirectory(); var operations = new FileOperations();
        var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var definition = Definition(fixture.Path);
        var due = CleanupScheduleClock.OnDate(definition.Schedule!, new DateOnly(2026, 11, 2));
        var clock = new FixedClock(due.ScheduledForUtc.AddHours(-1)); var native = new FakeScheduler();
        var service = Service(store, native, clock); await service.SaveAsync(definition);
        clock.Now = due.ScheduledForUtc.AddDays(2); await service.ReconcileAsync(); await service.ReconcileAsync();
        var missed = Assert.Single(store.LoadRuns()); Assert.Equal(CleanupRunState.Missed, missed.State);
        Assert.Equal(due.Id, missed.OccurrenceId); Assert.Empty(missed.Targets); Assert.Empty(missed.Receipts);
        Assert.True(Assert.Single(store.LoadDefinitions()).Enabled);
    }

    [Fact]
    public async Task MissingOrChangedNativeTaskPausesInsteadOfSilentlyRepairingAndEnablingIt()
    {
        using var fixture = new TestDirectory(); var operations = new FileOperations();
        var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var native = new FakeScheduler(); var service = Service(store, native); await service.SaveAsync(Definition(fixture.Path));
        native.OnMatch = _ => false; await service.ReconcileAsync();
        var saved = Assert.Single(store.LoadDefinitions()); Assert.False(saved.Enabled); Assert.Null(saved.RegisteredRevision);
        Assert.Contains("no longer matches", saved.ScheduleError); Assert.Equal(1, native.Registrations);
    }

    [Fact]
    public async Task PausingAndResumingFutureOccurrencesPreservesThePendingReviewAndItsRevision()
    {
        using var fixture = new TestDirectory(); var operations = new FileOperations();
        var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "state"));
        var service = Service(store, new FakeScheduler()); var saved = await service.SaveAsync(Definition(fixture.Path));
        var pending = new CleanupRunRecord { Definition = saved, ScanRootPath = saved.DiscoveryRootPath, State = CleanupRunState.Review,
            Source = "Scheduled", OccurrenceId = "2026-11-02" };
        store.SaveRun(pending);
        var paused = await service.PauseAsync(saved.Id, true);
        Assert.False(paused.Enabled); Assert.Equal(saved.Revision, paused.Revision);
        Assert.Equal(CleanupRunState.Review, store.LoadRun(pending.Id)!.State);
        var resumed = await service.PauseAsync(saved.Id, false);
        Assert.True(resumed.Enabled); Assert.Equal(saved.Revision, resumed.Revision);
        Assert.Equal(CleanupRunState.Review, store.LoadRun(pending.Id)!.State);
    }

    [Fact]
    public void NativeTaskIsLeastPrivilegeSilentAndBoundToOneDefinitionRevision()
    {
        var definition = Definition(@"G:\fixture") with { Revision = 3 };
        var launch = new CleanupTaskLaunch(@"C:\Space Maid\SpaceMaid.App.exe", "", @"C:\Space Maid");
        const string sid = "S-1-5-21-123-456-789-1001";
        string xml = WindowsCleanupTaskScheduler.CreateXml(definition, launch, sid, DateTimeOffset.UtcNow);
        Assert.True(WindowsCleanupTaskScheduler.XmlMatches(xml, definition, launch, sid));
        Assert.False(WindowsCleanupTaskScheduler.XmlMatches(xml, definition with { Revision = 4 }, launch, sid));
        var document = XElement.Parse(xml); XNamespace ns = document.Name.Namespace;
        Assert.Equal("false", document.Element(ns + "Settings")!.Element(ns + "WakeToRun")!.Value);
        Assert.Equal("false", document.Element(ns + "Settings")!.Element(ns + "StartWhenAvailable")!.Value);
        document.Element(ns + "Settings")!.Element(ns + "WakeToRun")!.Value = "true";
        Assert.False(WindowsCleanupTaskScheduler.XmlMatches(document.ToString(), definition, launch, sid));
    }

    [Fact]
    public void NativeTaskVerificationAcceptsOnlyTheSafeDefaultsWindowsOmitsFromRegisteredXml()
    {
        var definition = Definition(@"G:\fixture");
        var launch = new CleanupTaskLaunch(@"C:\Fixture\SpaceMaid.App.exe", "", @"C:\Fixture");
        const string sid = "S-1-5-21-123-456-789-1001";
        var document = XElement.Parse(WindowsCleanupTaskScheduler.CreateXml(definition, launch, sid, DateTimeOffset.UtcNow));
        XNamespace ns = document.Name.Namespace;
        var settings = document.Element(ns + "Settings")!;
        document.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "RunLevel")!.Remove();
        document.Element(ns + "Triggers")!.Element(ns + "CalendarTrigger")!.Element(ns + "Enabled")!.Remove();
        foreach (string name in new[] { "StartWhenAvailable", "WakeToRun", "Enabled" }) settings.Element(ns + name)!.Remove();
        Assert.True(WindowsCleanupTaskScheduler.XmlMatches(document.ToString(), definition, launch, sid));
        settings.Add(new XElement(ns + "WakeToRun", "true"));
        Assert.False(WindowsCleanupTaskScheduler.XmlMatches(document.ToString(), definition, launch, sid));
        settings.Element(ns + "WakeToRun")!.Remove(); settings.Element(ns + "AllowHardTerminate")!.Remove();
        Assert.False(WindowsCleanupTaskScheduler.XmlMatches(document.ToString(), definition, launch, sid));
    }

    [Theory]
    [InlineData("--cleanup-scheduled", "e839e985-5e39-4af5-8a85-2ff236975a67", "1", true)]
    [InlineData("--cleanup-scheduled", "G:\\some-folder", "1", false)]
    [InlineData("--cleanup-scheduled", "e839e985-5e39-4af5-8a85-2ff236975a67", "0", false)]
    [InlineData("--cleanup-scheduled", "e839e985-5e39-4af5-8a85-2ff236975a67", "1 & delete", false)]
    public void BackgroundArgumentsAcceptOnlyTypedIdentifiers(string command, string id, string revision, bool valid)
        => Assert.Equal(valid, CleanupScheduledEntrypoint.TryParse([command, id, revision], out _, out _));

    private static CleanupDefinition Definition(string root) => new()
    {
        Name = "Fixture build cleanup", DiscoveryRootPath = root, ScopePaths = [root], Enabled = true,
        RootIdentity = new(root, new FileIdentity(1, 2), 1),
        Schedule = new(DayOfWeek.Monday, new TimeOnly(9, 0), TimeZoneInfo.Local.Id, false)
    };
    private static CleanupScheduleService Service(CleanupRunStore store, FakeScheduler native, TimeProvider? clock = null)
        => new(store, native, () => new(@"C:\Fixture\SpaceMaid.App.exe", "", @"C:\Fixture"), clock ?? TimeProvider.System);
    private sealed class FakeScheduler : ICleanupTaskScheduler
    {
        internal Action<CleanupDefinition>? OnRegister;
        internal Func<CleanupDefinition, bool>? OnMatch;
        internal int Registrations;
        public void Register(CleanupDefinition definition, CleanupTaskLaunch launch) { Registrations++; OnRegister?.Invoke(definition); }
        public bool Matches(CleanupDefinition definition, CleanupTaskLaunch launch) => OnMatch?.Invoke(definition) ?? true;
        public void Remove(Guid definitionId) { }
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
