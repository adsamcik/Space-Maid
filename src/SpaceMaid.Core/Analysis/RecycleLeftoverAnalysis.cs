using SpaceMaid.Core.IO;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

public enum RecycleLeftoverKind
{
    NuGetPackages, NuGetData, Rustup, IdeData, AndroidImages, Documents, Media, Archives, Logs, Other
}

// Recognition uses the captured tree. It never grants permission to mutate an entry.
public static class RecycleLeftoverAnalysis
{
    private static readonly HashSet<string> NuGetLayoutDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_content", "_manifest", "_rels", "analyzers", "build", "buildMultiTargeting", "buildTransitive",
        "content", "contentFiles", "docs", "images", "include", "lib", "licenses", "native", "notices",
        "package", "ref", "runtimes", "sdk", "tools"
    };

    public static RecycleLeftoverKind Classify(DiskAccess.RecycleLeftover entry, CancellationToken token)
    {
        if (!entry.IsDirectory)
            return GroupFile(entry.Name);
        if (entry.Contents is not { } tree) return RecycleLeftoverKind.Other;
        var root = tree.EnumerateChildren(tree.RootIndex).Select(tree.Entry).ToArray();
        var packages = root.FirstOrDefault(item => item.IsDirectory && Named(item, "packages"));
        if (entry.Name.EndsWith(".nuget", StringComparison.OrdinalIgnoreCase) && packages.IsDirectory)
            return entry.CanDelete && root.Length == 1 && HasNuGetCacheLayout(tree, packages.FileIndex, token)
                ? RecycleLeftoverKind.NuGetPackages : RecycleLeftoverKind.NuGetData;
        if (entry.Name.EndsWith(".rustup", StringComparison.OrdinalIgnoreCase)
            && root.Any(item => item.IsDirectory && Named(item, "toolchains"))
            && root.Any(item => !item.IsDirectory && Named(item, "settings.toml") || item.IsDirectory && Named(item, "update-hashes")))
            return RecycleLeftoverKind.Rustup;
        if (root.Any(item => item.IsDirectory && Named(item, "caches"))
            && root.Any(item => item.IsDirectory && Named(item, "compile-server"))
            && root.Any(item => item.IsDirectory && Named(item, "index"))
            && root.Any(item => item.IsDirectory && (Named(item, "LocalHistory") || Named(item, "global-model-cache"))))
            return RecycleLeftoverKind.IdeData;
        if (root.Where(item => item.IsDirectory && (Named(item, "google_apis") || Named(item, "google_apis_playstore")))
            .Any(variant => tree.EnumerateChildren(variant.FileIndex).Select(tree.Entry)
                .Any(abi => abi.IsDirectory && IsAndroidImage(tree, abi.FileIndex))))
            return RecycleLeftoverKind.AndroidImages;
        return RecycleLeftoverKind.Other;
    }

    public static IReadOnlyList<ScanEntry> LargestContents(DiskAccess.RecycleLeftover entry, int limit)
    {
        if (entry.Contents is not { } tree || limit <= 0) return [];
        var largest = new PriorityQueue<int, long>(limit);
        foreach (int link in tree.EnumerateChildren(tree.RootIndex))
        {
            long bytes = tree.AttributedBytes(link);
            if (largest.Count < limit) largest.Enqueue(link, bytes);
            else if (largest.TryPeek(out _, out long smallest) && bytes > smallest) largest.EnqueueDequeue(link, bytes);
        }
        return largest.UnorderedItems.Select(item => tree.Entry(item.Element))
            .OrderByDescending(item => item.AttributedBytes(tree.HasAllocationData))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasNuGetCacheLayout(ScanSnapshot tree, int packages, CancellationToken token)
    {
        var ids = tree.EnumerateChildren(packages);
        if (!ids.Any()) return false;
        foreach (int idLink in ids)
        {
            token.ThrowIfCancellationRequested();
            var id = tree.Entry(idLink);
            if (!id.IsDirectory) return false;
            var versions = tree.EnumerateChildren(id.FileIndex);
            if (!versions.Any()) return false;
            foreach (int versionLink in versions)
            {
                var version = tree.Entry(versionLink);
                if (!version.IsDirectory) return false;
                string packageName = id.Name + "." + version.Name + ".nupkg";
                bool metadata = false, archive = false;
                foreach (int itemLink in tree.EnumerateChildren(version.FileIndex))
                {
                    token.ThrowIfCancellationRequested();
                    var item = tree.Entry(itemLink);
                    if (item.IsDirectory)
                    {
                        if (!NuGetLayoutDirectories.Contains(item.Name)) return false;
                        continue;
                    }
                    metadata |= Named(item, ".nupkg.metadata");
                    archive |= Named(item, packageName);
                    if (!IsNuGetVersionFile(item.Name, packageName, id.Name)) return false;
                }
                if (!metadata || !archive) return false;
            }
        }
        return true;
    }

    private static bool IsNuGetVersionFile(string name, string archive, string id) =>
        name.Equals(".nupkg.metadata", StringComparison.OrdinalIgnoreCase)
        || name.Equals(archive, StringComparison.OrdinalIgnoreCase)
        || name.Equals(archive + ".sha512", StringComparison.OrdinalIgnoreCase)
        || name.Equals(id + ".nuspec", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".signature.p7s", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("license", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("licence", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("readme", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("notice", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("thirdparty", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".svg" or ".ico"
            && (name.Contains("icon", StringComparison.OrdinalIgnoreCase) || name.Contains("logo", StringComparison.OrdinalIgnoreCase));

    private static bool IsAndroidImage(ScanSnapshot tree, int directory)
    {
        var items = tree.EnumerateChildren(directory).Select(tree.Entry).Where(item => !item.IsDirectory).ToArray();
        return items.Any(item => Named(item, "system.img")) && items.Any(item => Named(item, "package.xml"))
            && items.Any(item => Named(item, "source.properties"));
    }

    private static RecycleLeftoverKind GroupFile(string name)
    {
        // Familiar suffixes can organize a review even when the format registry
        // lists multiple interpretations. These groups never offer bulk checks.
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".heic" or ".mp4" or ".mov" or ".mp3" => RecycleLeftoverKind.Media,
            ".pdf" or ".docx" or ".xlsx" or ".pptx" or ".txt" or ".csv" => RecycleLeftoverKind.Documents,
            ".zip" or ".7z" or ".rar" => RecycleLeftoverKind.Archives,
            _ => FileTypeClassifier.Classify(name) switch
            {
                FileType.Image or FileType.RawPhoto or FileType.Video or FileType.Audio => RecycleLeftoverKind.Media,
                FileType.Document or FileType.StructuredData => RecycleLeftoverKind.Documents,
                FileType.ArchiveOrInstaller => RecycleLeftoverKind.Archives,
                FileType.LogOrTemporary => RecycleLeftoverKind.Logs,
                _ => RecycleLeftoverKind.Other
            }
        };
    }

    private static bool Named(ScanEntry entry, string name) => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
}
