using System.IO;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OpportunityGroupingTests
{
    [Fact]
    public void NestedCheckoutsStayWithTheirContainingGitProjectAndSizesDoNotDoubleCount()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int repo = Folder(2, 1, "project"); Folder(3, 2, ".git");
        int checkout = Folder(4, 2, "checkout"); File(5, 4, ".git", 0);
        int dependencies = Folder(6, 4, "node_modules"); File(7, 6, "package.bin", 4096);
        int output = Folder(8, 2, "bin"); File(9, 8, "app.dll", 8192);
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, 0, default);
        var opportunities = new[] { Finding(scan, checkout, "Git worktrees"), Finding(scan, dependencies, "Dependencies"), Finding(scan, output, "Build output") };
        var groups = OpportunityGrouping.Build(scan, opportunities);
        var project = Assert.Single(groups.Projects);
        Assert.Equal(@"G:\project", project.Path); Assert.Equal("Git project folder", project.Kind);
        Assert.Equal(3, project.Locations.Count); Assert.Equal(12288, project.ObservedBytes);
        Assert.Equal(3, groups.Types.Count);
        Assert.Equal(16384, groups.Types.Sum(group => group.ObservedBytes)); // Different types can overlap.
        Assert.Equal(scan.RootEntry.AllocatedBytes, project.ObservedBytes);

        int Folder(ulong id, ulong parent, string name)
        { int file = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(file, new(parent), name); return file; }
        void File(ulong id, ulong parent, string name, long bytes)
        { int file = builder.AddFile(new(new(id), FileAttributes.Normal, bytes, bytes, 0)); builder.AddLink(file, new(parent), name); }
    }

    [Fact]
    public void CaseSensitiveProjectPathsAndExternalLinkedCheckoutsRemainDistinct()
    {
        var builder = new ScanBuilder { AllocationData = false };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        var locations = new List<int>();
        for (ulong i = 0; i < 3; i++)
        {
            ulong id = 10 + i * 10;
            int repo = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(repo, new(1), i == 0 ? "Project" : i == 1 ? "project" : "external-checkout");
            int marker = builder.AddFile(new(new(id + 1), i == 2 ? FileAttributes.Normal : FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(marker, new(id), ".git");
            int folder = builder.AddFile(new(new(id + 2), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(folder, new(id), "obj"); locations.Add(folder);
            int data = builder.AddFile(new(new(id + 3), FileAttributes.Normal, 100, 0, 0));
            builder.AddLink(data, new(id + 2), "file.bin");
        }
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, 0, default);
        var groups = OpportunityGrouping.Build(scan, locations.Select(index => Finding(scan, index, "Build output")).ToArray());
        Assert.Equal(3, groups.Projects.Count); Assert.Equal(3, groups.Projects.Select(group => group.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(groups.Projects, group => Assert.Equal(100, group.ObservedBytes));
        Assert.Equal("Linked checkout folder", Assert.Single(groups.Projects, group => group.Name == "external-checkout").Kind);
    }

    [Fact]
    public void ProjectManifestsAndUnownedCachesHaveUsefulFallbackGroupsAndCancellation()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int repo = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(repo, new(1), "web-app");
        int manifest = builder.AddFile(new(new(3), FileAttributes.Normal, 0, 0, 0)); builder.AddLink(manifest, new(2), "package.json");
        int cache = builder.AddFile(new(new(4), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(cache, new(2), "node_modules");
        int shared = builder.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(shared, new(1), "package-cache");
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, 0, default);
        var locations = new[] { Finding(scan, cache, "Dependencies"), Finding(scan, shared, "Package caches") };
        var groups = OpportunityGrouping.Build(scan, locations);
        Assert.Equal("Project folder", Assert.Single(groups.Projects, group => group.Name == "web-app").Kind);
        Assert.Equal("Outside detected projects", Assert.Single(groups.Projects, group => group.Name == "Other package caches").Kind);
        Assert.Throws<OperationCanceledException>(() => OpportunityGrouping.Build(scan, locations, new CancellationToken(true)));
    }

    [Fact]
    public void BuildLanguagesUseProjectMarkersAndRetainHonestMixedAndUnknownGroups()
    {
        var builder = new ScanBuilder(); ulong next = 1;
        builder.RootIndex = builder.AddFile(new(new(next++), FileAttributes.Directory, 0, 0, 0));
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["C# / .NET"] = Output("csharp", ["app.CSPROJ"], "bin"),
            ["F# / .NET"] = Output("fsharp", ["app.fsproj"], "obj"),
            ["Visual Basic / .NET"] = Output("vb", ["app.vbproj"], "bin"),
            [".NET (mixed languages)"] = Output("mixed", ["app.csproj", "library.fsproj"], "obj"),
            ["JavaScript / TypeScript"] = Output("web", ["package.json"], ".next"),
            ["Python"] = Output("python", ["pyproject.toml"], "__pycache__"),
            [".NET"] = Output("unknown-dotnet", ["incidental.cs"], "bin"),
            ["Other build files"] = Output("unsupported", ["Cargo.toml", "build.gradle"], "target")
        };
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, 0, default);
        var detected = OpportunityDetector.Find(scan);
        var genericDotnet = Finding(scan, expected[".NET"], "Build output") with { Title = ".NET build output" };
        var unsupported = Finding(scan, expected["Other build files"], "Build output");
        var findings = detected.Concat([genericDotnet, unsupported]).ToArray();
        var groups = OpportunityGrouping.Build(scan, findings);
        Assert.NotNull(groups.BuildLanguages); Assert.Equal(8, groups.BuildLanguages.Count);
        foreach (var (name, folder) in expected)
        {
            var language = Assert.Single(groups.BuildLanguages, item => item.Name == name);
            Assert.Equal(folder, Assert.Single(language.Locations).DirectoryIndex);
            Assert.Equal(4096, language.ObservedBytes);
            var child = Assert.Single(language.Children!);
            var owner = Assert.Single(groups.Projects, item => item.Locations.Any(location => location.DirectoryIndex == folder));
            Assert.Equal(owner.Name, child.Name); Assert.Equal(owner.Path, child.Path); Assert.Equal(owner.Kind, child.Kind);
            var content = Assert.Single(language.ContentGroups!);
            Assert.Equal(folder, Assert.Single(content.Locations).DirectoryIndex);
            Assert.Equal(4096, content.ObservedBytes);
            Assert.False(content.CanSelectGroup);
            Assert.NotEmpty(content.Caption);
            Assert.Equal(language.Locations[0].Caution, content.Summary);
            Assert.Equal(language.Locations[0].SuggestedAction, content.StatusLabel);
        }
        Assert.All(groups.BuildLanguages.SelectMany(group => group.Locations), location => Assert.Equal("Build output", location.Category));
        Assert.Contains(findings, location => location.Category == "Dependencies");
        Assert.Equal(findings.Count(location => location.Category == "Build output"), groups.BuildLanguages.Sum(group => group.Locations.Count));
        // Classification never creates a cleanup finding for unsupported output.
        Assert.DoesNotContain(detected, location => location.DirectoryIndex == unsupported.DirectoryIndex);

        int Output(string projectName, string[] markers, string outputName)
        {
            ulong projectId = next++; int project = builder.AddFile(new(new(projectId), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(project, new(1), projectName);
            foreach (string name in markers)
            { int file = builder.AddFile(new(new(next++), FileAttributes.Normal, 0, 0, 0)); builder.AddLink(file, new(projectId), name); }
            ulong outputId = next++; int output = builder.AddFile(new(new(outputId), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(output, new(projectId), outputName);
            int data = builder.AddFile(new(new(next++), FileAttributes.Normal, 256, 4096, 0));
            builder.AddLink(data, new(outputId), outputName == "__pycache__" ? "module.pyc" : "artifact.bin");
            if (projectName == "web")
            {
                int dependencies = builder.AddFile(new(new(next++), FileAttributes.Directory, 0, 0, 0));
                builder.AddLink(dependencies, new(projectId), "node_modules");
            }
            return output;
        }
    }

    [Fact]
    public void BuildLanguageProjectsKeepMonorepoMembershipCaseAndNestedUnionSizes()
    {
        var builder = new ScanBuilder { AllocationData = false };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        Folder(2, 1, "Project"); Folder(3, 2, ".git");
        Folder(4, 2, "backend"); File(5, 4, "app.csproj", 0);
        int outer = Folder(6, 4, "bin"); File(7, 6, "output.dll", 100); File(9, 6, "nested.csproj", 0);
        int inner = Folder(8, 6, "obj"); File(10, 8, "output.dll", 200);
        Folder(11, 2, "frontend"); File(12, 11, "package.json", 0);
        int web = Folder(13, 11, "dist"); File(14, 13, "web.js", 600);
        Folder(20, 1, "project"); Folder(21, 20, ".git"); File(22, 20, "app.csproj", 0);
        int lower = Folder(23, 20, "obj"); File(24, 23, "output.dll", 400);
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, 0, default);
        var findings = OpportunityDetector.Find(scan);
        var groups = OpportunityGrouping.Build(scan, findings);
        var csharp = Assert.Single(groups.BuildLanguages!, group => group.Name == "C# / .NET");
        var javascript = Assert.Single(groups.BuildLanguages!, group => group.Name == "JavaScript / TypeScript");
        Assert.Equal(700, csharp.ObservedBytes); Assert.Equal(600, javascript.ObservedBytes);
        Assert.Equal(new[] { outer, inner, lower }.Order(), csharp.Locations.Select(location => location.DirectoryIndex).Order());
        Assert.Equal(web, Assert.Single(javascript.Locations).DirectoryIndex);
        Assert.Equal(2, csharp.Children!.Count);
        Assert.Equal(2, csharp.Children.Select(child => child.Key).Distinct(StringComparer.Ordinal).Count());
        var upper = Assert.Single(csharp.Children, child => child.Path == @"G:\Project");
        var lowerCase = Assert.Single(csharp.Children, child => child.Path == @"G:\project");
        Assert.Equal(300, upper.ObservedBytes); Assert.Equal(400, lowerCase.ObservedBytes);
        Assert.Equal("Git project folder", upper.Kind);
        Assert.Equal(upper.Path, Assert.Single(javascript.Children!).Path);
        Assert.DoesNotContain(upper.Locations, location => location.DirectoryIndex == web);
        Assert.Equal(900, Assert.Single(groups.Projects, group => group.Path == upper.Path).ObservedBytes);
        Assert.Equal(1300, Assert.Single(groups.Types).ObservedBytes);
        Assert.Equal(300, Assert.Single(csharp.ContentGroups!, group => group.Name == "bin/").ObservedBytes);
        Assert.Equal(600, Assert.Single(csharp.ContentGroups!, group => group.Name == "obj/").ObservedBytes);
        // Content groups can overlap; the language's union remains the authoritative total.
        Assert.Equal(900, csharp.ContentGroups!.Sum(group => group.ObservedBytes));
        Assert.Contains("Intermediate", Assert.Single(csharp.ContentGroups!, group => group.Name == "obj/").Caption);

        int Folder(ulong id, ulong parent, string name)
        { int file = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(file, new(parent), name); return file; }
        void File(ulong id, ulong parent, string name, long bytes)
        { int file = builder.AddFile(new(new(id), FileAttributes.Normal, bytes, 0, 0)); builder.AddLink(file, new(parent), name); }
    }

    internal static Opportunity Finding(ScanSnapshot scan, int folder, string category) => new(folder, category, category,
        scan.PathForFile(folder)!, scan.DirectoryLogical[folder], scan.DirectoryAllocated[folder], "Fixture evidence", "Review contents first.", "Read-only review.", true)
    { ProviderId = DeveloperOpportunityFixture.ProviderId, RuleId = category, IsWorktree = category == "Git worktrees" };

    [Fact]
    public void SpecificBuildSystemsWinOverIncidentalMarkers()
    {
        var builder = new ScanBuilder(); ulong next = 2;
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        var folders = new Dictionary<string, (int Folder, string Label)>();
        foreach (var (title, label) in new[] {
            ("Cargo build output", "Rust / Cargo"), ("Maven build output", "Java / Maven"),
            ("Gradle build output", "Java / Kotlin / Gradle"), ("CMake build output", "C / C++ / CMake"),
            ("SvelteKit build output", "JavaScript / TypeScript"), ("Nuxt build output", "JavaScript / TypeScript") })
        {
            ulong project = next++;
            int index = builder.AddFile(new(new(project), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(index, new(1), title);
            // An unrelated .NET marker must not relabel corroborated output from a different build system.
            index = builder.AddFile(new(new(next++), FileAttributes.Normal, 0, 0, 0)); builder.AddLink(index, new(project), "incidental.csproj");
            index = builder.AddFile(new(new(next++), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(index, new(project), "obj");
            folders.Add(title, (index, label));
        }
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, 0, default);
        var findings = folders.Select(pair => Finding(scan, pair.Value.Folder, "Build output") with { Title = pair.Key }).ToArray();
        var groups = OpportunityGrouping.Build(scan, findings);
        Assert.Equal(5, groups.BuildLanguages!.Count);
        foreach (var (title, item) in folders)
        {
            Assert.Contains(Assert.Single(groups.BuildLanguages, group => group.Name == item.Label).Locations, finding => finding.Title == title);
            var content = Assert.Single(Assert.Single(groups.BuildLanguages, group => group.Name == item.Label).ContentGroups!,
                group => group.Locations.Any(finding => finding.Title == title));
            Assert.DoesNotContain("regeneration has not been established", content.Caption);
        }
        Assert.Equal(findings.Length, groups.BuildLanguages.Sum(group => group.Locations.Count));
    }

    [Fact]
    public void NewCleanupCategoriesHaveExactToolAndProjectBreakdownsAndUnionTotals()
    {
        var builder = new ScanBuilder(); ulong next = 2;
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        var items = new List<(int Folder, string Title, string Category)>();
        foreach (string name in new[] { "Project", "project" })
        {
            ulong repo = next++; Folder(repo, 1, name); Folder(next++, repo, ".git");
            ulong cache = next++; int outer = Folder(cache, repo, ".ruff_cache"); Data(next++, cache, "data", 100);
            ulong nestedId = next++; int nested = Folder(nestedId, cache, "nested"); Data(next++, nestedId, "data", 200);
            items.Add((outer, "Ruff cache", "Tool caches")); items.Add((nested, "Ruff cache", "Tool caches"));
            Add(repo, "test-results", "Playwright test results", "Test artifacts");
            Add(repo, "playwright-report", "Playwright HTML report", "Test artifacts");
            Add(repo, "TestResults", ".NET test results", "Test artifacts");
            Add(repo, ".vs", "Visual Studio workspace data", "Editor workspace data");
            Add(repo, "dists", "Gradle wrapper downloads", "Downloaded tooling");
        }
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, 0, default);
        var findings = items.Select(item => Finding(scan, item.Folder, item.Category) with { Title = item.Title }).ToArray();
        var grouped = OpportunityGrouping.Build(scan, findings);
        var checklist = CleanupGrouping.Build(scan, findings);
        Assert.Equal(4, checklist.Count);
        Assert.Equal(600, Assert.Single(checklist, group => group.Key == "cleanup:tools").ObservedBytes);
        Assert.Contains(checklist, group => group.Name == "Editor workspace data" && group.Summary.Contains("breakpoints"));
        Assert.Contains(checklist, group => group.Name == "Downloaded tooling" && group.Summary.Contains("offline"));
        var tool = Assert.Single(grouped.CleanupBreakdowns!["cleanup:tools"]);
        Assert.Equal("Ruff", tool.Name); Assert.Equal(600, tool.ObservedBytes);
        Assert.Equal(2, tool.Children!.Count); Assert.Equal(2, tool.Children.Select(child => child.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(tool.Children, child => { Assert.Equal(2, child.Locations.Count); Assert.Equal(300, child.ObservedBytes); });
        Assert.Equal(new[] { @"G:\Project", @"G:\project" }, tool.Children.Select(child => child.Path));
        var reports = grouped.CleanupBreakdowns["cleanup:tests"];
        Assert.Equal(2, reports.Count);
        var playwright = Assert.Single(reports, group => group.Name == "Playwright");
        Assert.Equal(4, playwright.Locations.Count); Assert.Equal(2, playwright.Children!.Count);
        Assert.All(playwright.Locations, finding => Assert.StartsWith("Playwright", finding.Title));
        Assert.Single(reports, group => group.Name == ".NET");
        Assert.Equal(findings.Length, checklist.Sum(group => group.Locations.Count));
        Assert.All(tool.Locations, finding => Assert.Equal("Tool caches", finding.Category));

        int Folder(ulong id, ulong parent, string name)
        { int file = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(file, new(parent), name); return file; }
        void Data(ulong id, ulong parent, string name, long bytes)
        { int file = builder.AddFile(new(new(id), FileAttributes.Normal, bytes, bytes, 0)); builder.AddLink(file, new(parent), name); }
        void Add(ulong parent, string name, string title, string category)
        { ulong id = next++; int folder = Folder(id, parent, name); Data(next++, id, "data", 50); items.Add((folder, title, category)); }
    }

    [Fact]
    public void CleanupChecklistBoundsThousandsOfFindingsAndSeparatesCheckedWorktrees()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        var folders = new List<(int Index, string Category)>();
        string[] categories = ["Build output", "Package caches", "Dependencies", "Python environments", "Agent files", "Git worktrees"];
        for (int i = 0; i < 1200; i++)
        {
            ulong id = (ulong)i * 2 + 2;
            int folder = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(folder, new(1), "folder-" + i);
            int file = builder.AddFile(new(new(id + 1), FileAttributes.Normal, 4096, 4096, 0)); builder.AddLink(file, new(id), "data");
            folders.Add((folder, categories[i % categories.Length]));
        }
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, 0, default);
        var findings = folders.Select(item => Finding(scan, item.Index, item.Category)).ToArray();
        Assert.Equal(6, CleanupGrouping.Build(scan, findings).Count);
        var checkedPaths = findings.Where(item => item.Category == "Git worktrees").Take(50).Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        var groups = CleanupGrouping.Build(scan, findings, checkedPaths);
        Assert.Equal(7, groups.Count); Assert.Equal(1200, groups.Sum(group => group.Locations.Count));
        Assert.Equal(50, Assert.Single(groups, group => group.Name == "Old integrated worktrees").Locations.Count);
        Assert.Equal(150, Assert.Single(groups, group => group.Name == "Other worktrees").Locations.Count);
        Assert.Equal(scan.AllocatedBytes, groups.Sum(group => group.ObservedBytes));
        var ageGroup = Assert.Single(CleanupGrouping.Build(scan, findings, checkedPaths, worktreePolicy: new(WorktreeCleanupMode.Old, 60)), group => group.Name == "Old clean worktrees");
        Assert.Contains("60 days", ageGroup.Summary); Assert.Contains("Unmerged commits", ageGroup.Summary);
        Assert.Equal(50, ageGroup.Locations.Count);
        var integratedGroup = Assert.Single(CleanupGrouping.Build(scan, findings, checkedPaths, worktreePolicy: new(WorktreeCleanupMode.Integrated)), group => group.Name == "Worktrees already in upstream");
        Assert.Equal("Checked locally · Any age", integratedGroup.Kind); Assert.Contains("cached", integratedGroup.Summary);
        Assert.Throws<OperationCanceledException>(() => CleanupGrouping.Build(scan, findings, token: new(true)));
    }

    [Fact]
    public void CleanupChecklistCountsNestedFindingsOnceWithinAGroup()
    {
        var builder = new ScanBuilder { AllocationData = false };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int outer = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(outer, new(1), "build");
        int inner = builder.AddFile(new(new(3), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(inner, new(2), "nested");
        int file = builder.AddFile(new(new(4), FileAttributes.Normal, 4096, 0, 0)); builder.AddLink(file, new(3), "data");
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, 0, default);
        var group = Assert.Single(CleanupGrouping.Build(scan, [Finding(scan, outer, "Build output"), Finding(scan, inner, "Build output")]));
        Assert.Equal(4096, group.ObservedBytes); Assert.Equal(2, group.Locations.Count);
        Assert.Contains("Stop builds", group.Summary);
    }
}
