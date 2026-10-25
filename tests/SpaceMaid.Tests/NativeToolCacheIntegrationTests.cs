using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpaceMaid.Core.Optimization;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class NativeToolCacheIntegrationTests
{
    [LocalNativeNpmFact]
    public async Task InstalledNpmVerifiesAnOfflineFixtureCacheThroughTheRealHostService()
    {
        using var fixture = new NativeCacheFixture(FindNpm()!);
        string archive = Path.Join(fixture.Root, "fixture-package.tgz");
        CreatePackage(archive);
        byte[] originalArchive = await File.ReadAllBytesAsync(archive);
        string originalHash = Convert.ToHexString(SHA256.HashData(originalArchive));
        var seeded = await fixture.RunAsync(new(fixture.Launcher.Executable,
            fixture.Launcher.Prefix.Concat(new[] { "--global", "--cache", fixture.Cache, "--offline", "--ignore-scripts", "cache", "add", archive }).ToArray(),
            fixture.Working, new Dictionary<string, string>(), TimeSpan.FromSeconds(30)), default);
        Assert.True(seeded.ExitCode == 0, seeded.Error + seeded.Output);
        Assert.True(Directory.Exists(Path.Join(fixture.Cache, "_cacache")));
        var cachedBefore = ContentHashes(fixture.Cache);
        Assert.Contains(originalHash, cachedBefore);

        var inspected = (await fixture.Service.InspectAsync()).Single(row => row.Kind == ToolCacheKind.Npm);
        Assert.True(inspected.CanPrepare, inspected.Details);
        Assert.Equal(fixture.Cache, inspected.Root);
        Assert.Equal(OptimizationDisposition.Review, inspected.Evidence.Disposition);
        var plan = await fixture.Service.PrepareAsync(inspected);
        var result = await fixture.Service.ExecuteAsync(plan);
        Assert.True(result.Completed, result.Error + result.Output); Assert.False(result.Cancelled);
        Assert.Contains("verify", plan.CommandPreview, StringComparison.Ordinal);
        Assert.Contains("Cache verified", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archive))));
        Assert.Equal(cachedBefore, ContentHashes(fixture.Cache));
        Assert.False(Directory.Exists(Path.Join(fixture.Working, "node_modules")));
        Assert.False(File.Exists(Path.Join(fixture.Working, "package-lock.json")));
        Assert.All(fixture.Commands, command =>
        {
            Assert.Equal(fixture.Cache, command.Environment["NPM_CONFIG_CACHE"]);
            Assert.Equal("true", command.Environment["NPM_CONFIG_OFFLINE"]);
            Assert.Equal("true", command.Environment["NPM_CONFIG_IGNORE_SCRIPTS"]);
            Assert.Equal("", command.Environment["NODE_OPTIONS"]);
            Assert.Equal(fixture.Working, command.WorkingDirectory);
            Assert.DoesNotContain(command.Arguments, value => value is "install" or "dedupe" or "rebuild");
        });
        Assert.Single(fixture.Commands, command => command.Arguments.Contains("verify"));
    }

    [LocalUnsupportedPnpmFact]
    public async Task InstalledUnsupportedPnpmRemainsProtectedWithoutInspectingOrPruningAStore()
    {
        using var fixture = new NativeCacheFixture(FindPnpm()!, ToolCacheKind.Pnpm);
        var observed = (await fixture.Service.InspectAsync()).Single(row => row.Kind == ToolCacheKind.Pnpm);
        Assert.Equal(ToolCacheDisposition.Unavailable, observed.Disposition);
        Assert.False(observed.CanPrepare);
        Assert.Contains("outside the supported", observed.Details, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(observed));
        Assert.All(fixture.Commands, command => Assert.Contains("--version", command.Arguments));
        Assert.DoesNotContain(fixture.Commands, command => command.Arguments.Contains("store") || command.Arguments.Contains("prune"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Cache));
    }

    private static void CreatePackage(string path)
    {
        using var output = File.Create(path);
        using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        // The sentinel script is deliberately not executable project code. Any
        // accidental lifecycle execution would fail this offline cache-only test.
        Add("package/package.json", "{\"name\":\"space-maid-owned-cache-fixture\",\"version\":\"1.0.0\",\"scripts\":{\"preinstall\":\"SPACEMAID_MUST_NOT_EXECUTE_PACKAGE_SCRIPTS\"}}");
        Add("package/index.js", "module.exports = 'fixture content only';\n");
        void Add(string name, string content)
        {
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(content));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = bytes });
        }
    }

    private static string[] ContentHashes(string cache)
        => Directory.EnumerateFiles(Path.Join(cache, "_cacache", "content-v2"), "*", SearchOption.AllDirectories)
            .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static ToolCacheLauncher? FindNpm()
    {
        var launcher = ToolCacheCommandRunner.Resolve(ToolCacheKind.Npm);
        return launcher?.Prefix.Count == 1 && launcher.Prefix[0].EndsWith("npm-cli.js", StringComparison.OrdinalIgnoreCase) ? launcher : null;
    }
    private static ToolCacheLauncher? FindPnpm()
    {
        var direct = ToolCacheCommandRunner.Resolve(ToolCacheKind.Pnpm);
        if (direct?.Prefix.Count == 1) return direct;
        string runtime = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "node");
        string node = Path.Join(runtime, "bin", "node.exe"), cli = Path.Join(runtime, "node_modules", "pnpm", "bin", "pnpm.mjs");
        return File.Exists(node) && File.Exists(cli) ? new(node, [cli]) : null;
    }
    private static int? PackageMajor(ToolCacheLauncher? launcher)
    {
        if (launcher?.Prefix.Count != 1) return null;
        string path = Path.GetFullPath(Path.Join(Path.GetDirectoryName(launcher.Prefix[0])!, "..", "package.json"));
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length > 256 * 1024) return null;
            using var json = JsonDocument.Parse(input);
            string? version = json.RootElement.GetProperty("version").GetString();
            return int.TryParse(version?.Split('.')[0], out int major) ? major : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException) { return null; }
    }
    private sealed class LocalNativeNpmFact : FactAttribute
    {
        public LocalNativeNpmFact()
        {
            if (PackageMajor(FindNpm()) is not (>= 9 and <= 11))
                Skip = "Requires an existing direct Node/npm CLI in the supported npm 9–11 range; no tool installation is performed.";
        }
    }
    private sealed class LocalUnsupportedPnpmFact : FactAttribute
    {
        public LocalUnsupportedPnpmFact()
        {
            if (PackageMajor(FindPnpm()) is not (> 10 or < 9))
                Skip = "Requires an existing direct pnpm CLI outside the supported 9–10 contract to verify native rejection.";
        }
    }

    private sealed class NativeCacheFixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        private readonly string _userConfig, _globalConfig, _prefix;
        public string Root => _directory.Path;
        public string Cache { get; }
        public string Working { get; }
        public ToolCacheLauncher Launcher { get; }
        public List<ToolCacheCommand> Commands { get; } = [];
        public ToolCacheService Service { get; }
        internal NativeCacheFixture(ToolCacheLauncher launcher, ToolCacheKind kind = ToolCacheKind.Npm)
        {
            Launcher = launcher;
            Cache = Directory.CreateDirectory(Path.Join(Root, "cache")).FullName;
            Working = Directory.CreateDirectory(Path.Join(Root, "neutral-host")).FullName;
            _prefix = Directory.CreateDirectory(Path.Join(Root, "prefix")).FullName;
            _userConfig = Path.Join(Root, "user.npmrc"); _globalConfig = Path.Join(Root, "global.npmrc");
            File.WriteAllText(_userConfig, ""); File.WriteAllText(_globalConfig, "");
            Service = new(Working, resolve: tool => tool == kind ? launcher : null, run: RunAsync,
                environment: _ => null, userProfile: Path.Join(Root, "empty-profile"));
        }
        public async Task<ToolCacheCommandResult> RunAsync(ToolCacheCommand original, CancellationToken token)
        {
            AssertOwned(Cache); AssertOwned(Working); AssertOwned(_userConfig); AssertOwned(_globalConfig); AssertOwned(_prefix);
            var environment = new Dictionary<string, string>(original.Environment, StringComparer.OrdinalIgnoreCase)
            {
                ["NODE_OPTIONS"] = "", ["NODE_PATH"] = "", ["NPM_CONFIG_CACHE"] = Cache, ["NPM_CONFIG_USERCONFIG"] = _userConfig,
                ["NPM_CONFIG_GLOBALCONFIG"] = _globalConfig, ["NPM_CONFIG_PREFIX"] = _prefix,
                ["NPM_CONFIG_OFFLINE"] = "true", ["NPM_CONFIG_IGNORE_SCRIPTS"] = "true", ["NPM_CONFIG_UPDATE_NOTIFIER"] = "false",
                ["NPM_CONFIG_AUDIT"] = "false", ["NPM_CONFIG_FUND"] = "false", ["NPM_CONFIG_STORE_DIR"] = Cache,
                ["NPM_CONFIG_MANAGE_PACKAGE_MANAGER_VERSIONS"] = "false", ["PNPM_CONFIG_MANAGE_PACKAGE_MANAGER_VERSIONS"] = "false",
                ["PNPM_CONFIG_IGNORE_PNPMFILE"] = "true", ["COREPACK_ENABLE_NETWORK"] = "0", ["COREPACK_ENABLE_PROJECT_SPEC"] = "0",
                ["NO_COLOR"] = "1", ["CI"] = "true"
            };
            var bounded = original with { Environment = environment, WorkingDirectory = Working };
            Commands.Add(bounded);
            return await ToolCacheCommandRunner.RunAsync(bounded, token);
        }
        private void AssertOwned(string path)
        {
            Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        public void Dispose() => _directory.Dispose();
    }
}
