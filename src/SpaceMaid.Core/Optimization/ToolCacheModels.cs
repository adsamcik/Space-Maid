namespace SpaceMaid.Core.Optimization;

public enum ToolCacheKind { Npm, Pnpm, NuGet, Cargo, Gradle }
public enum ToolCacheDisposition { Unavailable, Review, ManagedByTool }

public sealed record ToolCacheResource(string Name, string Path, string Evidence, string Restoration);
public sealed record ToolCacheScope(string Name, string Path, ulong Volume, SpaceMaid.Core.Scanning.FileIdentity Identity);

public sealed record ToolCacheObservation(ToolCacheKind Kind, string Name, string Version, string? Root,
    ToolCacheDisposition Disposition, string Status, string Details, string Restoration,
    IReadOnlyList<ToolCacheResource> Resources, bool CanPrepare)
{
    internal ToolCacheLauncher? Launcher { get; init; }
    internal string LauncherStamp { get; init; } = "";
    internal string? StoreBase { get; init; }
    public string? CacheRoot { get; init; }
    public DateTime ObservedAtUtc { get; init; } = DateTime.UtcNow;
    public OptimizationEvidence Evidence => new(OptimizationAction.MaintainToolCache,
        CanPrepare ? OptimizationDisposition.Review : Disposition == ToolCacheDisposition.Unavailable
            ? OptimizationDisposition.Unknown : OptimizationDisposition.Protected,
        Name, Root ?? "", Details, RestorationRequirement.Download, ObservedAtUtc)
    { Producer = Name + " " + Version, CoverageComplete = false };
}

public sealed class ToolCachePlan
{
    public ToolCacheObservation Observation { get; }
    public string Action { get; }
    public string Consequence { get; }
    public string CommandPreview { get; }
    public DateTime PreparedAtUtc { get; }
    public OptimizationEvidence Evidence => Observation.Evidence with { Disposition = OptimizationDisposition.Eligible,
        Reason = "Native launcher, configured scope and supported maintenance contract revalidated; confirmation is still required.",
        CoverageComplete = true, ObservedAtUtc = PreparedAtUtc };
    internal ToolCacheLauncher Launcher { get; }
    internal IReadOnlyList<string> Arguments { get; }
    public IReadOnlyList<ToolCacheScope> Scopes { get; }
    public IReadOnlyList<string> AffectedPaths => Scopes.Select(scope => scope.Path).ToArray();
    public string ConfirmationScopes => string.Join('\n', Scopes.Select(scope => scope.Name + ": " + scope.Path));

    internal ToolCachePlan(ToolCacheObservation observation, ToolCacheLauncher launcher,
        IReadOnlyList<string> arguments, IReadOnlyList<ToolCacheScope> scopes)
    {
        Observation = observation; Launcher = launcher; Arguments = Array.AsReadOnly(arguments.ToArray());
        Scopes = Array.AsReadOnly(scopes.ToArray()); PreparedAtUtc = DateTime.UtcNow;
        Action = observation.Kind == ToolCacheKind.Npm ? "Verify npm cache" : "Prune unreferenced pnpm packages";
        Consequence = observation.Kind == ToolCacheKind.Npm
            ? "npm verifies its cache and removes unneeded or corrupt cache records. No packages are installed and scripts are disabled."
            : "pnpm permanently removes unreferenced store content and cached metadata in the listed locations. Future branch switches or installs may need downloads and private-feed access.";
        CommandPreview = ToolCacheCommand.Display(launcher.Executable, launcher.Prefix.Concat(Arguments));
    }
}

public sealed record ToolCacheRunResult(bool Completed, bool Cancelled, string Summary, string Output, string Error);

public sealed record ToolCacheCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment, TimeSpan Timeout)
{
    public string Preview => Display(Executable, Arguments);
    internal static string Display(string executable, IEnumerable<string> arguments)
        => string.Join(' ', new[] { executable }.Concat(arguments).Select(value => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'));
}
public sealed record ToolCacheCommandResult(int ExitCode, string Output, string Error);

public sealed record ToolCacheLauncher(string Executable, IReadOnlyList<string> Prefix)
{
    internal string Stamp()
    {
        var parts = new List<string>();
        foreach (string path in new[] { Executable }.Concat(Prefix))
        {
            ToolCachePaths.ValidateFile(path);
            using var file = GeneratedReadLease.Open(path);
            var revision = file.Revision;
            parts.Add(path + "|" + revision.Volume + "|" + revision.Identity + "|" + revision.Length
                + "|" + revision.Created + "|" + revision.Written + "|" + revision.Changed);
        }
        return string.Join('\n', parts);
    }

    internal IDisposable Lease()
    {
        var files = new List<GeneratedReadLease>();
        try
        {
            foreach (string path in new[] { Executable }.Concat(Prefix)) files.Add(GeneratedReadLease.Open(path));
            return new LauncherLeases(files);
        }
        catch { foreach (var file in files) file.Dispose(); throw; }
    }
    private sealed class LauncherLeases(List<GeneratedReadLease> files) : IDisposable
    { public void Dispose() { for (int i = files.Count - 1; i >= 0; i--) files[i].Dispose(); } }
}
