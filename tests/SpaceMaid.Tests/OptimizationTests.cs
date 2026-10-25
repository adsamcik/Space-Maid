using System.IO;
using System.Runtime.InteropServices;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OptimizationTests
{
    [Fact]
    public async Task NoRulesAreEnabledByDefaultAndGeneratedPatternsDoNotIncludeOtherFiles()
    {
        using var fixture = new TestDirectory();
        MakePython(fixture.Path, "example");
        string cache = Path.Join(fixture.Path, "__pycache__");
        File.WriteAllText(Path.Join(cache, "notes.txt"), "keep");
        File.WriteAllText(Path.Join(cache, "orphan.cpython-313.pyc"), "no source");
        string archive = Directory.CreateDirectory(Path.Join(cache, "archive")).FullName;
        File.WriteAllText(Path.Join(archive, "example.cpython-313.pyc"), "unfamiliar archived bytecode");
        Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        Assert.Empty(OptimizationPlanner.Create(scan, findings, new()).Files);
        var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
        var file = Assert.Single(preview.Files);
        Assert.EndsWith("example.cpython-313.pyc", file.Path);
        Assert.NotNull(file.Identity);
        Assert.True(File.Exists(Path.Join(cache, "notes.txt")));
        Assert.Empty(OptimizationPlanner.Create(scan, findings, policy with { EnabledRules = [] }).Files);
    }

    [Fact]
    public async Task RetentionNewCopiesAndExcludedFoldersStayOutOfPreview()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "example"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        Assert.Empty(OptimizationPlanner.Create(scan, findings, policy with { ExcludedPaths = [Path.Join(fixture.Path, "__pycache__")] }).Files);
        Assert.Empty(OptimizationPlanner.Create(scan, findings, policy with { ScopePath = Path.Join(fixture.Path, "other") }).Files);
        string file = Path.Join(fixture.Path, "__pycache__", "example.cpython-313.pyc");
        File.SetCreationTimeUtc(file, DateTime.UtcNow);
        var updated = await Inventory(fixture.Path);
        Assert.Empty(OptimizationPlanner.Create(updated.Scan, updated.Findings, updated.Policy).Files);
        Age(fixture.Path);
        File.SetLastWriteTimeUtc(Path.Join(fixture.Path, "example.py"), DateTime.UtcNow);
        updated = await Inventory(fixture.Path);
        Assert.Empty(OptimizationPlanner.Create(updated.Scan, updated.Findings, updated.Policy).Files);
    }

    [Fact]
    public async Task DottedPythonNamesNeedTheirOwnSourceRatherThanAnUnrelatedModule()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "foo");
        string dotted = Path.Join(fixture.Path, "__pycache__", "foo.bar.cpython-313.opt-2.pyc");
        File.WriteAllText(dotted, "only remaining bytecode"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        Assert.DoesNotContain(OptimizationPlanner.Create(scan, findings, policy).Files, f => f.Path == dotted);
        File.WriteAllText(Path.Join(fixture.Path, "foo.bar.py"), "matching source"); Age(fixture.Path);
        var updated = await Inventory(fixture.Path);
        Assert.Contains(OptimizationPlanner.Create(updated.Scan, updated.Findings, updated.Policy).Files, f => f.Path == dotted);
    }

    [Fact]
    public async Task DotNetOnlyOffersIntermediateGeneratedFilesAndKeepsOrdinaryOutputs()
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "project.csproj"), "<Project />");
        string output = Directory.CreateDirectory(Path.Join(fixture.Path, "obj")).FullName;
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "bin")).FullName;
        File.WriteAllText(Path.Join(output, "compiler.cache"), "generated");
        File.WriteAllText(Path.Join(output, "project.g.cs"), "generated");
        File.WriteAllText(Path.Join(output, "keep.txt"), "authored");
        File.WriteAllText(Path.Join(bin, "project.dll"), "binary"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        var preview = OptimizationPlanner.Create(scan, findings, policy);
        Assert.Equal(2, preview.Files.Count);
        Assert.All(preview.Files, f => Assert.Equal(output, f.ContainerPath));
        Assert.Null(Assert.Single(findings, f => f.Path == bin).AutomaticCleanup);
        File.WriteAllText(Path.Join(output, "handwritten.cs"), "keep"); Age(fixture.Path);
        var updated = await Inventory(fixture.Path);
        Assert.Empty(OptimizationPlanner.Create(updated.Scan, updated.Findings, updated.Policy).Files);
    }

    [Fact]
    public async Task MypyContributesGeneratedAnalysisWhileKeepingUnfamiliarFiles()
    {
        using var fixture = new TestDirectory();
        string cache = Directory.CreateDirectory(Path.Join(fixture.Path, ".mypy_cache")).FullName;
        File.WriteAllText(Path.Join(cache, "CACHEDIR.TAG"), "fixture cache marker");
        string version = Directory.CreateDirectory(Path.Join(cache, "3.13")).FullName;
        File.WriteAllText(Path.Join(version, "module.meta.json"), "generated");
        File.WriteAllText(Path.Join(version, "module.data.json"), "generated");
        File.WriteAllText(Path.Join(cache, "notes.txt"), "keep"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
        Assert.Equal(2, preview.Files.Count);
        Assert.Equal(2, OptimizationExecutor.Execute(preview, policy).DeletedCount);
        Assert.Equal("keep", File.ReadAllText(Path.Join(cache, "notes.txt")));
        Assert.True(File.Exists(Path.Join(cache, "CACHEDIR.TAG")));
    }

    [Fact]
    public async Task ProviderConsentIsSeparateAndChangingPatternsRequiresNewConsent()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "example"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        var original = Assert.Single(findings);
        await using var catalog = new OpportunityCatalog();
        catalog.Register("test.first", new Provider(original)); catalog.Register("test.second", new Provider(original));
        var shared = catalog.Find(scan);
        Assert.Equal(2, shared.Count);
        var first = shared.Single(f => f.ProviderId == "test.first");
        var preview = OptimizationPlanner.Create(scan, shared, policy with { EnabledRules = [first.AutomaticCleanup!.ConsentKey(first.ProviderId)] });
        Assert.Equal(first.AutomaticCleanup.ConsentKey("test.first"), Assert.Single(preview.Files).RuleKey);
        var changed = original with { AutomaticCleanup = new("python-bytecode", "Changed", "Regenerates", [".pyc", ".txt"]) };
        Assert.Empty(OptimizationPlanner.Create(scan, [changed], policy).Files);
        var both = OptimizationPlanner.Create(scan, shared, policy with { EnabledRules = shared.Select(f => f.AutomaticCleanup!.ConsentKey(f.ProviderId)).ToArray() });
        Assert.Single(both.Files); // The same file is never counted or deleted twice.
    }

    [Fact]
    public async Task ManualAndWorktreeFindingsBlockEvenNestedGeneratedFiles()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "example"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        var original = Assert.Single(findings);
        Assert.Empty(OptimizationPlanner.Create(scan, [original with { RequiresManualReview = true }], policy).Files);
        Assert.Empty(OptimizationPlanner.Create(scan, [original with { IsWorktree = true }], policy).Files);
        Assert.Empty(OptimizationPlanner.Create(scan, [original with { CleanupBlockReason = "managed" }], policy).Files);
        var parent = original with { DirectoryIndex = scan.RootIndex, Path = scan.RootPath, IsBuildOutput = false,
            IsWorktree = true, AutomaticCleanup = null, RuleId = "worktree" };
        Assert.Empty(OptimizationPlanner.Create(scan, [original, parent], policy).Files);
    }

    [Fact]
    public async Task RunLimitsBoundBothFileCountAndBytes()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "first"); MakePython(fixture.Path, "second"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        var preview = OptimizationPlanner.Create(scan, findings, policy with { MaxFilesPerRun = 1 });
        Assert.Single(preview.Files); Assert.True(preview.Limited);
        preview = OptimizationPlanner.Create(scan, findings, policy with { MaxBytesPerRun = 1 });
        Assert.Empty(preview.Files); Assert.True(preview.Limited);
    }

    [Fact]
    public async Task CappedRunsContinueWithoutTreatingTheirOwnDeletionAsBuildActivity()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "first"); MakePython(fixture.Path, "second"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        policy = policy with { MaxFilesPerRun = 1 };
        var operations = new FileOperations();
        var preview = await operations.PreviewOptimizationAsync(scan, findings, policy);
        Assert.Equal(1, OptimizationExecutor.Execute(preview, policy).DeletedCount);
        var next = await Inventory(fixture.Path);
        var nextPreview = await operations.PreviewOptimizationAsync(next.Scan, next.Findings, policy);
        Assert.Equal(1, OptimizationExecutor.Execute(nextPreview, policy).DeletedCount);
    }

    [Fact]
    public async Task AVolumeMismatchNeverDeletesEvenWhenTheFileIdMatches()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "example"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
        var file = Assert.Single(preview.Files);
        var changedVolume = preview with { Files = [file with { VolumeSerialNumber = file.VolumeSerialNumber!.Value ^ 1UL }] };
        Assert.Equal(0, OptimizationExecutor.Execute(changedVolume, policy).DeletedCount);
        Assert.True(File.Exists(file.Path));
    }

    [Fact]
    public async Task ExecutionDeletesOnlyCapturedGeneratedFilesAndHonorsDisabledPolicy()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "example"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        var operations = new FileOperations();
        var preview = await operations.PreviewOptimizationAsync(scan, findings, policy);
        Assert.Throws<InvalidOperationException>(() => OptimizationExecutor.Execute(preview, policy with { Enabled = false }));
        var result = await operations.OptimizeAsync(scan, findings, preview, policy, DeveloperOpportunityFixture.Find, default, (_, _) => { });
        Assert.Equal(1, result.DeletedCount);
        Assert.False(File.Exists(Assert.Single(preview.Files).Path));
        Assert.True(File.Exists(Path.Join(fixture.Path, "example.py")));
        Assert.True(Directory.Exists(Path.Join(fixture.Path, "__pycache__")));
    }

    [Fact]
    public async Task LockedAndReplacedFilesAreKeptWithoutRecapturingTheirIdentities()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "first"); MakePython(fixture.Path, "second"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
        Assert.Equal(2, preview.Files.Count);
        var first = preview.Files[0]; var second = preview.Files[1];
        using (var held = File.Open(first.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            string original = second.Path + ".original";
            File.Move(second.Path, original); File.Copy(original, second.Path);
            File.SetCreationTimeUtc(second.Path, DateTime.FromFileTimeUtc(second.CreationFileTime));
            File.SetLastWriteTimeUtc(second.Path, DateTime.FromFileTimeUtc(second.LastWriteFileTime));
            var result = OptimizationExecutor.Execute(preview, policy);
            Assert.Equal(0, result.DeletedCount); Assert.Equal(2, result.Files.Count);
            Assert.All(result.Files, r => Assert.Equal(OptimizationOutcome.Skipped, r.Outcome));
        }
        Assert.All(preview.Files, f => Assert.True(File.Exists(f.Path)));
    }

    [Fact]
    public async Task MissingSourceNewFilesAndRuleChangesCannotWidenAPreview()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "example"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        var operations = new FileOperations(); var preview = await operations.PreviewOptimizationAsync(scan, findings, policy);
        File.Delete(Path.Join(fixture.Path, "example.py"));
        var result = OptimizationExecutor.Execute(preview, policy);
        Assert.Equal(0, result.DeletedCount);
        Assert.True(File.Exists(Assert.Single(preview.Files).Path));
        File.WriteAllText(Path.Join(fixture.Path, "example.py"), "source"); MakePython(fixture.Path, "new"); Age(fixture.Path);
        result = await operations.OptimizeAsync(scan, findings, preview, policy, DeveloperOpportunityFixture.Find, default, (_, _) => { });
        Assert.True(File.Exists(Path.Join(fixture.Path, "__pycache__", "new.cpython-313.pyc")));
        Assert.True(result.Files.Count <= preview.Files.Count);
    }

    [Fact]
    public async Task HardLinksAndReadOnlyFilesAreNotCapturedForDeletion()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "first"); MakePython(fixture.Path, "second"); Age(fixture.Path);
        string first = Path.Join(fixture.Path, "__pycache__", "first.cpython-313.pyc");
        Assert.True(CreateHardLinkW(Path.Join(fixture.Path, "alias"), first, 0));
        string second = Path.Join(fixture.Path, "__pycache__", "second.cpython-313.pyc");
        File.SetAttributes(second, FileAttributes.ReadOnly);
        try
        {
            var (scan, findings, policy) = await Inventory(fixture.Path);
            var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
            Assert.Empty(preview.Files);
        }
        finally { File.SetAttributes(second, FileAttributes.Normal); }
    }

    [Fact]
    public async Task AlternateDataStreamsAreKeptEvenWhenTheScanDoesNotInventoryThem()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "example");
        string file = Path.Join(fixture.Path, "__pycache__", "example.cpython-313.pyc");
        File.WriteAllText(file + ":notes", "unique attached notes"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
        Assert.Empty(preview.Files);
        Assert.True(File.Exists(file)); Assert.Equal("unique attached notes", File.ReadAllText(file + ":notes"));
    }

    [Fact]
    public async Task PhotosContributeOnlyTheirOwnRegenerableThumbnailCache()
    {
        using var fixture = new TestDirectory();
        var host = new PhotoTestHost(fixture.Path, fixture.Path, []);
        string thumbnails = Directory.CreateDirectory(Path.Join(host.CacheDirectory, "thumbnails-v1")).FullName;
        string cached = Path.Join(thumbnails, "generated.png"); File.WriteAllText(cached, "cached");
        string original = Path.Join(fixture.Path, "original.png"); File.WriteAllText(original, "original");
        await using var extension = new SpaceMaid.Extensions.Photos.PhotosExtension();
        await extension.InitializeAsync(host, default); Age(fixture.Path);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        await using var catalog = new OpportunityCatalog(); catalog.Register("space-maid.photos", extension);
        var findings = catalog.Find(scan); var finding = Assert.Single(findings);
        var policy = new OptimizationPolicy { Enabled = true, ScopePath = fixture.Path,
            EnabledRules = [finding.AutomaticCleanup!.ConsentKey(finding.ProviderId)] };
        var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
        Assert.Equal(cached, Assert.Single(preview.Files).Path);
        Assert.Equal(1, OptimizationExecutor.Execute(preview, policy).DeletedCount);
        Assert.Equal("original", File.ReadAllText(original)); Assert.True(Directory.Exists(host.DataDirectory));
    }

    [Fact]
    public async Task CancellationStopsBeforeTheNextFileAndRetainsCompletedResults()
    {
        using var fixture = new TestDirectory(); MakePython(fixture.Path, "first"); MakePython(fixture.Path, "second"); Age(fixture.Path);
        var (scan, findings, policy) = await Inventory(fixture.Path);
        var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
        var cancelled = OptimizationExecutor.Execute(preview, policy, new(true));
        Assert.Empty(cancelled.Files); Assert.True(cancelled.Cancelled);
        using var work = new CancellationTokenSource();
        var result = OptimizationExecutor.Execute(preview, policy, work.Token, new CancelAfterFile(work));
        Assert.Equal(1, result.DeletedCount); Assert.True(result.Cancelled);
        Assert.Equal(1, preview.Files.Count(f => File.Exists(f.Path)));
    }

    [Fact]
    public void SettingsRoundTripAndCorruptSettingsNeverSilentlyEnableCleanup()
    {
        using var fixture = new TestDirectory(); string path = Path.Join(fixture.Path, "settings.json");
        Assert.False(OptimizationSettings.Load(path).Policy.Enabled);
        var policy = new OptimizationPolicy { Enabled = true, ScopePath = fixture.Path, EnabledRules = ["provider/rule/version"],
            ExcludedPaths = [Path.Join(fixture.Path, "keep")], KeepRecentDays = 30 };
        new OptimizationSettings(policy).Save(path);
        var restored = OptimizationSettings.Load(path).Policy;
        Assert.True(restored.Enabled); Assert.Equal(30, restored.KeepRecentDays); Assert.Equal(policy.ExcludedPaths, restored.ExcludedPaths);
        File.WriteAllText(path, "{}"); Assert.Throws<InvalidDataException>(() => OptimizationSettings.Load(path));
        Assert.Throws<ArgumentException>(() => (policy with { KeepRecentDays = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (policy with { ExcludedPaths = ["relative"] }).Validate());
    }

    private static async Task<(ScanSnapshot Scan, IReadOnlyList<Opportunity> Findings, OptimizationPolicy Policy)> Inventory(string path)
    {
        var scan = await new VolumeScanner().ScanAsync(path, preferFast: false);
        var findings = DeveloperOpportunityFixture.Find(scan);
        return (scan, findings, new OptimizationPolicy { Enabled = true, ScopePath = path,
            EnabledRules = findings.Where(f => f.AutomaticCleanup is not null).Select(f => f.AutomaticCleanup!.ConsentKey(f.ProviderId)).ToArray() });
    }
    internal static void MakePython(string root, string module)
    {
        File.WriteAllText(Path.Join(root, module + ".py"), "print('fixture')");
        string cache = Directory.CreateDirectory(Path.Join(root, "__pycache__")).FullName;
        File.WriteAllText(Path.Join(cache, module + ".cpython-313.pyc"), "fixture bytecode");
    }
    internal static void Age(string root)
    {
        BuildActivityFixture.AgeProject(root, 30);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetLastAccessTimeUtc(file, DateTime.UtcNow.AddDays(-30));
    }
    private sealed class Provider(Opportunity finding) : IOpportunityExtension
    { public OpportunityAnalysis Analyze(IOpportunityInventory inventory, CancellationToken token) => new([finding], new([], [])); }
    private sealed class CancelAfterFile(CancellationTokenSource work) : IProgress<OptimizationFileResult>
    { public void Report(OptimizationFileResult value) => work.Cancel(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string name, string existing, nint security);
}
