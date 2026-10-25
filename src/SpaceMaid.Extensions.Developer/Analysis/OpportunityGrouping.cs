using SpaceMaid.Core.Analysis;
using System.IO;

namespace SpaceMaid.Extensions.Developer.Analysis;

// Uses the existing inventory only. Git markers establish folder context,
// not repository identity, clean status, branch integration or abandonment.
public static class OpportunityGrouping
{
    public static OpportunityGroupSet Build(IOpportunityInventory scan, IReadOnlyList<Opportunity> locations, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var markers = new Dictionary<int, int>();
        var owners = new Dictionary<int, (int Directory, string Kind)>();
        var projectBuckets = new Dictionary<string, (string Name, string Path, string Kind, List<Opportunity> Items)>(StringComparer.Ordinal);
        var typeBuckets = new Dictionary<string, List<Opportunity>>(StringComparer.Ordinal);
        foreach (var location in locations)
        {
            token.ThrowIfCancellationRequested();
            int directory = location.DirectoryIndex;
            if (directory < 0 || directory >= scan.FileCount || !scan.IsDirectory(directory))
                throw new ArgumentException("A finding must reference a scanned folder.", nameof(locations));
            var owner = FindOwner(directory);
            string path = scan.PathForFile(owner.Directory) ?? scan.RootPath;
            bool other = owner.Kind == "Containing folder" && location.Category is "Package caches" or "Agent files";
            string key = other ? "other:" + location.Category : "folder:" + path;
            if (!projectBuckets.TryGetValue(key, out var bucket))
            {
                string name = other ? location.Category == "Agent files" ? "Coding-agent files" : "Other package caches"
                    : owner.Directory == scan.RootIndex || scan.PrimaryLink(owner.Directory) < 0 ? scan.RootPath
                        : scan.Name(scan.PrimaryLink(owner.Directory)).ToString();
                bucket = (name, other ? scan.RootPath : path, other ? "Outside detected projects" : owner.Kind, []);
                projectBuckets.Add(key, bucket);
            }
            bucket.Items.Add(location);
            if (!typeBuckets.TryGetValue(location.Category, out var items)) typeBuckets.Add(location.Category, items = []);
            items.Add(location);
        }
        var projects = projectBuckets.Select(pair => new OpportunityGroup(pair.Key, pair.Value.Name, pair.Value.Path,
            pair.Value.Kind, pair.Value.Items.ToArray(), UnionBytes(pair.Value.Items))).OrderByDescending(group => group.ObservedBytes)
            .ThenBy(group => group.Path, StringComparer.Ordinal).ToArray();
        var types = typeBuckets.Select(pair => new OpportunityGroup("type:" + pair.Key, pair.Key, scan.RootPath,
            "Clutter type", pair.Value.ToArray(), UnionBytes(pair.Value))).OrderByDescending(group => group.ObservedBytes)
            .ThenBy(group => group.Name, StringComparer.Ordinal).ToArray();
        var languageBuckets = new Dictionary<string, (string Name, Dictionary<string, List<Opportunity>> Projects)>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            foreach (var location in project.Locations)
            {
                token.ThrowIfCancellationRequested();
                if (location.Category != "Build output") continue;
                var language = BuildLanguage(location);
                if (!languageBuckets.TryGetValue(language.Key, out var bucket))
                    languageBuckets.Add(language.Key, bucket = (language.Name, new(StringComparer.Ordinal)));
                if (!bucket.Projects.TryGetValue(project.Key, out var items)) bucket.Projects.Add(project.Key, items = []);
                items.Add(location);
            }
        }
        var projectByKey = projects.ToDictionary(project => project.Key, StringComparer.Ordinal);
        var languages = new List<OpportunityGroup>();
        foreach (var (key, bucket) in languageBuckets)
        {
            token.ThrowIfCancellationRequested();
            string languageKey = "build-language:" + key;
            var children = bucket.Projects.Select(pair =>
            {
                token.ThrowIfCancellationRequested();
                var project = projectByKey[pair.Key];
                return new OpportunityGroup(languageKey + "/" + project.Key, project.Name, project.Path, project.Kind,
                    pair.Value.AsReadOnly(), UnionBytes(pair.Value));
            }).OrderByDescending(group => group.ObservedBytes).ThenBy(group => group.Path, StringComparer.Ordinal).ToArray();
            var findings = children.SelectMany(child => child.Locations).ToArray();
            var contents = findings.GroupBy(item => (item.Title, Folder: FolderName(item.DirectoryIndex))).Select(group =>
            {
                token.ThrowIfCancellationRequested();
                var items = group.ToArray();
                var first = items[0];
                return new OpportunityGroup(languageKey + "/contents/" + group.Key.Title + "/" + group.Key.Folder,
                    group.Key.Folder + "/", scan.RootPath, "Detected build folders", Array.AsReadOnly(items), UnionBytes(items), first.Caution)
                {
                    Caption = DescribeBuildContents(first.Title, group.Key.Folder),
                    StatusLabel = first.SuggestedAction,
                    CanSelectGroup = false
                };
            }).OrderByDescending(group => group.ObservedBytes).ThenBy(group => group.Name, StringComparer.Ordinal).ToArray();
            languages.Add(new(languageKey, bucket.Name, scan.RootPath, "Build language / system", Array.AsReadOnly(findings),
                UnionBytes(findings), "Classified from detected build folders and project-marker filenames. This does not classify the whole project.",
                Array.AsReadOnly(children))
            {
                Caption = string.Join(" · ", contents.Select(group => group.Name).Distinct(StringComparer.Ordinal).Take(4))
                    + (contents.Length > 4 ? $" · {contents.Length - 4:N0} more folder types" : ""),
                ContentGroups = Array.AsReadOnly(contents)
            });
        }
        var breakdowns = new Dictionary<string, IReadOnlyList<OpportunityGroup>>(StringComparer.Ordinal);
        foreach (var (category, key) in new[] { ("Tool caches", "cleanup:tools"), ("Test artifacts", "cleanup:tests"),
            ("Agent files", "cleanup:agent"), ("Agent checkpoints", "cleanup:checkpoints"), ("AI models & data", "cleanup:models"),
            ("Containers & virtual disks", "cleanup:containers") })
        {
            var toolGroups = new List<OpportunityGroup>();
            var candidates = projects.SelectMany(project => project.Locations.Where(item => item.Category == category)
                .Select(item => (Project: project, Item: item))).GroupBy(item => ToolName(item.Item), StringComparer.Ordinal);
            foreach (var tool in candidates)
            {
                token.ThrowIfCancellationRequested();
                string toolKey = key + "/" + tool.Key;
                var children = tool.GroupBy(item => item.Project.Key, StringComparer.Ordinal).Select(group =>
                {
                    token.ThrowIfCancellationRequested();
                    var project = group.First().Project;
                    var findings = group.Select(item => item.Item).ToArray();
                    return new OpportunityGroup(toolKey + "/" + project.Key, project.Name, project.Path, project.Kind,
                        Array.AsReadOnly(findings), UnionBytes(findings))
                    { CanSelectGroup = findings.Any(item => item.CleanupBlockReason.Length == 0) };
                }).OrderByDescending(group => group.ObservedBytes).ThenBy(group => group.Path, StringComparer.Ordinal).ToArray();
                var locationsForTool = children.SelectMany(child => child.Locations).ToArray();
                toolGroups.Add(new(toolKey, tool.Key, scan.RootPath, "Tool / project", Array.AsReadOnly(locationsForTool),
                    UnionBytes(locationsForTool), "Detected tool folders, grouped by project or containing folder. Review exact paths and cautions.", Array.AsReadOnly(children))
                { CanSelectGroup = locationsForTool.Any(item => item.CleanupBlockReason.Length == 0) });
            }
            if (toolGroups.Count > 0) breakdowns.Add(key, toolGroups.OrderByDescending(group => group.ObservedBytes)
                .ThenBy(group => group.Name, StringComparer.Ordinal).ToList().AsReadOnly());
        }
        return new(projects, types, languages.OrderByDescending(group => group.ObservedBytes)
            .ThenBy(group => group.Name, StringComparer.Ordinal).ToList().AsReadOnly(),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<OpportunityGroup>>(breakdowns));

        static string ToolName(Opportunity item) => item.Title switch
        {
            "Gradle project cache" => "Gradle",
            "pytest cache" => "pytest", "mypy cache" => "mypy", "Ruff cache" => "Ruff",
            "Turborepo cache" => "Turborepo", "Parcel cache" => "Parcel", "Angular workspace cache" => "Angular",
            "Vite dependency cache" => "Vite", ".NET test results" => ".NET",
            "Playwright test results" or "Playwright HTML report" => "Playwright",
            "Codex logs" or "Codex session history" => "Codex",
            "Claude Code project history" or "Claude Code debug files" or "Claude Code file checkpoints" => "Claude Code",
            "GitHub Copilot session history" => "GitHub Copilot",
            "Gemini CLI session history" => "Gemini CLI",
            "Cursor workspace history" or "Cursor global chat storage" => "Cursor",
            "Cline task history" or "Cline file checkpoints" => "Cline",
            "Roo Code task history" => "Roo Code",
            "OpenCode logs" or "OpenCode session storage" or "OpenCode download cache" => "OpenCode",
            "Hugging Face repository downloads" => "Hugging Face",
            "Ollama model library" => "Ollama",
            "LM Studio model downloads" => "LM Studio",
            "Docker virtual disk storage" or "Docker engine data store" => "Docker",
            "Linux virtual disk storage" => "Linux / WSL (verify owner)",
            "Virtual disk storage" => "Other virtual disks",
            _ => "Other tools"
        };

        (string Key, string Name) BuildLanguage(Opportunity location)
        {
            // Specific build evidence wins over incidental language markers in a mixed project.
            switch (location.Title)
            {
                case "Cargo build output": return ("rust", "Rust / Cargo");
                case "Maven build output": return ("maven", "Java / Maven");
                case "Gradle build output": return ("gradle", "Java / Kotlin / Gradle");
                case "CMake build output": return ("cmake", "C / C++ / CMake");
                case "SvelteKit build output": case "Nuxt build output": return ("javascript", "JavaScript / TypeScript");
            }
            int directory = location.DirectoryIndex, parent = Parent(directory);
            int link = scan.PrimaryLink(directory);
            ReadOnlySpan<char> name = directory == scan.RootIndex ? Path.GetFileName(Path.TrimEndingDirectorySeparator(scan.RootPath))
                : link >= 0 ? scan.Name(link) : [];
            int marker = parent >= 0 ? Marker(parent) : 0;
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                int languages = marker & (8 | 16 | 32);
                return languages switch
                {
                    8 => ("csharp", "C# / .NET"),
                    16 => ("fsharp", "F# / .NET"),
                    32 => ("visual-basic", "Visual Basic / .NET"),
                    > 0 => ("dotnet-mixed", ".NET (mixed languages)"),
                    _ => location.Title == ".NET build output" ? ("dotnet", ".NET") : ("other", "Other build files")
                };
            }
            if ((marker & 64) != 0 && (name.Equals("build", StringComparison.OrdinalIgnoreCase)
                || name.Equals("dist", StringComparison.OrdinalIgnoreCase) || name.Equals(".next", StringComparison.OrdinalIgnoreCase)))
                return ("javascript", "JavaScript / TypeScript");
            if (name.Equals("__pycache__", StringComparison.OrdinalIgnoreCase) && (Marker(directory) & 128) != 0)
                return ("python", "Python");
            return ("other", "Other build files");
        }

        (int Directory, string Kind) FindOwner(int directory)
        {
            if (owners.TryGetValue(directory, out var cached)) return cached;
            int pointer = -1, project = -1;
            var visited = new HashSet<int>();
            for (int current = directory; current >= 0 && visited.Add(current); current = Parent(current))
            {
                token.ThrowIfCancellationRequested();
                int marker = Marker(current);
                if ((marker & 1) != 0) return owners[directory] = (current, "Git project folder");
                if ((marker & 2) != 0 && pointer < 0) pointer = current;
                if ((marker & 4) != 0 && project < 0) project = current;
            }
            // Linked checkouts nested beneath a Git project stay with that
            // containing project. External checkouts retain their own group.
            if (pointer >= 0) return owners[directory] = (pointer, "Linked checkout folder");
            if (project >= 0) return owners[directory] = (project, "Project folder");
            int parent = Parent(directory);
            return owners[directory] = (parent >= 0 ? parent : directory, "Containing folder");
        }
        int Parent(int directory) => directory == scan.RootIndex || scan.PrimaryLink(directory) < 0
            ? -1 : scan.LinkParent(scan.PrimaryLink(directory));
        string FolderName(int directory) => (directory == scan.RootIndex
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(scan.RootPath))
            : scan.PrimaryLink(directory) is >= 0 and var link ? scan.Name(link).ToString() : "build output");
        int Marker(int directory)
        {
            if (markers.TryGetValue(directory, out int value)) return value;
            int visited = 0;
            foreach (int link in scan.Children(directory))
            {
                if ((visited++ & 1023) == 0) token.ThrowIfCancellationRequested();
                var name = scan.Name(link);
                bool isDirectory = scan.IsDirectory(scan.LinkObject(link));
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) value |= isDirectory ? 1 : 2;
                if (!isDirectory)
                {
                    if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) value |= 8;
                    if (name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)) value |= 16;
                    if (name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)) value |= 32;
                    if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase)) value |= 64;
                    if (name.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)) value |= 128;
                }
                if (!isDirectory && (name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) || name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) || name.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("settings.gradle", StringComparison.OrdinalIgnoreCase) || name.Equals("settings.gradle.kts", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) || name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("pytest.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("ruff.toml", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))) value |= 4;
            }
            markers.Add(directory, value); return value;
        }
        long UnionBytes(IEnumerable<Opportunity> items)
        {
            var selected = items.Select(item => item.DirectoryIndex).ToHashSet();
            long bytes = 0;
            foreach (int directory in selected)
            {
                token.ThrowIfCancellationRequested();
                var visited = new HashSet<int>();
                bool covered = false;
                for (int parent = Parent(directory); parent >= 0 && visited.Add(parent); parent = Parent(parent))
                { token.ThrowIfCancellationRequested(); if (selected.Contains(parent)) { covered = true; break; } }
                if (!covered) bytes = checked(bytes + (scan.HasAllocationData ? scan.AllocatedSize(directory) : scan.LogicalSize(directory)));
            }
            return bytes;
        }
    }

    private static string DescribeBuildContents(string title, string folder) => title switch
    {
        ".NET build output" => folder.Equals("obj", StringComparison.OrdinalIgnoreCase)
            ? "Intermediate compiler files, generated code and restore metadata."
            : "Compiled applications and libraries, debug symbols and copied runtime files.",
        "Cargo build output" => "Compiled crates, binaries, dependency artifacts and incremental compilation files, often under debug/ and release/.",
        "Gradle build output" => "Compiled classes, generated sources, intermediate files, packages and build reports for a Gradle module.",
        "Maven build output" => "Compiled classes, generated sources, packaged JARs or WARs and build reports.",
        "CMake build output" => "CMake configure state, generated build files, object files and compiled targets. Local configure options may be lost.",
        "Python bytecode cache" => "Compiled Python bytecode (.pyc). This finding covers the __pycache__ folder.",
        "SvelteKit build output" => "Generated SvelteKit application files, types and intermediate build output.",
        "Nuxt build output" => "Generated Nuxt application files, types and intermediate build output.",
        "Possible Node build output" when folder.Equals(".next", StringComparison.OrdinalIgnoreCase) =>
            "Next.js build output and caches. Review the project's configuration and any custom files.",
        "Possible Node build output" => "Possible web bundles, generated assets and copied static files. The folder name alone does not prove its contents are generated.",
        _ => "Detected build folders. Inspect their contents; regeneration has not been established."
    };
}
