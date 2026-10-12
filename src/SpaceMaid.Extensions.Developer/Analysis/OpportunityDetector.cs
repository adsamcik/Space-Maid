using SpaceMaid.Core.Analysis;
using System.IO;

namespace SpaceMaid.Extensions.Developer.Analysis;

public static class OpportunityDetector
{
    public static IReadOnlyList<Opportunity> Find(IOpportunityInventory scan, CancellationToken token = default)
    {
        var found = new List<Opportunity>();
        var evidence = new MarkerEvidence(scan, token);
        for (int directory = 0; directory < scan.FileCount; directory++)
        {
            if ((directory & 1023) == 0) token.ThrowIfCancellationRequested();
            if (!scan.IsDirectory(directory)) continue;
            int link = scan.PrimaryLink(directory);
            // The selected folder itself can be a cache or project artifact.
            string? rootName = directory == scan.RootIndex ? Path.GetFileName(Path.TrimEndingDirectorySeparator(scan.RootPath)) : null;
            ReadOnlySpan<char> name = rootName is not null ? rootName : link >= 0 ? scan.Name(link) : [];
            int parent = link >= 0 ? scan.LinkParent(link) : -1;
            Rule? rule = Classify(scan, directory, parent, name, evidence);
            if (rule is null) continue;
            string? path = scan.PathForFile(directory);
            if (path is null) continue;
            found.Add(new(directory, rule.Title, rule.Category, path,
                scan.LogicalSize(directory), scan.AllocatedSize(directory),
                rule.Reason, rule.Caution, rule.Action, rule.Manual)
            {
                RuleId = rule.Title,
                Appearance = rule.Category switch
                {
                    "Agent files" => OpportunityAppearance.Agent,
                    "Git worktrees" => OpportunityAppearance.Project,
                    "Test artifacts" or "Editor workspace data" => OpportunityAppearance.Other,
                    _ => OpportunityAppearance.Cache
                },
                IsWorktree = rule.Category == "Git worktrees"
            });
        }
        return found.OrderByDescending(o => o.DisplayBytes(scan.HasAllocationData))
            .ThenBy(o => o.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private sealed record Rule(string Title, string Category, string Reason, string Caution, string Action, bool Manual = false);

    private static Rule? Classify(IOpportunityInventory scan, int directory, int parent, ReadOnlySpan<char> name, MarkerEvidence evidence)
    {
        if ((Is(name, "bin") || Is(name, "obj")) && evidence.Has(parent, Markers.DotNetProject))
            return new(".NET build output", "Build output", "A bin/obj directory beside a .NET project file.",
                "Build outputs may include files copied by hand. Review them and stop running builds first.", "Rebuild with dotnet build after cleanup.");
        if (Is(name, "node_modules") && evidence.Has(parent, Markers.PackageJson))
            return new("Node dependencies", "Dependencies", "Installed dependencies beside package.json.",
                "Restoring may need network access. Local edits inside dependencies would be lost.", "Restore with the project's npm, pnpm, or Yarn install command.");
        if ((Is(name, ".next") || Is(name, "dist") || Is(name, "build")) && evidence.Has(parent, Markers.PackageJson))
            return new("Possible Node build output", "Build output", "A common build directory beside package.json.",
                "The project may keep authored files here. The directory name does not prove every file is generated.", "Check the project's build configuration before removing anything.", true);
        var direct = evidence.DirectFiles(directory);
        if ((direct & Markers.PythonEnvironment) != 0)
            return new("Python virtual environment", "Python environments", "The directory contains pyvenv.cfg.",
                "Recreation needs the correct interpreter and dependency list. Locally installed or edited packages may be unique.", "Record the environment requirements before recreating it.", true);
        if (Is(name, "__pycache__") && evidence.Has(directory, Markers.PythonBytecode))
            return new("Python bytecode cache", "Build output", "A __pycache__ directory containing compiled Python files.",
                "Review unexpected files and stop active Python processes before cleanup.", "Python normally regenerates bytecode when code runs.");
        if (Is(name, "packages") && ParentNamed(scan, parent, ".nuget"))
            return new("NuGet package cache", "Package caches", "The standard .nuget/packages directory.",
                "Offline builds may need these packages. Custom tool configuration can use other locations.", "Review the cache with dotnet nuget locals global-packages --list.");
        if (Is(name, "caches") && ParentNamed(scan, parent, ".gradle"))
            return new("Gradle caches", "Package caches", "The standard .gradle/caches directory.",
                "Android and Java builds may need downloads and recompilation. Stop Gradle before any future cleanup.", "Review Gradle caches and stop its daemons first.");
        if (Is(name, "npm-cache") && ParentNamed(scan, parent, "Local") && AncestorNamed(scan, parent, "AppData"))
            return new("npm cache", "Package caches", "npm's usual Windows cache directory under AppData/Local.",
                "Cached packages may be useful for offline installs.", "Use npm cache verify to review the cache.");
        if (Is(name, "Cache") && ParentNamed(scan, parent, "pip") && AncestorNamed(scan, parent, "AppData"))
            return new("pip cache", "Package caches", "pip's usual Windows cache directory.",
                "Future installs may need to download or rebuild cached wheels.", "Review it with python -m pip cache info.");
        if ((Is(name, "log") || Is(name, "logs") || Is(name, "sessions") || Is(name, "archived_sessions")) && ParentNamed(scan, parent, ".codex"))
            return new(Is(name, "log") || Is(name, "logs") ? "Codex logs" : "Codex session history", "Agent files",
                "A known Codex log or session-history directory.",
                "Session files can preserve useful conversations and recovery context. Age is not proof that they are disposable.",
                "Review in Codex and preserve any history you need.", true);
        if (Is(name, "debug") && ParentNamed(scan, parent, ".claude"))
            return new("Claude Code debug files", "Agent files", "The .claude/debug directory.",
                "Debug files may be useful for investigating a current issue.", "Review the files and close active agent sessions before cleanup.", true);
        if ((direct & Markers.GitPointer) != 0)
            return new("Linked Git checkout", "Git worktrees", "The checkout contains a .git pointer file; it may be a worktree or submodule.",
                "It may contain source changes or untracked work. No disposable/abandoned status is inferred.",
                "Review with git status and git worktree list; remove through Git or the owning app after preserving work.", true);
        return Additional(scan, directory, parent, name, direct, evidence);
    }

    private static Rule? Additional(IOpportunityInventory scan, int directory, int parent, ReadOnlySpan<char> name,
        Markers direct, MarkerEvidence evidence)
    {
        if (Is(name, "target"))
        {
            if (evidence.Has(parent, Markers.CargoManifest) && (evidence.Has(directory, Markers.RustInfo)
                || evidence.HasNested(directory, "debug", Markers.Fingerprint) || evidence.HasNested(directory, "release", Markers.Fingerprint)))
                return new("Cargo build output", "Build output", "A target folder beside Cargo.toml with Rust compiler or fingerprint metadata.",
                    "Stop Cargo builds first. Review files copied here by hand; rebuilding can take time.", "Cargo rebuilds missing output when you build or test again.");
            if (evidence.Has(parent, Markers.MavenManifest) && evidence.Any(directory, Markers.Classes | Markers.MavenStatus))
                return new("Maven build output", "Build output", "A target folder beside pom.xml with classes or maven-status build directories.",
                    "Build output may contain copied files or customized assets. Stop Maven and review them first.", "Rebuild with the project's Maven build command.", true);
        }
        if (Is(name, "build") && evidence.Has(parent, Markers.GradleBuild) && evidence.Any(directory,
            Markers.Outputs | Markers.Intermediates | Markers.Classes | Markers.Generated))
            return new("Gradle build output", "Build output", "A build folder beside a Gradle build file with generated output directories.",
                "Stop Gradle before cleanup. Generated directories may also contain customized or copied files.", "Run the project's Gradle build again to regenerate output.", true);
        if ((direct & (Markers.CMakeCache | Markers.CMakeSource)) == Markers.CMakeCache
            && evidence.Has(directory, Markers.CMakeFiles) && evidence.Has(parent, Markers.CMakeSource))
            return new("CMake build output", "Build output", "An out-of-source folder with CMakeCache.txt and CMakeFiles beside CMakeLists.txt; no CMakeLists.txt is inside this folder.",
                "The folder may contain hand-added files and local configure settings. Preserve them and stop builds first.", "Configure and build in a new output folder after reviewing the project's CMake options.", true);
        if (Is(name, ".svelte-kit") && evidence.Has(parent, Markers.PackageJson | Markers.SvelteConfig))
            return new("SvelteKit build output", "Build output", "A .svelte-kit folder beside package.json and a Svelte configuration file.",
                "Stop the development server and review any customized or copied files before cleanup.", "Run the project's SvelteKit development or build command to regenerate output.", true);
        if (Is(name, ".nuxt") && evidence.Has(parent, Markers.PackageJson | Markers.NuxtConfig))
            return new("Nuxt build output", "Build output", "A .nuxt folder beside package.json and a Nuxt configuration file.",
                "Stop Nuxt and review any customized or copied files before cleanup.", "Run the project's Nuxt development or build command to regenerate output.", true);
        if (Is(name, ".gradle") && evidence.Any(parent, Markers.GradleBuild | Markers.GradleSettings))
            return new("Gradle project cache", "Tool caches", "A project .gradle folder beside a Gradle build or settings file.",
                "Stop Gradle first. This includes local task history and caches; customized files may also be present.", "Gradle recreates project caches, which can make the next build slower.");
        if (Is(name, ".pytest_cache") && evidence.Has(directory, Markers.CacheTag | Markers.PytestState))
            return new("pytest cache", "Tool caches", "A .pytest_cache folder with CACHEDIR.TAG and the pytest v state directory.",
                "This can include last-failed test information and other useful state. Review it and stop tests first.", "pytest recreates caches; last-failed state is reset.", true);
        if (Is(name, ".mypy_cache") && evidence.Has(directory, Markers.CacheTag))
            return new("mypy cache", "Tool caches", "A .mypy_cache folder containing CACHEDIR.TAG.",
                "Review unexpected files and stop type checks first. Removing the cache slows the next check.", "mypy recreates its incremental type-check cache.");
        if (Is(name, ".ruff_cache") && evidence.Has(directory, Markers.CacheTag))
            return new("Ruff cache", "Tool caches", "A .ruff_cache folder containing CACHEDIR.TAG.",
                "Review unexpected files and stop Ruff first. The next lint or format run may take longer.", "Ruff recreates its cache on subsequent runs.");
        if (Is(name, ".turbo") && evidence.Has(parent, Markers.PackageJson | Markers.TurboConfig) && evidence.Has(directory, Markers.CacheDirectory))
            return new("Turborepo cache", "Tool caches", "A .turbo folder beside package.json and turbo.json with a cache directory.",
                "Stop tasks first. Local cached artifacts can support offline work; review custom files here.", "Turborepo rebuilds or downloads missing task artifacts.");
        if (Is(name, ".parcel-cache") && evidence.Has(parent, Markers.PackageJson))
            return new("Parcel cache", "Tool caches", "A .parcel-cache folder beside package.json.",
                "Stop Parcel and review unexpected files first. The next build may take longer.", "Parcel recreates its build cache.");
        if (Is(name, ".angular") && evidence.Has(parent, Markers.PackageJson | Markers.AngularConfig) && evidence.Has(directory, Markers.CacheDirectory))
            return new("Angular workspace cache", "Tool caches", "A .angular folder beside angular.json and package.json with a cache directory.",
                "Review the whole .angular folder for custom state and stop Angular tasks first.", "Angular recreates build caches; the next build can take longer.", true);
        if (Is(name, ".vite") && ParentNamed(scan, parent, "node_modules") && evidence.HasNested(directory, "deps", Markers.ViteMetadata))
            return new("Vite dependency cache", "Tool caches", "A node_modules/.vite folder with deps/_metadata.json optimization metadata.",
                "Stop the development server and review unexpected files here. Dependencies are optimized again after cleanup.", "Vite regenerates its dependency optimization cache.");
        if (Is(name, "cache") && ParentNamed(scan, parent, "registry") && ParentNamed(scan, Parent(scan, parent), ".cargo")
            && evidence.HasCargoArchives(directory))
            return new("Cargo registry archive cache", "Package caches", "The .cargo/registry/cache download folder containing .crate archives.",
                "Review custom files and preserve archives needed for offline builds. Cargo source, Git storage and configuration are outside this folder.", "Cargo downloads missing registry archives again when required.");
        if (Is(name, "cache") && ParentNamed(scan, parent, "uv") && LocalAppData(scan, Parent(scan, parent)) && evidence.Has(directory, Markers.CacheTag))
            return new("uv cache", "Package caches", "An AppData/Local/uv/cache folder containing CACHEDIR.TAG.",
                "Cached downloads and wheels may be needed offline. Stop uv and review custom files first.", "uv downloads or rebuilds missing cached packages when needed.");
        if (Is(name, "TestResults") && evidence.Has(directory, Markers.TestResult))
            return new(".NET test results", "Test artifacts", "A TestResults folder containing .trx test-result files.",
                "Test results, attachments and diagnostic evidence may be useful. Preserve reports you need and stop tests first.", "Future test runs create new reports; old evidence is not recreated.", true);
        if ((Is(name, "test-results") || Is(name, "playwright-report")) && evidence.Has(parent, Markers.PackageJson | Markers.PlaywrightConfig))
        {
            if (Is(name, "playwright-report") && evidence.Has(directory, Markers.HtmlIndex))
                return new("Playwright HTML report", "Test artifacts", "A playwright-report folder with index.html beside package.json and a Playwright configuration file.",
                    "Reports can contain useful test history, screenshots and traces. Preserve evidence and stop tests first.", "A new test run creates a new report; old test evidence is not recreated.", true);
            if (Is(name, "test-results") && evidence.Has(directory, Markers.LastRun))
                return new("Playwright test results", "Test artifacts", "A test-results folder with .last-run.json beside package.json and a Playwright configuration file.",
                    "Failures, screenshots, videos and traces may be unique diagnostic evidence. Preserve them and stop tests first.", "Future tests create new artifacts; old evidence is not recreated.", true);
        }
        if (Is(name, ".vs") && evidence.Has(parent, Markers.Solution))
            return new("Visual Studio workspace data", "Editor workspace data", "A .vs folder beside a Visual Studio solution file.",
                "This includes personal IDE state and local settings, not only cache. Close Visual Studio and preserve useful state.", "Visual Studio recreates some workspace data; personal state may be lost.", true);
        if (Is(name, "dists") && ParentNamed(scan, parent, "wrapper") && ParentNamed(scan, Parent(scan, parent), ".gradle"))
            return new("Gradle wrapper downloads", "Downloaded tooling", "The .gradle/wrapper/dists distribution download folder.",
                "Stop Gradle first. Offline builds can need these distributions; custom files should be reviewed.", "The Gradle wrapper downloads required distributions again.");
        if (Is(name, "ms-playwright") && LocalAppData(scan, parent) && evidence.Has(directory, Markers.BrowserVersion))
            return new("Playwright browser downloads", "Downloaded tooling", "An AppData/Local/ms-playwright folder with versioned Playwright browser directories.",
                "Stop Playwright first. Tests need these browser downloads; offline reinstalls may not be possible.", "Run the project's Playwright install command to download browsers again.");
        return null;
    }

    private static bool Is(ReadOnlySpan<char> value, string expected) => value.Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static int Parent(IOpportunityInventory scan, int directory) => directory < 0 || directory == scan.RootIndex || scan.PrimaryLink(directory) < 0
        ? -1 : scan.LinkParent(scan.PrimaryLink(directory));
    private static bool LocalAppData(IOpportunityInventory scan, int directory) => ParentNamed(scan, directory, "Local")
        && ParentNamed(scan, Parent(scan, directory), "AppData");

    [Flags]
    private enum Markers : ulong
    {
        DotNetProject = 1UL << 0, PackageJson = 1UL << 1, PythonEnvironment = 1UL << 2, GitPointer = 1UL << 3,
        PythonBytecode = 1UL << 4, CargoManifest = 1UL << 5, MavenManifest = 1UL << 6, GradleBuild = 1UL << 7,
        GradleSettings = 1UL << 8, CMakeSource = 1UL << 9, CMakeCache = 1UL << 10, SvelteConfig = 1UL << 11,
        NuxtConfig = 1UL << 12, TurboConfig = 1UL << 13, AngularConfig = 1UL << 14, PlaywrightConfig = 1UL << 15,
        Solution = 1UL << 16, RustInfo = 1UL << 17, CacheTag = 1UL << 18, TestResult = 1UL << 19,
        HtmlIndex = 1UL << 20, LastRun = 1UL << 21, ViteMetadata = 1UL << 22, CrateArchive = 1UL << 23,
        Fingerprint = 1UL << 24, Classes = 1UL << 25, MavenStatus = 1UL << 26, Outputs = 1UL << 27,
        Intermediates = 1UL << 28, Generated = 1UL << 29, CMakeFiles = 1UL << 30, PytestState = 1UL << 31,
        CacheDirectory = 1UL << 32, BrowserVersion = 1UL << 33
    }

    // One compact record per actual candidate or its immediate parent. General
    // directories only get an uncached scan for the existing venv/Git rules and
    // arbitrary-named CMake output. No child-name dictionaries or disk reads.
    private sealed class MarkerEvidence(IOpportunityInventory scan, CancellationToken token)
    {
        private readonly Dictionary<int, CandidateMarkers> _candidates = [];
        private readonly record struct CandidateMarkers(Markers Flags, int Debug = -1, int Release = -1, int Deps = -1);
        public bool Has(int directory, Markers flags) => (Get(directory).Flags & flags) == flags;
        public bool Any(int directory, Markers flags) => (Get(directory).Flags & flags) != 0;
        public bool HasNested(int directory, string child, Markers flags)
        {
            var metadata = Get(directory);
            int index = child switch { "debug" => metadata.Debug, "release" => metadata.Release, "deps" => metadata.Deps, _ => -1 };
            return index >= 0 && Has(index, flags);
        }
        public bool HasCargoArchives(int directory)
        {
            if (Has(directory, Markers.CrateArchive)) return true;
            int count = 0;
            foreach (int link in scan.Children(directory))
            {
                if ((count++ & 1023) == 0) token.ThrowIfCancellationRequested();
                int child = scan.LinkObject(link);
                if (scan.IsDirectory(child) && Has(child, Markers.CrateArchive)) return true;
            }
            return false;
        }
        public Markers DirectFiles(int directory)
        {
            if (_candidates.TryGetValue(directory, out var cached)) return cached.Flags;
            Markers flags = 0; int count = 0;
            foreach (int link in scan.Children(directory))
            {
                if ((count++ & 1023) == 0) token.ThrowIfCancellationRequested();
                if (scan.IsDirectory(scan.LinkObject(link))) continue;
                var name = scan.Name(link);
                if (Is(name, "pyvenv.cfg")) flags |= Markers.PythonEnvironment;
                if (Is(name, ".git")) flags |= Markers.GitPointer;
                if (Is(name, "CMakeCache.txt")) flags |= Markers.CMakeCache;
                if (Is(name, "CMakeLists.txt")) flags |= Markers.CMakeSource;
            }
            return flags;
        }
        private CandidateMarkers Get(int directory)
        {
            if (directory < 0) return new(0);
            if (_candidates.TryGetValue(directory, out var cached)) return cached;
            Markers flags = 0; int debug = -1, release = -1, deps = -1, count = 0;
            foreach (int link in scan.Children(directory))
            {
                if ((count++ & 1023) == 0) token.ThrowIfCancellationRequested();
                int file = scan.LinkObject(link); var name = scan.Name(link);
                if (scan.IsDirectory(file))
                {
                    if (Is(name, "debug")) debug = file;
                    if (Is(name, "release")) release = file;
                    if (Is(name, "deps")) deps = file;
                    if (Is(name, ".fingerprint")) flags |= Markers.Fingerprint;
                    if (Is(name, "classes")) flags |= Markers.Classes;
                    if (Is(name, "maven-status")) flags |= Markers.MavenStatus;
                    if (Is(name, "outputs")) flags |= Markers.Outputs;
                    if (Is(name, "intermediates")) flags |= Markers.Intermediates;
                    if (Is(name, "generated")) flags |= Markers.Generated;
                    if (Is(name, "CMakeFiles")) flags |= Markers.CMakeFiles;
                    if (Is(name, "v")) flags |= Markers.PytestState;
                    if (Is(name, "cache")) flags |= Markers.CacheDirectory;
                    if (BrowserName(name)) flags |= Markers.BrowserVersion;
                    continue;
                }
                if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)) flags |= Markers.DotNetProject;
                if (Is(name, "package.json")) flags |= Markers.PackageJson;
                if (Is(name, "pyvenv.cfg")) flags |= Markers.PythonEnvironment;
                if (Is(name, ".git")) flags |= Markers.GitPointer;
                if (name.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)) flags |= Markers.PythonBytecode;
                if (Is(name, "Cargo.toml")) flags |= Markers.CargoManifest;
                if (Is(name, "pom.xml")) flags |= Markers.MavenManifest;
                if (Is(name, "build.gradle") || Is(name, "build.gradle.kts")) flags |= Markers.GradleBuild;
                if (Is(name, "settings.gradle") || Is(name, "settings.gradle.kts")) flags |= Markers.GradleSettings;
                if (Is(name, "CMakeLists.txt")) flags |= Markers.CMakeSource;
                if (Is(name, "CMakeCache.txt")) flags |= Markers.CMakeCache;
                if (Is(name, "svelte.config.js") || Is(name, "svelte.config.ts")) flags |= Markers.SvelteConfig;
                if (Is(name, "nuxt.config.ts") || Is(name, "nuxt.config.js") || Is(name, "nuxt.config.mjs")) flags |= Markers.NuxtConfig;
                if (Is(name, "turbo.json")) flags |= Markers.TurboConfig;
                if (Is(name, "angular.json")) flags |= Markers.AngularConfig;
                if (Is(name, "playwright.config.ts") || Is(name, "playwright.config.js") || Is(name, "playwright.config.mjs")
                    || Is(name, "playwright.config.cjs") || Is(name, "playwright.config.mts") || Is(name, "playwright.config.cts")) flags |= Markers.PlaywrightConfig;
                if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) flags |= Markers.Solution;
                if (Is(name, ".rustc_info.json")) flags |= Markers.RustInfo;
                if (Is(name, "CACHEDIR.TAG")) flags |= Markers.CacheTag;
                if (name.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)) flags |= Markers.TestResult;
                if (Is(name, "index.html")) flags |= Markers.HtmlIndex;
                if (Is(name, ".last-run.json")) flags |= Markers.LastRun;
                if (Is(name, "_metadata.json")) flags |= Markers.ViteMetadata;
                if (name.EndsWith(".crate", StringComparison.OrdinalIgnoreCase)) flags |= Markers.CrateArchive;
            }
            var result = new CandidateMarkers(flags, debug, release, deps); _candidates.Add(directory, result); return result;
        }
        private static bool BrowserName(ReadOnlySpan<char> name)
        {
            foreach (string prefix in BrowserPrefixes)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length <= prefix.Length) continue;
                bool revision = true;
                foreach (char character in name[prefix.Length..]) if (!char.IsAsciiDigit(character)) { revision = false; break; }
                if (revision) return true;
            }
            return false;
        }
        private static readonly string[] BrowserPrefixes = ["chromium-", "chromium_headless_shell-", "firefox-", "webkit-"];
    }
    private static bool ParentNamed(IOpportunityInventory scan, int parent, string name) => parent >= 0 &&
        (parent == scan.RootIndex ? Is(Path.GetFileName(Path.TrimEndingDirectorySeparator(scan.RootPath)), name)
            : scan.PrimaryLink(parent) >= 0 && Is(scan.Name(scan.PrimaryLink(parent)), name));
    private static bool AncestorNamed(IOpportunityInventory scan, int directory, string name)
    {
        int remaining = scan.FileCount;
        while (directory >= 0 && remaining-- > 0)
        {
            if (ParentNamed(scan, directory, name)) return true;
            if (directory == scan.RootIndex || scan.PrimaryLink(directory) < 0) break;
            directory = scan.LinkParent(scan.PrimaryLink(directory));
        }
        return false;
    }
}
