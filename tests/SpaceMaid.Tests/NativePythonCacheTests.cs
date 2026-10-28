using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SpaceMaid.App;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace SpaceMaid.Tests;

[Trait("Stage", "System")]
public sealed class NativePythonCacheTests(ITestOutputHelper output)
{
    [LocalPythonFact]
    public async Task InstalledPythonRecreatesEvictedCheckedHashCachesAndKeepsOtherModes()
    {
        foreach (string python in Interpreters())
        foreach (string mode in new[] { "TIMESTAMP", "CHECKED_HASH", "UNCHECKED_HASH" })
        {
            using var fixture = new TestDirectory();
            string source = Path.Join(fixture.Path, "controlled.py");
            File.WriteAllText(source, "# Controlled fixture; compiled without execution\nVALUE = 'fixture'\n");
            var old = DateTime.UtcNow.AddDays(-30);
            File.SetCreationTimeUtc(source, old); File.SetLastWriteTimeUtc(source, old);
            string cache = await CompileAsync(python, source, mode);
            string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cache)));
            AgeOutput(cache, old);
            var (preview, policy) = await PreviewAsync(fixture.Path);
            if (mode != "CHECKED_HASH")
            { Assert.Empty(preview.Files); Assert.True(File.Exists(cache)); }
            else
            {
                Assert.True(preview.Files.Count == 1, string.Join(" | ", preview.Skipped.Select(s => s.Reason)));
                Assert.Equal(1, OptimizationExecutor.Execute(preview, policy).DeletedCount);
                Assert.False(File.Exists(cache)); Assert.Equal(old, File.GetLastWriteTimeUtc(source));
                Assert.Equal(cache, await CompileAsync(python, source, mode));
                Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cache))));
            }
            output.WriteLine($"PYTHON_CACHE executable={python} mode={mode} eligible={preview.Files.Count} exact_regeneration={mode == "CHECKED_HASH"}");
        }
    }

    [LocalPythonFact]
    public async Task RestoredSourceMtimeAndLengthCannotAuthorizeAStaleNativeTimestampCache()
    {
        foreach (string python in Interpreters())
        {
            using var fixture = new TestDirectory(); string source = Path.Join(fixture.Path, "controlled.py");
            File.WriteAllText(source, "VALUE = 'before'\n");
            var old = DateTime.UtcNow.AddDays(-30);
            File.SetCreationTimeUtc(source, old); File.SetLastWriteTimeUtc(source, old);
            string cache = await CompileAsync(python, source, "TIMESTAMP"); AgeOutput(cache, old);
            File.WriteAllText(source, "VALUE = 'after!'\n"); File.SetLastWriteTimeUtc(source, old);
            var (preview, _) = await PreviewAsync(fixture.Path);
            Assert.Empty(preview.Files); Assert.True(File.Exists(cache));
            Assert.Equal("VALUE = 'after!'\n", File.ReadAllText(source));
        }
    }

    private static async Task<(OptimizationPreview Preview, OptimizationPolicy Policy)> PreviewAsync(string path)
    {
        var scan = await new VolumeScanner().ScanAsync(path, preferFast: false);
        var findings = DeveloperOpportunityFixture.Find(scan);
        var policy = new OptimizationPolicy { Enabled = true, ScopePath = path,
            EnabledRules = findings.Where(f => f.AutomaticCleanup is not null).Select(f => f.AutomaticCleanup!.ConsentKey(f.ProviderId)).ToArray() };
        return (await new FileOperations().PreviewOptimizationAsync(scan, findings, policy), policy);
    }
    private static void AgeOutput(string path, DateTime old)
    { File.SetCreationTimeUtc(path, old); File.SetLastWriteTimeUtc(path, old); File.SetLastAccessTimeUtc(path, old); }

    private static async Task<string> CompileAsync(string python, string source, string mode)
    {
        var start = new ProcessStartInfo(python) { WorkingDirectory = Path.GetDirectoryName(source)!, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-I", "-B", "-c", "import importlib.util,py_compile,sys; p=importlib.util.cache_from_source(sys.argv[1],optimization=''); py_compile.compile(sys.argv[1],cfile=p,doraise=True,optimize=0,invalidation_mode=getattr(py_compile.PycInvalidationMode,sys.argv[2])); print(p)", source, mode })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            Assert.True(process.ExitCode == 0, await stderr);
            string path = (await stdout).Trim();
            Assert.StartsWith(Path.GetDirectoryName(source)! + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(path)); return path;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    private static string[] Interpreters()
    {
        string root = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "uv", "python");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).Take(32)
            .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^cpython-3\.(13|14)\.[0-9]+-windows-x86_64-none$"))
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .GroupBy(path => Path.GetFileName(path).Split('.')[1]).Select(group => group.Order(StringComparer.Ordinal).Last())
            .Select(path => Path.Join(path, "python.exe")).Where(File.Exists).ToArray();
    }
    private sealed class LocalPythonFact : FactAttribute
    { public LocalPythonFact() { if (Interpreters().Length == 0) Skip = "An existing local uv-managed CPython 3.13 or 3.14 runtime is required; nothing is installed."; } }
}
