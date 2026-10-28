using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpaceMaid.Core.Deduplication;
using SpaceMaid.Core.Optimization;
using Xunit;

namespace SpaceMaid.Tests;

[Trait("Stage", "System")]
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

    [LocalNativePnpmFact]
    public async Task InstalledPnpmPrunesUnreferencedContentAndPreservesTheInstalledProjectThroughTheRealHostService()
    {
        var launcher = FindSupportedPnpm();
        Assert.NotNull(launcher);
        using var fixture = new NativeCacheFixture(launcher, ToolCacheKind.Pnpm);
        string liveArchive = Path.Join(fixture.Root, "live-package.tgz"), unusedArchive = Path.Join(fixture.Root, "unused-package.tgz");
        const string liveName = "space-maid-live-fixture", unusedName = "space-maid-unused-fixture";
        const string livePayload = "owned live package payload\n", unusedPayload = "owned unused package payload\n";
        // Dependencies without lifecycle scripts can be imported as hardlinks.
        // The generated project has a failing lifecycle sentinel of its own.
        CreatePackage(liveArchive, liveName, livePayload, lifecycleScript: false);
        CreatePackage(unusedArchive, unusedName, unusedPayload, lifecycleScript: false);
        string project = fixture.OwnedDirectory("project");
        File.WriteAllText(Path.Join(project, "package.json"),
            "{\"name\":\"space-maid-owned-project\",\"private\":true,\"scripts\":{\"preinstall\":\"SPACEMAID_MUST_NOT_EXECUTE_PROJECT_SCRIPTS\"},\"dependencies\":{\"" + liveName + "\":\"file:../live-package.tgz\"}}");
        File.WriteAllText(Path.Join(project, "source.txt"), "owned original project source\n");
        await fixture.PnpmAsync(["--ignore-workspace", "--config.ignore-pnpmfile=true", "store", "add", unusedArchive]);
        await fixture.PnpmAsync(["--ignore-workspace", "--config.ignore-pnpmfile=true", "--dir", project,
            "--offline", "--ignore-scripts", "--package-import-method=hardlink", "--no-frozen-lockfile", "install"]);

        string reportedStore = ToolCacheService.ParsePath((await fixture.PnpmAsync(
            ["--ignore-workspace", "--config.ignore-pnpmfile=true", "store", "path"])).Output);
        fixture.AssertOwned(reportedStore);
        string[] storeFiles = Directory.EnumerateFiles(Path.Join(reportedStore, "files"), "*", SearchOption.AllDirectories).ToArray();
        string liveStored = Assert.Single(storeFiles, path => Hash(path) == Hash(Encoding.UTF8.GetBytes(livePayload)));
        string unusedStored = Assert.Single(storeFiles, path => Hash(path) == Hash(Encoding.UTF8.GetBytes(unusedPayload)));
        using (var shared = StorageFile.Open(liveStored, metadataOnly: true)) Assert.True(shared.Revision.Links >= 2);
        using (var unused = StorageFile.Open(unusedStored, metadataOnly: true)) Assert.Equal(1u, unused.Revision.Links);

        string installedPackage = Path.Join(project, "node_modules", liveName);
        string[] protectedFiles = [liveArchive, unusedArchive, Path.Join(project, "package.json"), Path.Join(project, "pnpm-lock.yaml"),
            Path.Join(project, "source.txt"), Path.Join(installedPackage, "package.json"), Path.Join(installedPackage, "index.js")];
        var preserved = protectedFiles.ToDictionary(path => path, Hash, StringComparer.OrdinalIgnoreCase);
        string metadata = fixture.OwnedDirectory(Path.Join(Path.GetFileName(fixture.MetadataCache), "metadata-owned-fixture"));
        File.WriteAllText(Path.Join(metadata, "record.json"), "{}");

        var observed = (await fixture.Service.InspectAsync()).Single(row => row.Kind == ToolCacheKind.Pnpm);
        Assert.True(observed.CanPrepare, observed.Details);
        Assert.Equal(reportedStore, observed.Root);
        Assert.Equal(fixture.MetadataCache, observed.CacheRoot);
        var plan = await fixture.Service.PrepareAsync(observed);
        Assert.Contains("prune", plan.CommandPreview, StringComparison.Ordinal);
        Assert.Equal(2, plan.Scopes.Count);
        Assert.Contains(reportedStore, plan.AffectedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(fixture.MetadataCache, plan.AffectedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(fixture.MetadataCache, plan.ConfirmationScopes, StringComparison.OrdinalIgnoreCase);
        var result = await fixture.Service.ExecuteAsync(plan);
        Assert.True(result.Completed, result.Error + result.Output);
        Assert.False(result.Cancelled);
        Assert.False(File.Exists(unusedStored));
        Assert.True(File.Exists(liveStored));
        Assert.False(Directory.Exists(metadata));
        Assert.All(preserved, pair => Assert.Equal(pair.Value, Hash(pair.Key)));
        await fixture.PnpmAsync(["--ignore-workspace", "--config.ignore-pnpmfile=true", "store", "status"]);
        Assert.All(fixture.Commands, command =>
        {
            Assert.Equal(fixture.Cache, command.Environment["NPM_CONFIG_STORE_DIR"]);
            Assert.Equal(fixture.MetadataCache, command.Environment["NPM_CONFIG_CACHE_DIR"]);
            Assert.Equal("true", command.Environment["PNPM_CONFIG_IGNORE_SCRIPTS"]);
            Assert.Equal("true", command.Environment["PNPM_CONFIG_OFFLINE"]);
            Assert.DoesNotContain(command.Arguments, value => value is "run" or "exec" or "dlx" or "rebuild");
        });
        var maintenance = Assert.Single(fixture.Commands, command => command.Arguments.Contains("prune"));
        Assert.Equal(fixture.Working, maintenance.WorkingDirectory);
        Assert.Contains("--config.cache-dir=" + fixture.MetadataCache, maintenance.Arguments);
        Assert.True(fixture.Commands.Count(command => command.Arguments.Contains("status")) >= 3);
    }

    private static void CreatePackage(string path, string name = "space-maid-owned-cache-fixture",
        string payload = "module.exports = 'fixture content only';\n", bool lifecycleScript = true)
    {
        using var output = File.Create(path);
        using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        // The sentinel script is deliberately not executable project code. Any
        // accidental lifecycle execution would fail this offline cache-only test.
        Add("package/package.json", "{\"name\":\"" + name + "\",\"version\":\"1.0.0\""
            + (lifecycleScript ? ",\"scripts\":{\"preinstall\":\"SPACEMAID_MUST_NOT_EXECUTE_PACKAGE_SCRIPTS\"}" : "") + "}");
        Add("package/index.js", payload);
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

    private static string Hash(string path) => Hash(File.ReadAllBytes(path));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

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
    private static ToolCacheLauncher? FindSupportedPnpm()
    {
        string? configured = Environment.GetEnvironmentVariable("SPACEMAID_TEST_PNPM");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathFullyQualified(configured) && Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(configured) ? new(configured, []) : null;
        var launcher = ToolCacheCommandRunner.Resolve(ToolCacheKind.Pnpm);
        return PackageMajor(launcher) is >= 9 and <= 10 ? launcher : null;
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
    private sealed class LocalNativePnpmFact : FactAttribute
    {
        public LocalNativePnpmFact()
        {
            if (FindSupportedPnpm() is null && Environment.GetEnvironmentVariable("SPACEMAID_REQUIRE_PNPM_PRUNE_TEST") != "1")
                Skip = "Requires an existing supported pnpm 9–10 CLI or SPACEMAID_TEST_PNPM pointing to a direct executable; no tool installation is performed by this test.";
        }
    }

    private sealed class NativeCacheFixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        private readonly string _userConfig, _globalConfig, _prefix;
        public string Root => _directory.Path;
        public string Cache { get; }
        public string MetadataCache { get; }
        public string Working { get; }
        public ToolCacheLauncher Launcher { get; }
        public List<ToolCacheCommand> Commands { get; } = [];
        public ToolCacheService Service { get; }
        internal NativeCacheFixture(ToolCacheLauncher launcher, ToolCacheKind kind = ToolCacheKind.Npm)
        {
            Launcher = launcher;
            Cache = Directory.CreateDirectory(Path.Join(Root, "cache")).FullName;
            MetadataCache = Directory.CreateDirectory(Path.Join(Root, "metadata-cache-žluťoučký")).FullName;
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
                ["NODE_OPTIONS"] = "", ["NODE_PATH"] = "", ["NODE_DISABLE_COMPILE_CACHE"] = "1", ["NPM_CONFIG_CACHE"] = Cache, ["NPM_CONFIG_USERCONFIG"] = _userConfig,
                ["NPM_CONFIG_GLOBALCONFIG"] = _globalConfig, ["NPM_CONFIG_PREFIX"] = _prefix,
                ["NPM_CONFIG_OFFLINE"] = "true", ["NPM_CONFIG_IGNORE_SCRIPTS"] = "true", ["NPM_CONFIG_UPDATE_NOTIFIER"] = "false",
                ["NPM_CONFIG_AUDIT"] = "false", ["NPM_CONFIG_FUND"] = "false", ["NPM_CONFIG_STORE_DIR"] = Cache,
                ["NPM_CONFIG_CACHE_DIR"] = MetadataCache, ["PNPM_CONFIG_CACHE_DIR"] = MetadataCache, ["PNPM_CONFIG_STORE_DIR"] = Cache,
                ["PNPM_CONFIG_CONFIG_DIR"] = OwnedDirectory("config"), ["PNPM_CONFIG_STATE_DIR"] = OwnedDirectory("state"),
                ["PNPM_HOME"] = OwnedDirectory("pnpm-home"), ["PNPM_CONFIG_GLOBAL_DIR"] = OwnedDirectory("global"),
                ["NPM_CONFIG_GLOBAL_DIR"] = OwnedDirectory("global"), ["PNPM_CONFIG_USERCONFIG"] = _userConfig,
                ["PNPM_CONFIG_OFFLINE"] = "true", ["PNPM_CONFIG_IGNORE_SCRIPTS"] = "true", ["PNPM_CONFIG_UPDATE_NOTIFIER"] = "false",
                ["PNPM_CONFIG_PM_ON_FAIL"] = "error", ["PNPM_CONFIG_RUNTIME_ON_FAIL"] = "error",
                ["NPM_CONFIG_REGISTRY"] = "http://127.0.0.1:9/", ["PNPM_CONFIG_REGISTRY"] = "http://127.0.0.1:9/",
                ["HOME"] = OwnedDirectory("profile"), ["USERPROFILE"] = OwnedDirectory("profile"),
                ["APPDATA"] = OwnedDirectory(Path.Join("profile", "AppData", "Roaming")), ["LOCALAPPDATA"] = OwnedDirectory(Path.Join("profile", "AppData", "Local")),
                ["XDG_CONFIG_HOME"] = OwnedDirectory("xdg-config"), ["XDG_CACHE_HOME"] = OwnedDirectory("xdg-cache"),
                ["XDG_DATA_HOME"] = OwnedDirectory("xdg-data"), ["XDG_STATE_HOME"] = OwnedDirectory("xdg-state"),
                ["NPM_CONFIG_MANAGE_PACKAGE_MANAGER_VERSIONS"] = "false", ["PNPM_CONFIG_MANAGE_PACKAGE_MANAGER_VERSIONS"] = "false",
                ["PNPM_CONFIG_IGNORE_PNPMFILE"] = "true", ["COREPACK_ENABLE_NETWORK"] = "0", ["COREPACK_ENABLE_PROJECT_SPEC"] = "0",
                ["NO_COLOR"] = "1", ["CI"] = "true"
            };
            var bounded = original with { Environment = environment, WorkingDirectory = Working };
            Commands.Add(bounded);
            return await ToolCacheCommandRunner.RunAsync(bounded, token);
        }
        public async Task<ToolCacheCommandResult> PnpmAsync(IReadOnlyList<string> arguments)
        {
            var result = await RunAsync(new(Launcher.Executable, Launcher.Prefix.Concat(arguments).ToArray(), Working,
                new Dictionary<string, string>(), TimeSpan.FromSeconds(30)), default);
            Assert.True(result.ExitCode == 0, result.Error + result.Output);
            return result;
        }
        public string OwnedDirectory(string relative)
        {
            string path = Path.Join(Root, relative);
            AssertOwned(path);
            return Directory.CreateDirectory(path).FullName;
        }
        public void AssertOwned(string path)
        {
            Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        public void Dispose()
        {
            string root = Path.GetFullPath(Root);
            string expected = Path.GetFullPath(Path.Join(Path.GetTempPath(), "SpaceMaid-tests")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
                throw new InvalidOperationException("Native cache fixture cleanup escaped its owned directory.");
            int remaining = 10_000;
            Remove(root, 0);

            void Remove(string path, int depth)
            {
                string full = Path.GetFullPath(path);
                if (depth > 64 || --remaining < 0
                    || !full.Equals(root, StringComparison.OrdinalIgnoreCase)
                        && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Native cache fixture cleanup exceeded its owned scope or traversal budget.");
                var attributes = File.GetAttributes(full);
                // Windows applies SetFileAttributes to the link itself. Never
                // enumerate a reparse point or recursively delete through it.
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(full, attributes & ~FileAttributes.ReadOnly);
                bool directory = (attributes & FileAttributes.Directory) != 0;
                if (directory && (attributes & FileAttributes.ReparsePoint) == 0)
                    foreach (string child in Directory.EnumerateFileSystemEntries(full)) Remove(child, depth + 1);
                if (directory) Directory.Delete(full, recursive: false);
                else File.Delete(full);
            }
        }
    }
}
