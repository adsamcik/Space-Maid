using System.IO;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OpportunityExtensionTests
{
    [Fact]
    public async Task BundledDeveloperLoadsWithoutAViewAndCanBeDisabledAfterRestart()
    {
        using var fixture = new TestDirectory();
        string environment = Directory.CreateDirectory(Path.Join(fixture.Path, ".venv")).FullName;
        File.WriteAllText(Path.Join(environment, "pyvenv.cfg"), "home = fixture");
        File.WriteAllBytes(Path.Join(environment, "data.bin"), new byte[4096]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        string bundle = Path.Join(AppContext.BaseDirectory, "Extensions", "space-maid.developer");
        string bundles = Path.GetDirectoryName(bundle)!;
        Assert.True(File.Exists(Path.Join(bundle, "extension.json")), "The Developer extension must be included in the app bundle.");
        string data = Path.Join(fixture.Path, "data"), logs = Path.Join(fixture.Path, "logs");
        await using (var runtime = new ExtensionRuntime(() => scan, () => false, _ => { }, data, bundles, logs, administrator: false))
        {
            await runtime.InitializeAsync();
            var entry = Assert.Single(runtime.Entries, value => value.Id == "space-maid.developer");
            Assert.Equal("space-maid.developer", entry.Id); Assert.Equal("Loaded", entry.Status); Assert.Null(entry.View);
            var analysis = runtime.Opportunities.Analyze(scan, CancellationToken.None);
            var finding = Assert.Single(analysis.Findings);
            Assert.Equal(environment, finding.Path); Assert.Equal(entry.Id, finding.ProviderId); Assert.NotEmpty(finding.RuleId);
            Assert.Equal("cleanup:environments", Assert.Single(analysis.Groups.Cleanup!).Key);
            var scoped = await new VolumeScanner().ScanAsync(environment, preferFast: false);
            var scopedAnalysis = runtime.Opportunities.Analyze(scoped, CancellationToken.None);
            Assert.Equal(scoped.RootIndex, Assert.Single(scopedAnalysis.Findings).DirectoryIndex);
            Assert.Equal(environment, Assert.Single(scopedAnalysis.Findings).Path);
            Assert.Equal("cleanup:environments", Assert.Single(scopedAnalysis.Groups.Cleanup!).Key);
            runtime.SetEnabled(entry, false);
        }
        await using var restarted = new ExtensionRuntime(() => scan, () => false, _ => { }, data, bundles, logs, administrator: false);
        await restarted.InitializeAsync();
        Assert.StartsWith("Disabled", Assert.Single(restarted.Entries, value => value.Id == "space-maid.developer").Status);
        Assert.Empty(restarted.Opportunities.Find(scan, CancellationToken.None));
    }

    [Fact]
    public async Task ProvidersShareHostAccountingAndKeepTheirGroupsDistinct()
    {
        var (scan, first, _) = Inventory();
        var finding = Finding(scan, first) with { LogicalBytes = long.MaxValue, AllocatedBytes = long.MaxValue, ProviderId = "forged.owner" };
        var source = Analysis(finding, "cleanup:cache");
        await using var catalog = new OpportunityCatalog();
        catalog.Register("test.media", new FixedProvider(source));
        catalog.Register("test.models", new FixedProvider(source));
        var analysis = catalog.Analyze(scan, CancellationToken.None);
        Assert.Equal(2, analysis.Findings.Count);
        Assert.All(analysis.Findings, value =>
        {
            Assert.Equal(scan.DirectoryLogical[first], value.LogicalBytes);
            Assert.Equal(scan.DirectoryAllocated[first], value.AllocatedBytes);
            Assert.Equal(scan.PathForFile(first), value.Path);
        });
        Assert.Equal(new[] { "test.media", "test.models" }, analysis.Findings.Select(value => value.ProviderId).Order().ToArray());
        Assert.Equal(new[] { "cleanup:test.media/cache", "cleanup:test.models/cache" }, analysis.Groups.Cleanup!.Select(group => group.Key).Order().ToArray());
        Assert.All(analysis.Groups.Cleanup!, group =>
        {
            Assert.Equal(scan.DirectoryAllocated[first], group.ObservedBytes);
            Assert.All(group.Locations, value => Assert.Equal(group.ProviderId, value.ProviderId));
        });
        var rebuilt = catalog.BuildCleanupGroups(scan, analysis.Findings, analysis.Groups,
            new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), WorktreeCleanupPolicy.Default), CancellationToken.None);
        Assert.Equal(analysis.Groups.Cleanup!.Select(group => group.Key), rebuilt.Select(group => group.Key));
        Assert.All(rebuilt, group => Assert.Equal(scan.DirectoryAllocated[first], group.ObservedBytes));
        Assert.ThrowsAny<OperationCanceledException>(() => catalog.Analyze(scan, new(true)));
    }

    [Fact]
    public async Task InvalidMembershipAndFailedProvidersDoNotHideOtherProviders()
    {
        var (scan, first, second) = Inventory();
        var allowed = Finding(scan, first);
        var forbidden = Finding(scan, second);
        var invalid = new OpportunityAnalysis([allowed], new([], [], Cleanup:
            [new("cleanup:cache", "Invalid", forbidden.Path, "", [forbidden], 999999)]));
        var events = new List<string>();
        await using var catalog = new OpportunityCatalog((name, _) => events.Add(name));
        catalog.Register("test.invalid", new FixedProvider(invalid));
        catalog.Register("test.failed", new FixedProvider(new InvalidDataException("Cannot classify fixture")));
        catalog.Register("test.valid", new FixedProvider(Analysis(allowed, "cleanup:cache")));
        var analysis = catalog.Analyze(scan, CancellationToken.None);
        Assert.Equal("test.valid", Assert.Single(analysis.Findings).ProviderId);
        Assert.Equal("cleanup:test.valid/cache", Assert.Single(analysis.Groups.Cleanup!).Key);
        Assert.Equal(2, events.Count(name => name == "opportunity-extension-failed"));
        Assert.Contains("unavailable", catalog.Status(scan));
    }

    [Fact]
    public async Task DuplicateChildKeysAcrossCleanupBranchesAreRejectedAndGroupingFailuresRemainVisible()
    {
        var (scan, first, _) = Inventory();
        var finding = Finding(scan, first);
        OpportunityGroup Group(string key, IReadOnlyList<OpportunityGroup>? children = null) =>
            new(key, key, finding.Path, "Review", [finding], finding.AllocatedBytes, Children: children);
        var invalid = new OpportunityAnalysis([finding], new([], [], Cleanup:
            [Group("cleanup:first", [Group("shared")]), Group("cleanup:second", [Group("shared")])]));
        var events = new List<string>();
        await using var catalog = new OpportunityCatalog((name, _) => events.Add(name));
        catalog.Register("test.duplicate", new FixedProvider(invalid));
        catalog.Register("test.valid", new FixedProvider(Analysis(finding, "cleanup:cache")));
        catalog.Register("test.grouping", new FixedProvider(Analysis(finding, "cleanup:cache"), failGrouping: true));
        var analysis = catalog.Analyze(scan, CancellationToken.None);
        Assert.DoesNotContain(analysis.Findings, value => value.ProviderId == "test.duplicate");
        Assert.Single(events, name => name == "opportunity-extension-failed");
        var groups = catalog.BuildCleanupGroups(scan, analysis.Findings, analysis.Groups,
            new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), WorktreeCleanupPolicy.Default), CancellationToken.None);
        Assert.Equal("test.valid", Assert.Single(groups).ProviderId);
        Assert.Single(events, name => name == "opportunity-extension-grouping-failed");
        Assert.Contains("unavailable", catalog.Status(scan));
    }

    [Fact]
    public async Task CleanupRequiresAnAvailableOwningRuleAndCannotSubstituteAnotherProvider()
    {
        using var fixture = new TestDirectory();
        string project = Directory.CreateDirectory(Path.Join(fixture.Path, "project")).FullName;
        File.WriteAllText(Path.Join(project, "app.csproj"), "<Project />");
        string output = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
        string file = Path.Join(output, "result.dll"); File.WriteAllBytes(file, new byte[4096]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var selected = Assert.Single(DeveloperOpportunityFixture.Find(scan));
        Assert.Throws<ArgumentNullException>(() => CleanupPlanner.Prepare(scan, [selected]));
        await Assert.ThrowsAsync<ArgumentNullException>(() => OpportunityReviewValidator.ValidateAsync(scan, [selected]));
        var wrongOwner = selected with { ProviderId = "test.other" };
        var wrongRule = selected with { RuleId = "other-rule" };
        var blocked = CleanupPlanner.Prepare(scan, [wrongOwner, wrongRule], discover: DeveloperOpportunityFixture.Find);
        Assert.False(blocked.CanExecute); Assert.Equal(2, blocked.BlockedLocations.Count);
        var validated = await OpportunityReviewValidator.ValidateAsync(scan, [wrongOwner, wrongRule], discover: DeveloperOpportunityFixture.Find);
        Assert.Empty(validated.ValidatedLocations);
        var plan = CleanupPlanner.Prepare(scan, [selected], discover: DeveloperOpportunityFixture.Find);
        Assert.True(plan.CanExecute);
        await Assert.ThrowsAsync<ArgumentNullException>(() => CleanupBatch.ExecuteAsync(plan, 0, new SilentProgress(), default, (_, _) => { }));
        var unavailable = await CleanupBatch.ExecuteAsync(plan, 0, new SilentProgress(), default, (_, _) => { }, discover: (_, _) => []);
        Assert.Equal(0, unavailable.RecycledCount);
        Assert.All(unavailable.Locations, value => Assert.False(value.Attempted));
        Assert.True(Directory.Exists(output)); Assert.Equal(4096, new FileInfo(file).Length);
    }

    private static (ScanSnapshot Scan, int First, int Second) Inventory()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int first = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(first, new(1), "first");
        int second = builder.AddFile(new(new(3), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(second, new(1), "second");
        int file = builder.AddFile(new(new(4), FileAttributes.Normal, 5000, 8192, 1)); builder.AddLink(file, new(2), "preview.bin");
        return (builder.Build(@"G:\", "fixture", TimeSpan.Zero, 0, default), first, second);
    }

    private static Opportunity Finding(ScanSnapshot scan, int directory) => new(directory, "Preview cache", "Media cache", scan.PathForFile(directory)!,
        scan.DirectoryLogical[directory], scan.DirectoryAllocated[directory], "Generated preview files", "Review contents first", "Recycle", true)
    { RuleId = "previews" };

    private static OpportunityAnalysis Analysis(Opportunity finding, string groupKey) => new([finding], new([], [], Cleanup:
        [new(groupKey, "Previews", finding.Path, "Review", [finding], long.MaxValue) { Caption = "Generated previews", StatusLabel = "Review" }]));

    private sealed class FixedProvider : IOpportunityExtension
    {
        private readonly OpportunityAnalysis? _analysis;
        private readonly Exception? _failure;
        private readonly bool _failGrouping;
        internal FixedProvider(OpportunityAnalysis analysis, bool failGrouping = false)
        { _analysis = analysis; _failGrouping = failGrouping; }
        internal FixedProvider(Exception failure) => _failure = failure;
        public OpportunityAnalysis Analyze(IOpportunityInventory inventory, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (_failure is not null) throw _failure; return _analysis!; }
        public IReadOnlyList<OpportunityGroup> BuildCleanupGroups(IOpportunityInventory inventory, OpportunityAnalysis analysis,
            OpportunityGroupContext context, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (_failGrouping) throw new InvalidDataException("Cannot group fixture"); return analysis.Groups.Cleanup ?? []; }
    }
    private sealed class SilentProgress : IProgress<CleanupProgress>
    { public void Report(CleanupProgress value) { } }
}
