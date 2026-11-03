using System.IO;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

[Trait("Stage", "System")]
public sealed class CleanupSchedulingSystemTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuietOwnedProfilePreparesOrExecutesExactlyOneOnTimeOccurrenceDespiteVerificationDelay(bool automatic)
    {
        using var fixture = new TestDirectory();
        var project = CleanupRulePlanTests.Project(fixture.Path, "project", 60);
        var operations = new FileOperations();
        var store = new CleanupRunStore(operations.Files, Path.Join(fixture.Path, "profile"));
        string data = Path.Join(fixture.Path, "extensions"), logs = Path.Join(fixture.Path, "logs");
        var schedule = new CleanupSchedule(DayOfWeek.Monday, new TimeOnly(9, 0), TimeZoneInfo.Local.Id, automatic);
        var due = CleanupScheduleClock.OnDate(schedule, new DateOnly(2026, 11, 2));
        var clock = new FixtureClock(due.ScheduledForUtc.AddHours(-1));
        var native = new FixtureScheduler();
        Func<CleanupTaskLaunch> launcher = () => new(@"C:\Fixture\SpaceMaid.App.exe", "", @"C:\Fixture");
        var definition = new CleanupDefinition { Name = "Owned weekly build fixture", DiscoveryRootPath = project.Root,
            ScopePaths = [project.Root], Schedule = schedule, BuildAction = automatic ? BuildCleanupAction.PermanentlyDelete : BuildCleanupAction.Recycle };
        var scan = await operations.ScanAsync(project.Root);
        await using (var runtime = new ExtensionRuntime(() => scan, () => false, _ => { }, data,
            Path.Join(AppContext.BaseDirectory, "Extensions"), logs, administrator: false, operations: operations))
        {
            await runtime.InitializeDiscoveryAsync();
            var candidates = runtime.Opportunities.Find(scan);
            var prepared = await new CleanupRunCoordinator(operations, store, runtime.Opportunities.Find)
                .PrepareAsync(definition, scan, candidates, CancellationToken.None);
            Assert.Single(prepared.Plan.Targets);
            definition = await new CleanupScheduleService(store, native, launcher, clock)
                .SaveAsync(prepared.Definition with { Enabled = true });
        }
        clock.Now = due.ScheduledForUtc.AddSeconds(10);
        DateTimeOffset arrived = clock.Now;
        // An expensive native verification or queue wait must not retroactively
        // turn the admitted on-time invocation into a missed occurrence.
        native.Verification = () => clock.Now = clock.Now.AddMinutes(20);
        int result = await CleanupScheduledEntrypoint.RunOwnedProfileAsync(definition.Id, definition.Revision,
            operations, store, native, launcher, clock, data, logs, CancellationToken.None);
        Assert.Equal(0, result);
        var record = store.LoadRun(Assert.Single(store.LoadRunHeaders()).Id)!;
        Assert.Equal(due.Id, record.OccurrenceId); Assert.Equal(arrived, record.CreatedUtc);
        Assert.Equal(due.ScheduledForUtc, record.ScheduledForUtc);
        Assert.Equal(Environment.ProcessId, record.OwnerProcessId);
        Assert.Equal(CleanupRunOwnership.CurrentStartedUtc, record.OwnerStartedUtc);
        Assert.Single(record.Targets, item => item.Decision == "Ready");
        if (automatic)
        {
            Assert.Equal(CleanupRunState.Completed, record.State);
            Assert.Equal(CleanupOutcome.PermanentlyDeleted, Assert.Single(record.Receipts).Outcome);
            Assert.False(Directory.Exists(project.Output));
            Directory.CreateDirectory(project.Output); File.WriteAllText(project.File, "replacement output");
            BuildActivityFixture.AgeProject(project.Root, 60);
        }
        else
        {
            Assert.Equal(CleanupRunState.Review, record.State); Assert.Empty(record.Receipts);
            Assert.Equal("generated output", File.ReadAllText(project.File));
        }
        Assert.Equal(0, await CleanupScheduledEntrypoint.RunOwnedProfileAsync(definition.Id, definition.Revision,
            operations, store, native, launcher, clock, data, logs, CancellationToken.None));
        Assert.Single(store.LoadRunHeaders());
        if (automatic) Assert.Equal("replacement output", File.ReadAllText(project.File));
    }

    [Fact]
    public void OwnedGlobalGateCanBeCreatedWithoutAServiceOrSpecialPrivilege()
    {
        using var mutex = new Mutex(false, @"Global\SpaceMaid.SchedulingFixture." + Guid.NewGuid().ToString("N"));
        Assert.True(mutex.WaitOne(0));
        mutex.ReleaseMutex();
    }

    [Fact]
    public void OwnedFutureTaskCanBeRegisteredVerifiedAndRemovedWithoutRunningIt()
    {
        using var fixture = new TestDirectory();
        var native = new WindowsCleanupTaskScheduler(Path.Join(fixture.Path, "owned-profile"));
        var definition = new CleanupDefinition
        {
            Name = "Space Maid owned scheduling fixture", DiscoveryRootPath = fixture.Path, ScopePaths = [fixture.Path],
            RootIdentity = new(fixture.Path, new FileIdentity(1, 2), 1),
            Schedule = new(DateTime.Today.AddDays(2).DayOfWeek, new TimeOnly(9, 0), TimeZoneInfo.Local.Id, false)
        };
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test executable is unavailable.");
        var launch = new CleanupTaskLaunch(executable, "", Path.GetDirectoryName(executable)!);
        try
        {
            native.Register(definition, launch);
            Assert.True(native.Matches(definition, launch));
            Assert.False(native.Matches(definition with { Revision = definition.Revision + 1 }, launch));
        }
        finally { native.Remove(definition.Id); }
        Assert.False(native.Matches(definition, launch));
    }

    private sealed class FixtureScheduler : ICleanupTaskScheduler
    {
        internal Action? Verification;
        public void Register(CleanupDefinition definition, CleanupTaskLaunch launch) { }
        public bool Matches(CleanupDefinition definition, CleanupTaskLaunch launch) { Verification?.Invoke(); return true; }
        public void Remove(Guid definitionId) { }
    }
    private sealed class FixtureClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
