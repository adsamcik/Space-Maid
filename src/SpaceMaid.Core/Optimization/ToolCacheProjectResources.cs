using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpaceMaid.Core.Optimization;

// Bounded, passive layout observations. These do not establish rebuildability
// or execute project/configuration logic, and never authorize deletion.
internal static class ToolCacheProjectResources
{
    internal static IReadOnlyList<ToolCacheResource> Cargo(string? scope, Func<string, string?> environment, string? home)
    {
        var result = new List<ToolCacheResource>();
        if (!ProjectRoot(scope, "Cargo.toml", out string project)) return result.AsReadOnly();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool ambiguous = false;
        var parents = new Stack<string>();
        for (string? p = project; p is not null && parents.Count < 64; p = Path.GetDirectoryName(p)) parents.Push(p);
        if (home is not null) ReadConfig(home);
        foreach (string parent in parents) ReadConfig(Path.Join(parent, ".cargo"));
        string? target = environment("CARGO_TARGET_DIR") ?? environment("CARGO_BUILD_TARGET_DIR") ?? values.GetValueOrDefault("target-dir");
        string? build = environment("CARGO_BUILD_BUILD_DIR") ?? values.GetValueOrDefault("build-dir");
        if (ambiguous)
        {
            result.Add(new("Cargo project", project, "Output configuration is too large, complex or ambiguous for passive inspection. No guessed root is authorized.",
                "Inspect with the owning tool after choosing a trusted project."));
            return result.AsReadOnly();
        }
        AddVariants(target ?? Path.Join(project, "target"), "Cargo outputs");
        if (build is not null && !string.Equals(build, target, StringComparison.OrdinalIgnoreCase)) AddVariants(build, "Cargo intermediates");
        return result.AsReadOnly();

        void ReadConfig(string directory)
        {
            string? config = new[] { "config", "config.toml" }.Select(name => Path.Join(directory, name)).FirstOrDefault(File.Exists);
            if (config is null) return;
            string text;
            try { text = ReadSmall(config); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { ambiguous = true; return; }
            string section = "";
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) { section = line; continue; }
                if (section != "[build]") continue;
                var match = Regex.Match(line, @"^(target-dir|build-dir)\s*=\s*(.+)$", RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                string rawPath = match.Groups[2].Value.Trim(); string? value = null;
                try
                {
                    if (rawPath.StartsWith('"')) value = JsonSerializer.Deserialize<string>(rawPath);
                    else if (rawPath.Length >= 2 && rawPath.StartsWith('\'') && rawPath.EndsWith('\'')) value = rawPath[1..^1];
                }
                catch (JsonException) { }
                if (string.IsNullOrWhiteSpace(value) || value.Contains('{') || value.Contains('$')) { ambiguous = true; continue; }
                // Cargo config-relative paths are based on the parent of .cargo.
                string basePath = Path.GetDirectoryName(directory) ?? project;
                values[match.Groups[1].Value] = Path.IsPathFullyQualified(value) ? value : Path.GetFullPath(value, basePath);
            }
        }
        void AddVariants(string candidate, string category)
        {
            string root;
            try { root = ToolCachePaths.Root(Path.IsPathFullyQualified(candidate) ? candidate : Path.GetFullPath(candidate, project)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return; }
            bool marker = File.Exists(Path.Join(root, ".rustc_info.json"));
            int examined = 0;
            foreach (string directory in Directory.EnumerateDirectories(root).Take(64))
            {
                if (++examined > 64) break;
                try
                {
                    ToolCachePaths.Root(directory);
                    if (Directory.Exists(Path.Join(directory, ".fingerprint"))) Add(directory);
                    else foreach (string child in Directory.EnumerateDirectories(directory).Take(32))
                        if (Directory.Exists(Path.Join(child, ".fingerprint"))) { ToolCachePaths.Root(child); Add(child); }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            if (marker && !result.Any(r => OptimizationPolicy.Covers(root, r.Path)))
                result.Add(new(category, root, "Cargo compiler metadata is present; exact producer ownership, shared consumers and supported layout remain unverified.",
                    "Rebuild requires compatible toolchain, dependencies and inputs. No automatic removal."));
            void Add(string path) => result.Add(new(category + " · " + Path.GetRelativePath(root, path), path,
                "Cargo fingerprint layout observed in this exact profile/target. Shared consumers and per-file producer ownership remain unknown.",
                "Rebuild cost and offline dependency availability need review. No broad cargo clean is run."));
        }
    }

    internal static IReadOnlyList<ToolCacheResource> Gradle(string? scope, string? home)
    {
        var result = new List<ToolCacheResource>();
        if (home is not null)
        {
            string caches = Path.Join(home, "caches");
            if (Directory.Exists(caches))
                foreach (string directory in Directory.EnumerateDirectories(caches).Take(64))
                {
                    string name = Path.GetFileName(directory);
                    if (!Regex.IsMatch(name, @"^[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant)) continue;
                    try
                    {
                        ToolCachePaths.Root(directory);
                        result.Add(new("Gradle " + name, directory,
                            "Version-specific cache directory observed. Native usage/retention policy decides maintenance; an old name is not proof of non-use.",
                            "May regenerate locally or need downloads depending on cache class."));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                }
        }
        if (scope is not null && Directory.Exists(scope)
            && new[] { "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts" }.Any(name => File.Exists(Path.Join(scope, name))))
        {
            try
            {
                string root = ToolCachePaths.Root(scope);
                foreach (string name in new[] { ".gradle", "build" })
                {
                    string path = Path.Join(root, name); if (!Directory.Exists(path)) continue;
                    ToolCachePaths.Root(path);
                    result.Add(new("Gradle project " + name, path,
                        "Project marker and directory observed. Build scripts can relocate or customize outputs; no project evaluation or inferred deletion authority.",
                        "Known generated outputs need task/variant provenance. Unknown local state stays protected."));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        }
        return result.AsReadOnly();
    }

    private static bool ProjectRoot(string? scope, string marker, out string root)
    {
        root = "";
        if (scope is null || !File.Exists(Path.Join(scope, marker))) return false;
        try { root = ToolCachePaths.Root(scope); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return false; }
    }
    private static string ReadSmall(string path)
    {
        ToolCachePaths.ValidateFile(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (input.Length > 64 * 1024) throw new InvalidDataException("Configuration exceeds the passive inspection limit.");
        using var reader = new StreamReader(input); return reader.ReadToEnd();
    }
}
