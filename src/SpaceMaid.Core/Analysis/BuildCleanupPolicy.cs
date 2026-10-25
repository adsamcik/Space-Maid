using System.Collections.ObjectModel;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Core.Analysis;

public sealed record BuildCleanupPolicy
{
    public int KeepRecentDays { get; }
    public static BuildCleanupPolicy Default { get; } = new();
    public BuildCleanupPolicy(int KeepRecentDays = 3)
    {
        if (KeepRecentDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(KeepRecentDays), "Use 1–365 days.");
        this.KeepRecentDays = KeepRecentDays;
    }
}

public enum CleanupReadiness { Ready, NeedsReview, Protected }
public sealed record BuildCleanupAssessment(CleanupReadiness State, DateTime? LastActivityUtc, string Reason);

// Creation/write metadata is evidence of activity, not a record of actual use.
// Aggregate only candidate subtrees and direct project files; no disk reads and
// no second volume-sized array. Nested candidates reuse the directory results.
public static class BuildCleanupProtection
{
    private static readonly string[] AuthoredSuffixes = [".cs", ".fs", ".vb", ".py", ".rs", ".java", ".kt", ".cpp", ".h", ".hpp", ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".psd", ".blend"];
    public static IReadOnlyDictionary<int, BuildCleanupAssessment> Assess(ScanSnapshot scan, IReadOnlyList<Opportunity> locations,
        BuildCleanupPolicy policy, CancellationToken token = default, DateTime? nowUtc = null)
    {
        var result = new Dictionary<int, BuildCleanupAssessment>();
        var subtrees = new Dictionary<int, Activity>();
        var parents = new Dictionary<int, Activity>();
        DateTime cutoff = (nowUtc ?? DateTime.UtcNow).ToUniversalTime().AddDays(-policy.KeepRecentDays);
        foreach (var location in locations)
        {
            token.ThrowIfCancellationRequested();
            if ((uint)location.DirectoryIndex >= (uint)scan.FileCount || !scan.Records[location.DirectoryIndex].IsDirectory)
                throw new ArgumentException("A cleanup assessment must reference a scanned folder.", nameof(locations));
            BuildCleanupAssessment assessment;
            if (location.CleanupBlockReason.Length > 0)
                assessment = new(CleanupReadiness.Protected, null, location.CleanupBlockReason);
            else if (!location.IsBuildOutput)
                assessment = new(location.RequiresManualReview ? CleanupReadiness.NeedsReview : CleanupReadiness.Ready, null,
                    location.RequiresManualReview ? "Review contents" : "Ready");
            else
            {
                var activity = Subtree(location.DirectoryIndex);
                int link = scan.PrimaryLink(location.DirectoryIndex);
                int parent = link < 0 ? -1 : scan.Links[link].ParentIndex;
                if (parent >= 0)
                {
                    if (!parents.TryGetValue(parent, out var source))
                    {
                        source = new();
                        foreach (int child in scan.Children(parent))
                        {
                            token.ThrowIfCancellationRequested();
                            int index = scan.Links[child].FileIndex;
                            if (!scan.Records[index].IsDirectory) source = source.Merge(Read(index));
                        }
                        parents.Add(parent, source);
                    }
                    activity = activity.Merge(source);
                }
                assessment = Classify(activity, location.IsRegeneratable, cutoff, policy);
            }
            if (!result.TryGetValue(location.DirectoryIndex, out var prior) || assessment.State > prior.State)
                result[location.DirectoryIndex] = assessment;
        }
        // Selecting a containing cache/environment must not bypass an output's
        // manual-review requirement or recent-activity protection.
        foreach (var location in locations.Where(item => item.IsBuildOutput))
        {
            var nested = result[location.DirectoryIndex];
            if (nested.State == CleanupReadiness.Ready) continue;
            int directory = location.DirectoryIndex, remaining = scan.FileCount;
            while (remaining-- > 0)
            {
                token.ThrowIfCancellationRequested();
                int link = scan.PrimaryLink(directory);
                if (link < 0 || (directory = scan.Links[link].ParentIndex) < 0) break;
                if (result.TryGetValue(directory, out var ancestor) && nested.State > ancestor.State)
                    result[directory] = new(nested.State, nested.LastActivityUtc, nested.State == CleanupReadiness.Protected
                        ? "Contains protected build files · Kept" : "Contains build files needing review");
            }
        }
        return new ReadOnlyDictionary<int, BuildCleanupAssessment>(result);

        Activity Subtree(int root)
        {
            if (subtrees.TryGetValue(root, out var cached)) return cached;
            var pending = new Stack<(int Directory, bool Exit)>();
            var active = new HashSet<int>();
            pending.Push((root, false));
            while (pending.TryPop(out var next))
            {
                token.ThrowIfCancellationRequested();
                if (subtrees.ContainsKey(next.Directory)) continue;
                if (!next.Exit)
                {
                    if (!active.Add(next.Directory)) { subtrees[next.Directory] = new(0, true); continue; }
                    pending.Push((next.Directory, true));
                    foreach (int child in scan.Children(next.Directory))
                    {
                        int index = scan.Links[child].FileIndex;
                        if (scan.Records[index].IsDirectory && !subtrees.ContainsKey(index)) pending.Push((index, false));
                    }
                }
                else
                {
                    var value = Read(next.Directory);
                    if ((scan.EntryFlags[next.Directory] & 6) != 0) value = value with { Unknown = true };
                    foreach (int child in scan.Children(next.Directory))
                    {
                        token.ThrowIfCancellationRequested();
                        int index = scan.Links[child].FileIndex;
                        value = value.Merge(scan.Records[index].IsDirectory ? subtrees.GetValueOrDefault(index, new(0, true)) : Read(index));
                        // Source/project markers in an output folder invalidate
                        // the automatic rebuildable assumption. Generated .g.cs
                        // and similar compiler files are intentionally allowed.
                        if ((!scan.Records[index].IsDirectory && Authored(scan.Name(child)))
                            || scan.Name(child).Equals(".git", StringComparison.OrdinalIgnoreCase)) value = value with { Custom = true };
                    }
                    subtrees[next.Directory] = value; active.Remove(next.Directory);
                }
            }
            return subtrees[root];
        }
        Activity Read(int index)
        {
            var record = scan.Records[index];
            const long maximum = 2650467743999999999; // DateTime.MaxValue as Windows FILETIME.
            bool unknown = record.LastWriteFileTime <= 0 || record.LastWriteFileTime > maximum
                || record.CreationFileTime < 0 || record.CreationFileTime > maximum
                || (record.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0;
            return new(unknown ? 0 : Math.Max(record.LastWriteFileTime, record.CreationFileTime), unknown);
        }
    }

    private readonly record struct Activity(long Latest = 0, bool Unknown = false, bool Custom = false)
    {
        public Activity Merge(Activity other) => new(Math.Max(Latest, other.Latest), Unknown || other.Unknown, Custom || other.Custom);
    }
    private static BuildCleanupAssessment Classify(Activity activity, bool regeneratable, DateTime cutoff, BuildCleanupPolicy policy)
    {
        DateTime? date = activity.Latest > 0 ? DateTime.FromFileTimeUtc(activity.Latest) : null;
        if (activity.Unknown || date is null) return new(CleanupReadiness.Protected, date, "Activity unavailable · Kept");
        if (date >= cutoff) return new(CleanupReadiness.Protected, date, $"Activity within {policy.KeepRecentDays} days · Kept");
        if (!regeneratable || activity.Custom) return new(CleanupReadiness.NeedsReview, date, "Rebuildability needs review");
        return new(CleanupReadiness.Ready, date, "Recognized build output");
    }
    private static bool Authored(ReadOnlySpan<char> name)
    {
        if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string suffix in AuthoredSuffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        return name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) || name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) || name.Equals(".git", StringComparison.OrdinalIgnoreCase);
    }

    // Last-minute checks are scoped to the target, not merged into the full
    // inventory. Builds can become active while a large batch is progressing.
    public static async Task<BuildCleanupAssessment> CheckCurrentAsync(Opportunity location, BuildCleanupPolicy policy,
        CancellationToken token = default)
    {
        var reader = new VolumeScanner();
        var output = await reader.ReadDirectoryAsync(location.Path, true, token, allStreams: false, allowBasic: true).ConfigureAwait(false);
        if (output.WarningCount > 0) return new(CleanupReadiness.Protected, null, "Current activity could not be checked · Kept");
        var current = location with { DirectoryIndex = output.RootIndex, IsBuildOutput = true };
        var assessment = Assess(output, [current], policy, token)[output.RootIndex];
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(location.Path));
        if (parent is not null && assessment.State != CleanupReadiness.Protected)
        {
            var source = await reader.ReadDirectoryAsync(parent, false, token, allStreams: false, allowBasic: true).ConfigureAwait(false);
            if (source.WarningCount > 0) return new(CleanupReadiness.Protected, assessment.LastActivityUtc, "Project activity could not be checked · Kept");
            DateTime cutoff = DateTime.UtcNow.AddDays(-policy.KeepRecentDays);
            foreach (int link in source.Children(source.RootIndex))
            {
                token.ThrowIfCancellationRequested();
                var file = source.Records[source.Links[link].FileIndex];
                if (file.IsDirectory) continue;
                if (file.LastWriteFileTime <= 0 || file.CreationFileTime < 0 || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    return new(CleanupReadiness.Protected, null, "Project activity unavailable · Kept");
                DateTime time;
                try { time = DateTime.FromFileTimeUtc(Math.Max(file.LastWriteFileTime, file.CreationFileTime)); }
                catch (ArgumentOutOfRangeException) { return new(CleanupReadiness.Protected, null, "Project activity unavailable · Kept"); }
                if (time >= cutoff) return new(CleanupReadiness.Protected, time, $"Project activity within {policy.KeepRecentDays} days · Kept");
            }
        }
        return assessment;
    }
}
