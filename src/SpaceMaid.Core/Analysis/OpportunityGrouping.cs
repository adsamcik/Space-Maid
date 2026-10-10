using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

public sealed record OpportunityGroup(string Key, string Name, string Path, string Kind,
    IReadOnlyList<Opportunity> Locations, long ObservedBytes, string Summary = "");
public sealed record OpportunityGroupSet(IReadOnlyList<OpportunityGroup> Projects, IReadOnlyList<OpportunityGroup> Types);

// Uses the existing inventory only. Git markers establish folder context,
// not repository identity, clean status, branch integration or abandonment.
public static class OpportunityGrouping
{
    public static OpportunityGroupSet Build(ScanSnapshot scan, IReadOnlyList<Opportunity> locations, CancellationToken token = default)
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
            if (directory < 0 || directory >= scan.FileCount || !scan.Records[directory].IsDirectory)
                throw new ArgumentException("A finding must reference a scanned folder.", nameof(locations));
            var owner = FindOwner(directory);
            string path = scan.PathForFile(owner.Directory) ?? scan.RootPath;
            bool other = owner.Kind == "Containing folder" && location.Category is "Package caches" or "Agent files";
            string key = other ? "other:" + location.Category : "folder:" + path;
            if (!projectBuckets.TryGetValue(key, out var bucket))
            {
                string name = other ? location.Category == "Agent files" ? "Coding-agent files" : "Other package caches"
                    : owner.Directory == scan.RootIndex ? scan.RootPath : scan.Entry(scan.PrimaryLink(owner.Directory)).Name;
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
        return new(projects, types);

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
        int Parent(int directory) => directory == scan.RootIndex || scan.FirstLinks[directory] < 0
            ? -1 : scan.Links[scan.FirstLinks[directory]].ParentIndex;
        int Marker(int directory)
        {
            if (markers.TryGetValue(directory, out int value)) return value;
            int visited = 0;
            foreach (int link in scan.Children(directory))
            {
                if ((visited++ & 1023) == 0) token.ThrowIfCancellationRequested();
                var name = scan.Name(link);
                bool isDirectory = scan.Records[scan.Links[link].FileIndex].IsDirectory;
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) value |= isDirectory ? 1 : 2;
                if (!isDirectory && (name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) || name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) || name.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase)
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
                if (!covered) bytes = checked(bytes + (scan.HasAllocationData ? scan.DirectoryAllocated[directory] : scan.DirectoryLogical[directory]));
            }
            return bytes;
        }
    }
}
