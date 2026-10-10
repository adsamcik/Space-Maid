using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class AnalysisTests
{
    [Fact]
    public async Task RulesRequireProjectEvidenceAndThePlanNeverMutatesFiles()
    {
        using var fixture = new TestDirectory();
        Directory.CreateDirectory(Path.Join(fixture.Path, "unrelated", "bin"));
        string project = Directory.CreateDirectory(Path.Join(fixture.Path, "project")).FullName;
        await File.WriteAllTextAsync(Path.Join(project, "app.csproj"), "<Project />");
        string bin = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
        string file = Path.Join(bin, "result.dll");
        await File.WriteAllBytesAsync(file, new byte[8192]);
        Directory.CreateDirectory(Path.Join(fixture.Path, "linked"));
        await File.WriteAllTextAsync(Path.Join(fixture.Path, "linked", ".git"), "gitdir: example");
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var opportunities = OpportunityDetector.Find(scan);
        Assert.Equal(2, opportunities.Count);
        var build = Assert.Single(opportunities, o => o.Category == "Build output");
        Assert.Equal(bin, build.Path);
        Assert.True(Assert.Single(opportunities, o => o.Category == "Git worktrees").RequiresManualReview);
        var plan = CleanupPlanner.Create(scan, [build]);
        Assert.False(plan.CanExecute);
        Assert.Equal(8192, plan.EstimatedBytes);
        Assert.Throws<NotSupportedException>(() => CleanupPlanner.Execute(plan));
        Assert.Equal(8192, new FileInfo(file).Length);
    }

    [Fact]
    public void NestedSelectionsAndSharedAllocationDoNotInflateEstimates()
    {
        var b = new ScanBuilder { CompleteVolumeLinks = true, AllStreams = true };
        b.RootIndex = b.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
        int env = b.AddFile(new(new(10), FileAttributes.Directory, 0, 0, 0)); b.AddLink(env, new(5), ".venv");
        int cache = b.AddFile(new(new(11), FileAttributes.Directory, 0, 0, 0)); b.AddLink(cache, new(10), "__pycache__");
        int marker = b.AddFile(new(new(12), FileAttributes.Normal, 10, 0, 0)); b.AddLink(marker, new(10), "pyvenv.cfg");
        int bytecode = b.AddFile(new(new(13), FileAttributes.Normal, 8192, 8192, 0)); b.AddLink(bytecode, new(11), "a.pyc");
        int shared = b.AddFile(new(new(14), FileAttributes.Normal, 4096, 4096, 0));
        b.AddLink(shared, new(11), "shared.pyc"); b.AddLink(shared, new(5), "keep.pyc");
        var scan = b.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        var found = OpportunityDetector.Find(scan);
        Assert.Equal(2, found.Count);
        var plan = CleanupPlanner.Create(scan, found);
        Assert.Single(plan.Locations);
        Assert.Equal(8192, plan.EstimatedBytes);
        Assert.Equal(4096, plan.ExcludedSharedBytes);
    }

    [Fact]
    public async Task KnownCacheAndAgentDirectoriesAreRecognizedWithoutInferringAbandonment()
    {
        using var fixture = new TestDirectory();
        string[] folders = [".nuget/packages", ".gradle/caches", ".codex/sessions", ".codex/logs", ".claude/debug", "AppData/Local/npm-cache", "AppData/Local/pip/Cache"];
        foreach (string folder in folders) Directory.CreateDirectory(Path.Join(fixture.Path, folder));
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var found = OpportunityDetector.Find(scan);
        Assert.Equal(folders.Length, found.Count);
        Assert.All(found.Where(o => o.Category == "Agent files"), o => Assert.True(o.RequiresManualReview));
    }
}
