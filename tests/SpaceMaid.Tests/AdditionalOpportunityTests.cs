using System.IO;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class AdditionalOpportunityTests
{
    public static IEnumerable<object[]> Patterns()
    {
        yield return Case("Cargo build output", "Build output", "rust/target", ["rust/Cargo.toml", "rust/target/.rustc_info.json"], [], "rust/Cargo.toml");
        yield return Case("Cargo build output", "Build output", "rust/target", ["rust/Cargo.toml"], ["rust/target/debug/.fingerprint"], "rust/target/debug/.fingerprint");
        yield return Case("Maven build output", "Build output", "java/target", ["java/pom.xml"], ["java/target/maven-status"], "java/target/maven-status", true);
        yield return Case("Gradle build output", "Build output", "android/build", ["android/build.gradle.kts"], ["android/build/intermediates"], "android/build.gradle.kts", true);
        yield return Case("CMake build output", "Build output", "native/out", ["native/CMakeLists.txt", "native/out/CMakeCache.txt"], ["native/out/CMakeFiles"], "native/out/CMakeCache.txt", true);
        yield return Case("SvelteKit build output", "Build output", "web/.svelte-kit", ["web/package.json", "web/svelte.config.js"], [], "web/svelte.config.js", true);
        yield return Case("Nuxt build output", "Build output", "web/.nuxt", ["web/package.json", "web/nuxt.config.ts"], [], "web/nuxt.config.ts", true);
        yield return Case("Gradle project cache", "Tool caches", "android/.gradle", ["android/settings.gradle"], [], "android/settings.gradle");
        yield return Case("pytest cache", "Tool caches", "python/.pytest_cache", ["python/.pytest_cache/CACHEDIR.TAG"], ["python/.pytest_cache/v"], "python/.pytest_cache/v", true);
        yield return Case("mypy cache", "Tool caches", "python/.mypy_cache", ["python/.mypy_cache/CACHEDIR.TAG"], [], "python/.mypy_cache/CACHEDIR.TAG");
        yield return Case("Ruff cache", "Tool caches", "python/.ruff_cache", ["python/.ruff_cache/CACHEDIR.TAG"], [], "python/.ruff_cache/CACHEDIR.TAG");
        yield return Case("Turborepo cache", "Tool caches", "web/.turbo", ["web/package.json", "web/turbo.json"], ["web/.turbo/cache"], "web/turbo.json");
        yield return Case("Parcel cache", "Tool caches", "web/.parcel-cache", ["web/package.json"], [], "web/package.json");
        yield return Case("Angular workspace cache", "Tool caches", "web/.angular", ["web/package.json", "web/angular.json"], ["web/.angular/cache"], "web/angular.json", true);
        yield return Case("Vite dependency cache", "Tool caches", "web/node_modules/.vite", ["web/node_modules/.vite/deps/_metadata.json"], [], "web/node_modules/.vite/deps/_metadata.json");
        yield return Case("Cargo registry archive cache", "Package caches", ".cargo/registry/cache", [".cargo/registry/cache/index/pkg-1.0.crate"], [], ".cargo/registry/cache/index/pkg-1.0.crate");
        yield return Case("uv cache", "Package caches", "AppData/Local/uv/cache", ["AppData/Local/uv/cache/CACHEDIR.TAG"], [], "AppData/Local/uv/cache/CACHEDIR.TAG");
        yield return Case(".NET test results", "Test artifacts", "app/TestResults", ["app/TestResults/results.trx"], [], "app/TestResults/results.trx", true);
        yield return Case("Playwright test results", "Test artifacts", "web/test-results", ["web/package.json", "web/playwright.config.mts", "web/test-results/.last-run.json"], [], "web/test-results/.last-run.json", true);
        yield return Case("Playwright HTML report", "Test artifacts", "web/playwright-report", ["web/package.json", "web/playwright.config.cjs", "web/playwright-report/index.html"], [], "web/playwright-report/index.html", true);
        yield return Case("Visual Studio workspace data", "Editor workspace data", "app/.vs", ["app/app.slnx"], [], "app/app.slnx", true);
        yield return Case("Gradle wrapper downloads", "Downloaded tooling", ".gradle/wrapper/dists", [], [], "rename:.gradle/wrapper/dists");
        yield return Case("Playwright browser downloads", "Downloaded tooling", "AppData/Local/ms-playwright", [], ["AppData/Local/ms-playwright/chromium_headless_shell-1234"], "AppData/Local/ms-playwright/chromium_headless_shell-1234");

        static object[] Case(string title, string category, string path, string[] files, string[] folders, string removed, bool manual = false)
            => [title, category, path, files, folders, removed, manual];
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void CorroboratedPatternsAreDetectedAndMissingEvidenceIsNot(string title, string category, string path,
        string[] files, string[] folders, string removed, bool manual)
    {
        var positive = Inventory(path, files, folders);
        var finding = Assert.Single(OpportunityDetector.Find(positive), value => value.Path == Full(path));
        Assert.Equal(title, finding.Title); Assert.Equal(category, finding.Category); Assert.Equal(manual, finding.RequiresManualReview);
        Assert.NotEmpty(finding.Reason); Assert.NotEmpty(finding.Caution); Assert.NotEmpty(finding.SuggestedAction);
        string negativePath = removed.StartsWith("rename:", StringComparison.Ordinal) ? path.Replace(".gradle", "ordinary", StringComparison.Ordinal) : path;
        var negative = Inventory(negativePath, files.Where(file => file != removed), folders.Where(folder => folder != removed));
        Assert.DoesNotContain(OpportunityDetector.Find(negative), value => value.Path == Full(negativePath));
    }

    [Fact]
    public void SourceFoldersInstalledToolingWrongPathsAndAncestorOnlyConfigAreExcluded()
    {
        string[] folders = ["rust/target", "java/target", "android/build", "native/output/CMakeFiles", "native/in-source/CMakeFiles",
            ".gradle", ".idea", ".vscode", ".cargo/registry/src", ".cargo/git", ".rustup/toolchains/stable", "cache",
            "web/deep/.angular/cache", "uv/cache", "AppData/Local/ms-playwright/profile", "AppData/Local/ms-playwright/chromium-123-personal",
            "web/deep/.nuxt", "web/test-results"];
        string[] files = ["rust/Cargo.toml", "java/pom.xml", "android/build.gradle", "native/CMakeLists.txt",
            "native/in-source/CMakeCache.txt", "native/in-source/CMakeLists.txt", "native/output/CMakeCache.txt",
            "web/package.json", "web/angular.json", "web/nuxt.config.ts", "web/playwright.config.ts", "web/deep/.angular/cache/data",
            "uv/cache/CACHEDIR.TAG", "cache/pkg.crate", ".cargo/config.toml", "auth.json"];
        // One valid out-of-source output remains; neither configuration alone
        // nor a broadly named target/build/cache/profile establishes a finding.
        var scan = Inventory("", files, folders);
        var found = OpportunityDetector.Find(scan);
        Assert.Equal("CMake build output", Assert.Single(found).Title);
        Assert.Equal(Full("native/output"), found[0].Path);
        Assert.Throws<OperationCanceledException>(() => OpportunityDetector.Find(scan, new(true)));
        var mixedNodeGradle = Inventory("mixed/build", ["mixed/package.json", "mixed/build.gradle"], ["mixed/build/outputs"]);
        Assert.Equal("Possible Node build output", Assert.Single(OpportunityDetector.Find(mixedNodeGradle)).Title);
    }

    [Fact]
    public async Task SelectedOutputAndToolCacheAreExcludedWhenFreshChecksLoseTheirRequiredMarkers()
    {
        using var fixture = new TestDirectory();
        string project = Directory.CreateDirectory(Path.Join(fixture.Path, "rust")).FullName;
        string target = Directory.CreateDirectory(Path.Join(project, "target")).FullName;
        string cache = Directory.CreateDirectory(Path.Join(fixture.Path, ".ruff_cache")).FullName;
        string manifest = Path.Join(project, "Cargo.toml"), tag = Path.Join(cache, "CACHEDIR.TAG");
        string output = Path.Join(target, "keep-output.bin"), source = Path.Join(project, "main.rs");
        File.WriteAllText(manifest, "owned fixture"); File.WriteAllText(Path.Join(target, ".rustc_info.json"), "owned fixture");
        File.WriteAllText(tag, "owned fixture"); File.WriteAllText(output, "preserve output"); File.WriteAllText(source, "preserve source");
        var original = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var selected = OpportunityDetector.Find(original);
        Assert.Equal(2, selected.Count);
        File.Delete(manifest); File.Delete(tag); // Only owned fixture markers.
        var validated = await OpportunityReviewValidator.ValidateAsync(original, selected);
        Assert.Empty(validated.ValidatedLocations); Assert.Equal(2, validated.ExcludedPaths.Count);
        Assert.Contains(target, validated.ExcludedPaths); Assert.Contains(cache, validated.ExcludedPaths);
        Assert.Equal("preserve output", File.ReadAllText(output)); Assert.Equal("preserve source", File.ReadAllText(source));
    }

    private static string Full(string path) => Path.Join(@"G:\fixture", path.Replace('/', Path.DirectorySeparatorChar));
    private static ScanSnapshot Inventory(string selectedFolder, IEnumerable<string> files, IEnumerable<string> folders)
    {
        var builder = new ScanBuilder(); ulong next = 1;
        builder.RootIndex = builder.AddFile(new(new(next++), FileAttributes.Directory, 0, 0, 0));
        var paths = new Dictionary<string, (int Index, ulong Id)>(StringComparer.Ordinal) { [""] = (builder.RootIndex, 1) };
        Folder(selectedFolder);
        foreach (string directory in folders) Folder(directory);
        foreach (string file in files)
        {
            int slash = file.LastIndexOf('/'); string parent = slash < 0 ? "" : file[..slash];
            var folder = Folder(parent);
            int index = builder.AddFile(new(new(next++), FileAttributes.Normal, 1, 1, 0));
            builder.AddLink(index, new(folder.Id), slash < 0 ? file : file[(slash + 1)..]);
        }
        return builder.Build(@"G:\fixture", "fixture", TimeSpan.Zero, 0, default);

        (int Index, ulong Id) Folder(string path)
        {
            if (paths.TryGetValue(path, out var existing)) return existing;
            int slash = path.LastIndexOf('/'); var parent = Folder(slash < 0 ? "" : path[..slash]);
            ulong id = next++; int index = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(index, new(parent.Id), slash < 0 ? path : path[(slash + 1)..]);
            return paths[path] = (index, id);
        }
    }
}
