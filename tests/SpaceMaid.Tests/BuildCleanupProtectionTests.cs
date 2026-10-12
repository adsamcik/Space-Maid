using System.IO;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class BuildCleanupProtectionTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ARepositoryInsideRecognizedOutputRequiresExplicitReview()
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "app.csproj"), "<Project />");
        string output = Directory.CreateDirectory(Path.Join(fixture.Path, "bin")).FullName;
        string repository = Directory.CreateDirectory(Path.Join(output, "nested", ".git")).FullName;
        File.WriteAllText(Path.Join(repository, "config"), "owned fixture");
        BuildActivityFixture.AgeProject(fixture.Path);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var finding = Assert.Single(DeveloperOpportunityFixture.Find(scan), item => item.Path == output);
        var assessment = BuildCleanupProtection.Assess(scan, [finding], new())[finding.DirectoryIndex];
        Assert.Equal(CleanupReadiness.NeedsReview, assessment.State);
        Assert.True(Directory.Exists(repository));
    }

    [Fact]
    public void RetentionProtectsRecentDescendantsAndProjectSourceAndUsesTheConfiguredWindow()
    {
        var (oldScan, oldOutput) = Inventory(outputWrite: Now.AddDays(-4));
        Assert.Equal(CleanupReadiness.Ready, Assessment(oldScan, oldOutput).State);
        Assert.Equal(CleanupReadiness.Protected, Assessment(oldScan, oldOutput, days: 7).State);
        var (recentScan, recentOutput) = Inventory(outputWrite: Now.AddDays(-1));
        var recent = Assessment(recentScan, recentOutput);
        Assert.Equal(CleanupReadiness.Protected, recent.State);
        Assert.Equal(Now.AddDays(-1), recent.LastActivityUtc);
        var (sourceScan, sourceOutput) = Inventory(sourceWrite: Now.AddHours(-1));
        var source = Assessment(sourceScan, sourceOutput);
        Assert.Equal(CleanupReadiness.Protected, source.State);
        Assert.Equal(Now.AddHours(-1), source.LastActivityUtc);
    }

    [Fact]
    public void NewCopiesUnknownDatesFutureDatesAndIncompleteOrLinkedOutputsAreKept()
    {
        foreach (var (write, created, attributes, incomplete) in new[]
        {
            (Now.AddDays(-14), Now, FileAttributes.Normal, false),
            (DateTime.MinValue, Now.AddDays(-14), FileAttributes.Normal, false),
            (Now.AddDays(1), Now.AddDays(-14), FileAttributes.Normal, false),
            (Now.AddDays(-14), Now.AddDays(-14), FileAttributes.ReparsePoint | FileAttributes.Directory, false),
            (Now.AddDays(-14), Now.AddDays(-14), FileAttributes.Normal, true)
        })
        {
            var (scan, output) = Inventory(outputWrite: write, outputCreated: created,
                descendantAttributes: attributes, incomplete: incomplete);
            var assessment = Assessment(scan, output);
            Assert.Equal(CleanupReadiness.Protected, assessment.State);
            Assert.False(string.IsNullOrWhiteSpace(assessment.Reason));
        }
    }

    [Fact]
    public void AgeDoesNotMakeGenericBuildFoldersOrManualFindingsReady()
    {
        var (scan, output) = Inventory();
        var generic = Finding(scan, output) with { IsRegeneratable = false, RequiresManualReview = true };
        var manual = generic with { IsBuildOutput = false };
        var assessments = BuildCleanupProtection.Assess(scan, [generic], new(), nowUtc: Now);
        Assert.Equal(CleanupReadiness.NeedsReview, assessments[output].State);
        Assert.Equal(CleanupReadiness.NeedsReview,
            BuildCleanupProtection.Assess(scan, [manual], new(), nowUtc: Now)[output].State);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BuildCleanupPolicy(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BuildCleanupPolicy(366));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            BuildCleanupProtection.Assess(scan, [generic], new(), new(true), Now));
    }

    [Fact]
    public void AReadyCacheContainingAnOldGenericBuildStillNeedsReview()
    {
        var (scan, output) = Inventory();
        int nested = scan.Links[Assert.Single(scan.Children(output))].FileIndex;
        var cache = Finding(scan, output) with
        {
            Title = "Tool cache", Category = "Tool caches", RuleId = "tool-cache",
            IsBuildOutput = false, IsRegeneratable = false, RequiresManualReview = false
        };
        var genericBuild = Finding(scan, nested) with
        {
            Title = "Possible build output", RuleId = "possible-build",
            IsRegeneratable = false, RequiresManualReview = true
        };
        var assessments = BuildCleanupProtection.Assess(scan, [cache, genericBuild], new(), nowUtc: Now);
        Assert.Equal(CleanupReadiness.NeedsReview, assessments[nested].State);
        Assert.Equal(CleanupReadiness.NeedsReview, assessments[output].State);
    }

    [Fact]
    public async Task ASelectedAncestorCannotBypassARecentlyUsedNestedBuild()
    {
        using var fixture = new TestDirectory();
        string environment = Directory.CreateDirectory(Path.Join(fixture.Path, ".venv")).FullName;
        File.WriteAllText(Path.Join(environment, "pyvenv.cfg"), "home = fixture");
        string cache = Directory.CreateDirectory(Path.Join(environment, "__pycache__")).FullName;
        string bytecode = Path.Join(cache, "module.pyc"); File.WriteAllBytes(bytecode, new byte[1024]);
        BuildActivityFixture.AgeProject(fixture.Path);
        File.SetLastWriteTimeUtc(bytecode, DateTime.UtcNow);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var findings = DeveloperOpportunityFixture.Find(scan);
        var ancestor = Assert.Single(findings, item => item.Path == environment);
        var plan = CleanupPlanner.Prepare(scan, [ancestor], discover: DeveloperOpportunityFixture.Find);
        Assert.False(plan.CanExecute);
        Assert.Empty(plan.Targets);
        Assert.Equal(environment, Assert.Single(plan.BlockedLocations).Opportunity.Path);
        Assert.Equal(1024, new FileInfo(bytecode).Length);
    }

    [Fact]
    public async Task RestrictingConsentRetainsCapturedTargetsAndNeverAddsUnreviewedFolders()
    {
        using var fixture = new TestDirectory();
        foreach (string name in new[] { "first", "second", "third" })
        {
            string project = Directory.CreateDirectory(Path.Join(fixture.Path, name)).FullName;
            File.WriteAllText(Path.Join(project, "app.csproj"), "<Project />");
            string output = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
            File.WriteAllBytes(Path.Join(output, "result.dll"), new byte[1024]);
        }
        BuildActivityFixture.AgeProject(fixture.Path);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var findings = DeveloperOpportunityFixture.Find(scan);
        var chosen = findings.Take(2).ToArray();
        var policy = new BuildCleanupPolicy(7);
        var plan = CleanupPlanner.Prepare(scan, chosen, discover: DeveloperOpportunityFixture.Find, buildPolicy: policy);
        var kept = chosen[0];
        Assert.Throws<ArgumentException>(() => CleanupPlanner.Restrict(plan, [kept, findings[2]]));
        var narrowed = CleanupPlanner.Restrict(plan, [kept]);
        Assert.Same(plan.Snapshot, narrowed.Snapshot);
        Assert.Equal(policy, narrowed.BuildPolicy);
        Assert.Equal(kept.Path, Assert.Single(narrowed.Preview.Locations).Path);
        Assert.Same(plan.Targets.Single(target => target.Path == kept.Path), Assert.Single(narrowed.Targets));
        Assert.Equal(kept.DisplayBytes(scan.HasAllocationData), narrowed.Preview.EstimatedBytes);
        Assert.Empty(CleanupPlanner.Restrict(plan, []).Targets);
        Assert.All(findings, item => Assert.True(Directory.Exists(item.Path)));
    }

    [Fact]
    public async Task AuthoredFilesRequireReviewEvenInOldRecognizedBuildFolders()
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "app.csproj"), "<Project />");
        string output = Directory.CreateDirectory(Path.Join(fixture.Path, "obj")).FullName;
        string source = Path.Join(output, "Program.cs"); File.WriteAllText(source, "// preserve authored source");
        BuildActivityFixture.AgeProject(fixture.Path);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var finding = Assert.Single(DeveloperOpportunityFixture.Find(scan));
        Assert.True(finding.IsBuildOutput); Assert.True(finding.IsRegeneratable);
        Assert.Equal(CleanupReadiness.NeedsReview,
            BuildCleanupProtection.Assess(scan, [finding], new())[finding.DirectoryIndex].State);
        File.Move(source, Path.Join(output, "Program.g.cs"));
        BuildActivityFixture.AgeProject(fixture.Path);
        var generated = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        finding = Assert.Single(DeveloperOpportunityFixture.Find(generated));
        Assert.Equal(CleanupReadiness.Ready,
            BuildCleanupProtection.Assess(generated, [finding], new())[finding.DirectoryIndex].State);
        Assert.Equal("// preserve authored source", File.ReadAllText(Path.Join(output, "Program.g.cs")));
    }

    private static BuildCleanupAssessment Assessment(ScanSnapshot scan, int output, int days = 3)
        => BuildCleanupProtection.Assess(scan, [Finding(scan, output)], new(days), nowUtc: Now)[output];

    private static Opportunity Finding(ScanSnapshot scan, int directory) => new(directory, ".NET build output", "Build output",
        scan.PathForFile(directory)!, scan.DirectoryLogical[directory], scan.DirectoryAllocated[directory],
        "Recognized generated output", "Stop builds", "Rebuild", false)
    { ProviderId = DeveloperOpportunityFixture.ProviderId, RuleId = ".NET build output", IsBuildOutput = true, IsRegeneratable = true };

    private static (ScanSnapshot Scan, int Output) Inventory(DateTime? outputWrite = null, DateTime? sourceWrite = null,
        DateTime? outputCreated = null, FileAttributes descendantAttributes = FileAttributes.Normal, bool incomplete = false)
    {
        var old = Now.AddDays(-14);
        static long Time(DateTime date) => date == DateTime.MinValue ? 0 : date.ToFileTimeUtc();
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, Time(old), Time(old)));
        int project = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, Time(old), Time(old)));
        builder.AddLink(project, new(1), "project");
        int output = builder.AddFile(new(new(3), FileAttributes.Directory, 0, 0, Time(old), Time(old)));
        builder.AddLink(output, new(2), "bin");
        int nested = builder.AddFile(new(new(4), FileAttributes.Directory, 0, 0, Time(old), Time(old)));
        builder.AddLink(nested, new(3), "Debug");
        int file = builder.AddFile(new(new(5), descendantAttributes, 4096, 4096,
            Time(outputWrite ?? old), Time(outputCreated ?? old)));
        builder.AddLink(file, new(4), "result.dll");
        int source = builder.AddFile(new(new(6), FileAttributes.Normal, 12, 12, Time(sourceWrite ?? old), Time(old)));
        builder.AddLink(source, new(2), "app.csproj");
        if (incomplete) builder.Warn("Fixture output incomplete", new(3));
        return (builder.Build(@"G:\fixture", "Illustrative build metadata", TimeSpan.Zero, 0, default), output);
    }
}
