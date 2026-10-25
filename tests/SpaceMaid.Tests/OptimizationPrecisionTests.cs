using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OptimizationPrecisionTests
{
    [Fact]
    public async Task CyclicMypyProofDoesNotWarmDependencyCandidatesOrReopenItsExclusiveTarget()
    {
        using var fixture = new TestDirectory();
        string cache = Directory.CreateDirectory(Path.Join(fixture.Path, ".mypy_cache")).FullName;
        File.WriteAllText(Path.Join(cache, "CACHEDIR.TAG"), "mypy fixture");
        string version = Directory.CreateDirectory(Path.Join(cache, "3.13")).FullName;
        foreach (string module in new[] { "a", "b" })
        {
            string source = Path.Join(fixture.Path, module + ".py"); File.WriteAllText(source, "# " + module);
            string data = Path.Join(version, module + ".data.json");
            File.WriteAllText(data, JsonSerializer.Serialize(new Dictionary<string, object> { [".class"] = "MypyFile", ["_fullname"] = module }));
            var old = DateTime.UtcNow.AddDays(-30); File.SetCreationTimeUtc(source, old); File.SetLastWriteTimeUtc(source, old);
            File.WriteAllText(Path.Join(version, module + ".meta.json"), JsonSerializer.Serialize(new { id = module, path = source, version_id = "1.18.2",
                mtime = new DateTimeOffset(old).ToUnixTimeSeconds(), size = new FileInfo(source).Length,
                hash = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(source))), interface_hash = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(data))),
                dependencies = new[] { module == "a" ? "b" : "a" } }));
        }
        OptimizationTests.Age(fixture.Path);
        var (scan, findings, policy) = await Read(fixture.Path);
        var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
        Assert.Equal(2, preview.Files.Count);
        foreach (var file in preview.Files) Assert.True(File.GetLastAccessTimeUtc(file.Path) < DateTime.UtcNow.AddDays(-20));
        var result = OptimizationExecutor.Execute(preview with { Files = [preview.Files[0]] }, policy);
        Assert.Equal(1, result.DeletedCount); Assert.True(File.Exists(preview.Files[1].Path));
    }

    [Fact]
    public async Task RepeatedPreviewPreservesAccessEvidenceAndExternalAccessStillProtectsTheFile()
    {
        using var fixture = new TestDirectory(); OptimizationTests.MakePython(fixture.Path, "module"); OptimizationTests.Age(fixture.Path);
        string file = Path.Join(fixture.Path, "__pycache__", "module.cpython-313.pyc");
        DateTime accessed = File.GetLastAccessTimeUtc(file);
        var (scan, findings, policy) = await Read(fixture.Path); var operations = new FileOperations();
        Assert.Single((await operations.PreviewOptimizationAsync(scan, findings, policy)).Files);
        Assert.Equal(accessed, File.GetLastAccessTimeUtc(file));
        Assert.Single((await operations.PreviewOptimizationAsync(scan, findings, policy with { KeepRecentDays = 20 })).Files);
        Assert.Equal(accessed, File.GetLastAccessTimeUtc(file));
        File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
        var warm = await operations.PreviewOptimizationAsync(scan, findings, policy);
        Assert.Empty(warm.Files); Assert.Contains(warm.Skipped, s => s.Reason.Contains("Recent observed access", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProofReadLimitReportsPartialCoverageWithoutAuthorizingAFile()
    {
        using var fixture = new TestDirectory(); var (file, _) = DotNet(fixture.Path); OptimizationTests.Age(fixture.Path);
        var (scan, findings, policy) = await Read(fixture.Path);
        var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy with { MaxProofBytesPerRun = 1 });
        Assert.Empty(preview.Files); Assert.False(preview.CoverageComplete); Assert.True(preview.Limited);
        Assert.Contains(preview.Skipped, s => s.Reason.Contains("Inspection budget", StringComparison.Ordinal)); Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData("311", 3495)]
    [InlineData("312", 3531)]
    [InlineData("314", 3627)]
    public async Task FinalCpythonMagicMatchesTheDeclaredCacheVersion(string version, ushort magic)
    {
        using var fixture = new TestDirectory(); OptimizationTests.MakePython(fixture.Path, "module"); OptimizationTests.Age(fixture.Path);
        string original = Path.Join(fixture.Path, "__pycache__", "module.cpython-313.pyc"); byte[] bytes = File.ReadAllBytes(original);
        string file = original.Replace("313", version, StringComparison.Ordinal); File.Move(original, file);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, magic); File.WriteAllBytes(file, bytes); AgeOutput(file);
        var (scan, findings, policy) = await Read(fixture.Path);
        Assert.Single((await new FileOperations().PreviewOptimizationAsync(scan, findings, policy)).Files);
        bytes[2] = 0; File.WriteAllBytes(file, bytes); AgeOutput(file);
        var current = await Read(fixture.Path);
        Assert.Empty((await new FileOperations().PreviewOptimizationAsync(current.Scan, current.Findings, current.Policy)).Files);
    }

    [Fact]
    public async Task ExhaustedProofPagesProgressPastMalformedPrefixToLaterValidFiles()
    {
        using var fixture = new TestDirectory();
        for (int i = 0; i < 4; i++) OptimizationTests.MakePython(fixture.Path, "module_" + i);
        OptimizationTests.Age(fixture.Path);
        var initial = await Read(fixture.Path); var ordered = OptimizationPlanner.Create(initial.Scan, initial.Findings, initial.Policy).Files;
        // Two invalid headers spend the first page, and the third exhausts it.
        foreach (var item in ordered.Take(3))
        { var bytes = new byte[64]; File.WriteAllBytes(item.Path, bytes); AgeOutput(item.Path); }
        var (scan, findings, policy) = await Read(fixture.Path); policy = policy with { MaxProofBytesPerRun = 128 };
        var first = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
        Assert.Empty(first.Files); Assert.False(first.CoverageComplete); Assert.True(first.NextCursor > 0);
        var next = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy, cursor: first.NextCursor);
        Assert.Contains(next.Files, f => f.Path == ordered[3].Path); Assert.All(ordered, f => Assert.True(File.Exists(f.Path)));
    }

    [Fact]
    public async Task OnlyRecordedStockFingerprintInTheExactBuildVariantIsEligible()
    {
        using var fixture = new TestDirectory();
        var (fingerprint, manifest) = DotNet(fixture.Path);
        string unique = Path.Join(Path.GetDirectoryName(fingerprint)!, "Recovery.g.cs"); File.WriteAllText(unique, "// Irreplaceable hand-maintained source");
        string custom = Path.Join(Path.GetDirectoryName(fingerprint)!, "custom.cache"); File.WriteAllText(custom, "unique cache-looking data");
        File.AppendAllText(manifest, unique + Environment.NewLine + custom + Environment.NewLine); OptimizationTests.Age(fixture.Path);
        var (scan, findings, policy) = await Read(fixture.Path);
        var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
        var file = Assert.Single(preview.Files); Assert.Equal(fingerprint, file.Path);
        Assert.Equal("Release" + Path.DirectorySeparatorChar + "net10.0", file.Evidence!.Variant);
        Assert.Equal(OptimizationDisposition.Eligible, file.Evidence.Disposition);
        var result = OptimizationExecutor.Execute(preview, policy); Assert.Equal(1, result.DeletedCount);
        Assert.True(File.Exists(unique)); Assert.True(File.Exists(custom)); Assert.True(File.Exists(manifest));
    }

    [Fact]
    public async Task MissingManifestUnknownLayoutAndChangedInputsKeepMatchingFingerprints()
    {
        using var fixture = new TestDirectory(); var (fingerprint, manifest) = DotNet(fixture.Path); OptimizationTests.Age(fixture.Path);
        var (scan, findings, policy) = await Read(fixture.Path);
        var operations = new FileOperations(); var preview = await operations.PreviewOptimizationAsync(scan, findings, policy);
        Assert.Single(preview.Files);
        File.AppendAllText(Path.Join(fixture.Path, "project.csproj"), "<!-- changed -->");
        Assert.Equal(0, OptimizationExecutor.Execute(preview, policy).DeletedCount);
        Assert.True(File.Exists(fingerprint));
        File.Delete(manifest); OptimizationTests.Age(fixture.Path);
        var current = await Read(fixture.Path);
        Assert.Empty((await operations.PreviewOptimizationAsync(current.Scan, current.Findings, current.Policy)).Files);
    }

    [Fact]
    public async Task UncheckedOrMismatchedPythonCachesStayProtected()
    {
        using var fixture = new TestDirectory(); OptimizationTests.MakePython(fixture.Path, "module"); OptimizationTests.Age(fixture.Path);
        string file = Path.Join(fixture.Path, "__pycache__", "module.cpython-313.pyc");
        byte[] bytes = File.ReadAllBytes(file); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1); File.WriteAllBytes(file, bytes);
        AgeOutput(file);
        var (scan, findings, policy) = await Read(fixture.Path);
        Assert.Empty((await new FileOperations().PreviewOptimizationAsync(scan, findings, policy)).Files);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 999999); File.WriteAllBytes(file, bytes); AgeOutput(file);
        var current = await Read(fixture.Path);
        Assert.Empty((await new FileOperations().PreviewOptimizationAsync(current.Scan, current.Findings, current.Policy)).Files);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task SourceMetadataAccessDoesNotSubstituteForReadableSource()
    {
        using var fixture = new TestDirectory(); OptimizationTests.MakePython(fixture.Path, "module"); OptimizationTests.Age(fixture.Path);
        string path = Path.Join(fixture.Path, "module.py"); var file = new FileInfo(path); var original = file.GetAccessControl();
        var denied = file.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
        try
        {
            file.SetAccessControl(denied);
            var (scan, findings, policy) = await Read(fixture.Path);
            var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings, policy);
            Assert.Empty(preview.Files); Assert.True(File.Exists(Path.Join(fixture.Path, "__pycache__", "module.cpython-313.pyc")));
        }
        finally { file.SetAccessControl(original); }
    }

    [Fact]
    public void EffectiveAncestorLeaseBlocksDirectoryReplacement()
    {
        using var fixture = new TestDirectory();
        string directory = Directory.CreateDirectory(Path.Join(fixture.Path, "producer")).FullName;
        string file = Path.Join(directory, "source.txt"); File.WriteAllText(file, "source");
        using var lease = GeneratedReadLease.Open(file);
        Assert.Throws<IOException>(() => Directory.Move(directory, Path.Join(fixture.Path, "moved")));
        lease.Verify(); Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task AnUnfamiliarPngInAnOwnedCacheHasNoAutomaticAuthority()
    {
        using var fixture = new TestDirectory(); var host = new PhotoTestHost(fixture.Path, fixture.Path, []);
        string cache = Directory.CreateDirectory(Path.Join(host.CacheDirectory, "thumbnails-v1")).FullName;
        string file = Path.Join(cache, "original.png"); File.WriteAllText(file, "unique original");
        await using var extension = new SpaceMaid.Extensions.Photos.PhotosExtension(); await extension.InitializeAsync(host, default);
        OptimizationTests.Age(fixture.Path);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var findings = extension.Analyze(scan, default).Findings.Select(f => f with { ProviderId = "space-maid.photos" }).ToArray();
        var policy = Policy(fixture.Path, findings);
        Assert.Empty((await new FileOperations().PreviewOptimizationAsync(scan, findings, policy)).Files); Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task InspectionCursorReachesBeyondAnUnevictablePrefixAndHonorsEntryBudgets()
    {
        using var fixture = new TestDirectory();
        for (int i = 0; i < 8; i++) OptimizationTests.MakePython(fixture.Path, "module_" + i);
        OptimizationTests.Age(fixture.Path);
        var (scan, findings, policy) = await Read(fixture.Path); policy = policy with { MaxEntriesPerRun = 3, MaxFilesPerRun = 1 };
        var paths = new HashSet<string>(); int cursor = 0;
        for (int pass = 0; pass < scan.FileCount + 1; pass++)
        {
            var preview = OptimizationPlanner.Create(scan, findings, policy, cursor: cursor); cursor = preview.NextCursor;
            Assert.InRange(preview.ExaminedEntries, 0, 3);
            foreach (var file in preview.Files) paths.Add(file.Path);
        }
        Assert.Equal(8, paths.Count);
        var first = OptimizationPlanner.Create(scan, findings, policy with { MaxEntriesPerRun = 1000, MaxFilesPerRun = 8 });
        var blocked = first.Files[0];
        var afterCooldown = OptimizationPlanner.Create(scan, findings, policy with { MaxEntriesPerRun = 1000, MaxFilesPerRun = 8 },
            cooldowns: [new(blocked.Path, blocked.LastWriteFileTime, blocked.CreationFileTime, DateTime.UtcNow.AddHours(2))]);
        Assert.DoesNotContain(afterCooldown.Files, f => f.Path == blocked.Path); Assert.Equal(7, afterCooldown.Files.Count);
    }

    [Fact]
    public async Task ValuableCheckoutContextStillAllowsAnIndependentlyVerifiedCacheAction()
    {
        using var fixture = new TestDirectory(); OptimizationTests.MakePython(fixture.Path, "module"); OptimizationTests.Age(fixture.Path);
        var (scan, findings, policy) = await Read(fixture.Path);
        var worktree = new Opportunity(scan.RootIndex, "Local work", "Git worktrees", scan.RootPath, scan.LogicalBytes, scan.AllocatedBytes,
            "unique commits", "keep", "review", true) { IsWorktree = true };
        var preview = await new FileOperations().PreviewOptimizationAsync(scan, findings.Append(worktree).ToArray(), policy);
        Assert.Single(preview.Files); Assert.Equal(1, OptimizationExecutor.Execute(preview, policy).DeletedCount);
        Assert.True(File.Exists(Path.Join(fixture.Path, "module.py"))); Assert.True(Directory.Exists(fixture.Path));
    }

    internal static (string File, string Manifest) DotNet(string root)
    {
        File.WriteAllText(Path.Join(root, "project.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string variant = Directory.CreateDirectory(Path.Join(root, "obj", "Release", "net10.0")).FullName;
        string file = Path.Join(variant, "project.csproj.CoreCompileInputs.cache"); File.WriteAllText(file, new string('a', 64) + "\n");
        string manifest = Path.Join(variant, "project.csproj.FileListAbsolute.txt"); File.WriteAllText(manifest, file + Environment.NewLine);
        return (file, manifest);
    }
    private static void AgeOutput(string path)
    { var old = DateTime.UtcNow.AddDays(-30); File.SetCreationTimeUtc(path, old); File.SetLastWriteTimeUtc(path, old); File.SetLastAccessTimeUtc(path, old); }
    private static OptimizationPolicy Policy(string root, IReadOnlyList<Opportunity> findings) => new() { Enabled = true, ScopePath = root,
        EnabledRules = findings.Where(f => f.AutomaticCleanup is not null).Select(f => f.AutomaticCleanup!.ConsentKey(f.ProviderId)).ToArray() };
    private static async Task<(ScanSnapshot Scan, IReadOnlyList<Opportunity> Findings, OptimizationPolicy Policy)> Read(string root)
    { var scan = await new VolumeScanner().ScanAsync(root, preferFast: false); var findings = DeveloperOpportunityFixture.Find(scan); return (scan, findings, Policy(root, findings)); }
}
