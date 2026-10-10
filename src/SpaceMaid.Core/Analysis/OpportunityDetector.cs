using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

public static class OpportunityDetector
{
    public static IReadOnlyList<Opportunity> Find(ScanSnapshot scan, CancellationToken token = default)
    {
        var found = new List<Opportunity>();
        for (int directory = 0; directory < scan.Records.Length; directory++)
        {
            if ((directory & 1023) == 0) token.ThrowIfCancellationRequested();
            if (!scan.Records[directory].IsDirectory) continue;
            int link = scan.FirstLinks[directory];
            // The selected folder itself can be a cache or project artifact.
            string? rootName = directory == scan.RootIndex ? Path.GetFileName(Path.TrimEndingDirectorySeparator(scan.RootPath)) : null;
            ReadOnlySpan<char> name = rootName is not null ? rootName : link >= 0 ? scan.Name(link) : [];
            int parent = link >= 0 ? scan.Links[link].ParentIndex : -1;
            Rule? rule = Classify(scan, directory, parent, name);
            if (rule is null) continue;
            string? path = scan.PathForFile(directory);
            if (path is null) continue;
            found.Add(new(directory, rule.Title, rule.Category, path,
                scan.DirectoryLogical[directory], scan.DirectoryAllocated[directory],
                rule.Reason, rule.Caution, rule.Action, rule.Manual));
        }
        return found.OrderByDescending(o => o.DisplayBytes(scan.HasAllocationData))
            .ThenBy(o => o.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private sealed record Rule(string Title, string Category, string Reason, string Caution, string Action, bool Manual = false);

    private static Rule? Classify(ScanSnapshot scan, int directory, int parent, ReadOnlySpan<char> name)
    {
        if ((Is(name, "bin") || Is(name, "obj")) && parent >= 0 && HasProject(scan, parent, ".csproj", ".fsproj", ".vbproj"))
            return new(".NET build output", "Build output", "A bin/obj directory beside a .NET project file.",
                "Build outputs may include files copied by hand. Review them and stop running builds first.", "Rebuild with dotnet build after cleanup.");
        if (Is(name, "node_modules") && parent >= 0 && HasChild(scan, parent, "package.json", directory: false))
            return new("Node dependencies", "Dependencies", "Installed dependencies beside package.json.",
                "Restoring may need network access. Local edits inside dependencies would be lost.", "Restore with the project's npm, pnpm, or Yarn install command.");
        if ((Is(name, ".next") || Is(name, "dist") || Is(name, "build")) && parent >= 0 && HasChild(scan, parent, "package.json", directory: false))
            return new("Possible Node build output", "Build output", "A common build directory beside package.json.",
                "The project may keep authored files here. The directory name does not prove every file is generated.", "Check the project's build configuration before removing anything.", true);
        if (HasChild(scan, directory, "pyvenv.cfg", directory: false))
            return new("Python virtual environment", "Python environments", "The directory contains pyvenv.cfg.",
                "Recreation needs the correct interpreter and dependency list. Locally installed or edited packages may be unique.", "Record the environment requirements before recreating it.", true);
        if (Is(name, "__pycache__") && HasProject(scan, directory, ".pyc"))
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
        if (HasChild(scan, directory, ".git", directory: false))
            return new("Linked Git checkout", "Git worktrees", "The checkout contains a .git pointer file; it may be a worktree or submodule.",
                "It may contain source changes or untracked work. No disposable/abandoned status is inferred.",
                "Review with git status and git worktree list; remove through Git or the owning app after preserving work.", true);
        return null;
    }

    private static bool Is(ReadOnlySpan<char> value, string expected) => value.Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static bool HasChild(ScanSnapshot scan, int directoryIndex, string name, bool directory)
    {
        foreach (int link in scan.Children(directoryIndex))
            if (scan.Records[scan.Links[link].FileIndex].IsDirectory == directory && Is(scan.Name(link), name)) return true;
        return false;
    }
    private static bool HasProject(ScanSnapshot scan, int directory, params string[] extensions)
    {
        foreach (int link in scan.Children(directory))
        {
            if (scan.Records[scan.Links[link].FileIndex].IsDirectory) continue;
            foreach (string extension in extensions)
                if (scan.Name(link).EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    private static bool ParentNamed(ScanSnapshot scan, int parent, string name) => parent >= 0 &&
        (parent == scan.RootIndex ? Is(Path.GetFileName(Path.TrimEndingDirectorySeparator(scan.RootPath)), name)
            : scan.FirstLinks[parent] >= 0 && Is(scan.Name(scan.FirstLinks[parent]), name));
    private static bool AncestorNamed(ScanSnapshot scan, int directory, string name)
    {
        int remaining = scan.FileCount;
        while (directory >= 0 && remaining-- > 0)
        {
            if (ParentNamed(scan, directory, name)) return true;
            if (directory == scan.RootIndex || scan.FirstLinks[directory] < 0) break;
            directory = scan.Links[scan.FirstLinks[directory]].ParentIndex;
        }
        return false;
    }
}
