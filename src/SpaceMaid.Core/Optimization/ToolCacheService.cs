using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpaceMaid.Core.Optimization;

// Fixed native adapters. Inspection never runs repository builds, shell
// wrappers, package installers, custom tasks or arbitrary project commands.
public sealed class ToolCacheService
{
    private readonly string _workingDirectory;
    private readonly Func<ToolCacheKind, ToolCacheLauncher?> _resolve;
    private readonly Func<ToolCacheCommand, CancellationToken, Task<ToolCacheCommandResult>> _run;
    private readonly Func<string, string?> _environment;
    private readonly string _userProfile;

    public ToolCacheService(string workingDirectory,
        Func<ToolCacheKind, ToolCacheLauncher?>? resolve = null,
        Func<ToolCacheCommand, CancellationToken, Task<ToolCacheCommandResult>>? run = null,
        Func<string, string?>? environment = null, string? userProfile = null)
    {
        _workingDirectory = OptimizationPolicy.NormalizePath(workingDirectory);
        Directory.CreateDirectory(_workingDirectory);
        _resolve = resolve ?? ToolCacheCommandRunner.Resolve; _run = run ?? ToolCacheCommandRunner.RunAsync;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public async Task<IReadOnlyList<ToolCacheObservation>> InspectAsync(string? projectScope = null, CancellationToken token = default)
    {
        var rows = new List<ToolCacheObservation>();
        foreach (ToolCacheKind kind in Enum.GetValues<ToolCacheKind>())
        {
            token.ThrowIfCancellationRequested();
            rows.Add(await InspectOneAsync(kind, projectScope, token).ConfigureAwait(false));
        }
        return rows.AsReadOnly();
    }

    public async Task<ToolCachePlan> PrepareAsync(ToolCacheObservation selected, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (selected.Kind is not (ToolCacheKind.Npm or ToolCacheKind.Pnpm))
            throw new InvalidOperationException("This cache remains managed by its owner. No selective maintenance contract is available.");
        var fresh = await InspectOneAsync(selected.Kind, null, token).ConfigureAwait(false);
        RequireSame(selected, fresh);
        using var lease = ToolCachePaths.Lease(fresh.Root!);
        using var cacheLease = fresh.Kind == ToolCacheKind.Pnpm ? ToolCachePaths.Lease(fresh.CacheRoot!) : null;
        if (fresh.Kind == ToolCacheKind.Pnpm)
            await CheckPnpmAsync(fresh, token).ConfigureAwait(false);
        var arguments = ActionArguments(fresh);
        var scopes = new List<ToolCacheScope> { new(fresh.Kind == ToolCacheKind.Pnpm ? "Package store" : "Cache", fresh.Root!, lease.Volume, lease.Identity) };
        if (cacheLease is not null) scopes.Add(new("Metadata cache", fresh.CacheRoot!, cacheLease.Volume, cacheLease.Identity));
        return new(fresh, fresh.Launcher!, arguments, scopes);
    }

    public async Task<ToolCacheRunResult> ExecuteAsync(ToolCachePlan plan, CancellationToken token = default,
        Action<string, object>? diagnostic = null)
    {
        // A plan binds every affected scope and expires; native maintenance can alter
        // many records and is intentionally separate from automatic byte caps.
        if (DateTime.UtcNow - plan.PreparedAtUtc > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("The maintenance preview expired. Inspect and review it again.");
        var fresh = await InspectOneAsync(plan.Observation.Kind, null, token).ConfigureAwait(false);
        RequireSame(plan.Observation, fresh);
        using var launcherLease = plan.Launcher.Lease();
        if (plan.Launcher.Stamp() != fresh.LauncherStamp)
            throw new InvalidOperationException("The native launcher changed before its stable lease could be acquired.");
        if (!ActionArguments(fresh).SequenceEqual(plan.Arguments, StringComparer.Ordinal))
            throw new InvalidOperationException("The native maintenance contract changed. Review again.");
        using var lease = ToolCachePaths.Lease(fresh.Root!);
        using var cacheLease = fresh.Kind == ToolCacheKind.Pnpm ? ToolCachePaths.Lease(fresh.CacheRoot!) : null;
        if (plan.Scopes.Count != (cacheLease is null ? 1 : 2)
            || plan.Scopes[0].Path != fresh.Root || lease.Identity != plan.Scopes[0].Identity || lease.Volume != plan.Scopes[0].Volume
            || cacheLease is not null && (plan.Scopes[1].Path != fresh.CacheRoot || cacheLease.Identity != plan.Scopes[1].Identity || cacheLease.Volume != plan.Scopes[1].Volume))
            throw new InvalidOperationException("The cache folder was replaced after review. No maintenance started.");
        if (fresh.Kind == ToolCacheKind.Pnpm) await CheckPnpmAsync(fresh, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        diagnostic?.Invoke("tool-cache-maintenance-start", new { Tool = fresh.Kind.ToString(), plan.Scopes, plan.Action, plan.CommandPreview });
        try
        {
            var result = await RunAsync(plan.Launcher, plan.Arguments, token, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            bool completed = result.ExitCode == 0;
            string summary = completed
                ? plan.Action + " completed. The native tool's report is shown below; drive space was not independently measured."
                : "The native tool did not confirm completion. Some records may already have changed; inspect again before retrying.";
            diagnostic?.Invoke("tool-cache-maintenance-result", new { Tool = fresh.Kind.ToString(), result.ExitCode, Completed = completed });
            return new(completed, false, summary, result.Output, result.Error);
        }
        catch (Exception error) when (Expected(error) || error is OperationCanceledException)
        {
            diagnostic?.Invoke("tool-cache-maintenance-uncertain", new { Tool = fresh.Kind.ToString(), Error = error.Message });
            return new(false, token.IsCancellationRequested,
                "Maintenance stopped. Completed native changes are not undone; inspect again before retrying.", "", error.Message);
        }
    }

    private async Task<ToolCacheObservation> InspectOneAsync(ToolCacheKind kind, string? scope, CancellationToken token)
    {
        string name = kind switch { ToolCacheKind.Npm => "npm", ToolCacheKind.Pnpm => "pnpm", ToolCacheKind.NuGet => "NuGet", _ => kind.ToString() };
        try
        {
            if (kind == ToolCacheKind.Gradle) return InspectGradle(scope);
            var launcher = _resolve(kind);
            if (launcher is null) return Unavailable(kind, name,
                "No supported direct launcher found. Shell/Corepack download shims are not run; use an installed native tool or Node CLI.");
            string stamp = launcher.Stamp();
            string version = ParseVersion((await QueryAsync(launcher,
                kind == ToolCacheKind.NuGet ? ["nuget", "--version"] : kind == ToolCacheKind.Npm ? ["--global", "--version"]
                    : kind == ToolCacheKind.Pnpm ? ["--ignore-workspace", "--config.ignore-pnpmfile=true", "--version"] : ["--version"], token).ConfigureAwait(false)).Output);
            if (version.Length == 0) throw new InvalidDataException("The installed tool did not report a recognizable version.");
            int.TryParse(version.Split('.')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major);
            if (kind == ToolCacheKind.Npm && major is < 9 or > 11 || kind == ToolCacheKind.Pnpm && major is < 9 or > 10)
                return Unavailable(kind, name, "This tool version is outside the supported native maintenance contract. Use its own cache tools after review.") with { Version = version };
            ToolCacheObservation observed;
            if (kind == ToolCacheKind.Npm)
            {
                string root = ToolCachePaths.Root(ParsePath((await QueryAsync(launcher, ["--global", "config", "get", "cache", "--json"], token).ConfigureAwait(false)).Output));
                CheckSeparateScope(root);
                bool marker = Directory.Exists(Path.Join(root, "_cacache"));
                observed = new(kind, name, version, root, marker ? ToolCacheDisposition.Review : ToolCacheDisposition.ManagedByTool,
                    marker ? "Native verification available" : "No npm cache marker",
                    "Configured root reported by npm. Verification uses npm's own cache integrity and garbage collection rules. It does not run dependency dedupe or install packages.",
                    "Removed cache records may need downloads on a later install. Offline operation is preserved for this maintenance command.", [], marker);
            }
            else if (kind == ToolCacheKind.Pnpm)
            {
                string root = ToolCachePaths.Root(ParsePath((await QueryAsync(launcher, ["--ignore-workspace", "--config.ignore-pnpmfile=true", "store", "path"], token).ConfigureAwait(false)).Output));
                CheckSeparateScope(root);
                string cacheReport = (await QueryAsync(launcher,
                    ["--ignore-workspace", "--config.ignore-pnpmfile=true", "config", "get", "cacheDir", "--json"], token).ConfigureAwait(false)).Output;
                if (string.IsNullOrWhiteSpace(cacheReport) || cacheReport.Trim() is "null" or "undefined")
                    return new(kind, name, version, root, ToolCacheDisposition.ManagedByTool, "Metadata cache needs a location",
                        "pnpm did not report its metadata cache location. Configure an explicit cache-dir with pnpm, then inspect again. Pruning is unavailable until both locations can be checked.",
                        "Store pruning can remove both package content and cached registry metadata.", [], false);
                string cacheRoot = ToolCachePaths.Root(ParsePath(cacheReport));
                CheckSeparateScope(cacheRoot);
                if (OptimizationPolicy.Covers(root, cacheRoot) || OptimizationPolicy.Covers(cacheRoot, root))
                    throw new InvalidOperationException("The pnpm store and metadata cache overlap; native pruning requires separate locations.");
                bool versioned = Regex.IsMatch(Path.GetFileName(root), @"^v[0-9]+$", RegexOptions.CultureInvariant);
                string? storeBase = versioned ? Path.GetDirectoryName(root) : null;
                bool marker = versioned && Directory.Exists(Path.Join(root, "files"));
                if (marker)
                {
                    string bound = ToolCachePaths.Root(ParsePath((await QueryAsync(launcher,
                        ["--ignore-workspace", "--config.ignore-pnpmfile=true", "--config.cache-dir=" + cacheRoot, "--store-dir", storeBase!, "store", "path"], token).ConfigureAwait(false)).Output));
                    marker = string.Equals(bound, root, StringComparison.OrdinalIgnoreCase);
                    string boundCache = ToolCachePaths.Root(ParsePath((await QueryAsync(launcher,
                        ["--ignore-workspace", "--config.ignore-pnpmfile=true", "--config.cache-dir=" + cacheRoot, "config", "get", "cacheDir", "--json"], token).ConfigureAwait(false)).Output));
                    marker &= string.Equals(boundCache, cacheRoot, StringComparison.OrdinalIgnoreCase);
                }
                observed = new(kind, name, version, root, marker ? ToolCacheDisposition.Review : ToolCacheDisposition.ManagedByTool,
                    marker ? "Native pruning available" : "Store layout needs review",
                    "Configured store and metadata cache reported by pnpm. Pruning removes unreferenced store content and cached registry metadata. Dependencies and lockfiles are not rewritten.",
                    "Future installs or branch switches may require removed packages again, including downloads and private-feed credentials.",
                    [new("Metadata cache", cacheRoot, "Native prune also removes cached metadata here.", "Registry metadata may need downloading again.")], marker)
                    { StoreBase = storeBase, CacheRoot = cacheRoot };
            }
            else if (kind == ToolCacheKind.NuGet)
            {
                string report = (await QueryAsync(launcher, ["nuget", "locals", "all", "--list"], token).ConfigureAwait(false)).Output;
                var resources = ParseNuGetRoots(report);
                observed = new(kind, name, version, resources.FirstOrDefault(r => r.Name == "global-packages")?.Path,
                    ToolCacheDisposition.ManagedByTool, "Selective removal protected",
                    "Native configured locations. Old .nupkg.metadata timestamps do not prove non-use: up-to-date restores do not refresh them. Selective eviction needs NuGet's concurrency locks and completion-marker ordering; no age sweep is offered.",
                    "Restoring removed packages may require the original feed, credentials and network. Offline/private packages may be irreplaceable.", resources, false);
            }
            else
            {
                string? home = ExistingRoot(_environment("CARGO_HOME") ?? Path.Join(_userProfile, ".cargo"));
                var resources = ToolCacheProjectResources.Cargo(scope, _environment, home);
                bool nativeGc = System.Version.TryParse(version.Split('-')[0].Split('+')[0], out var parsed) && parsed >= new Version(1, 88, 0);
                observed = new(kind, name, version, home, ToolCacheDisposition.ManagedByTool, "Native cache lifecycle",
                    (nativeGc ? "This Cargo version supports automatic global-cache GC with normal online build/fetch activity; GC is suppressed offline. "
                        : "This Cargo version does not establish stable automatic global-cache GC support. ")
                    + "No project evaluation, build, broad cargo clean or unstable GC command is run here. "
                    + (resources.Count > 0 ? "Observed output variants below need producer evidence before deletion." : "Select a Cargo project folder to inspect known output variants."),
                    "Global-cache eviction can need downloads; intermediate builds need compatible tools and inputs. Internal build layouts vary by Cargo version.", resources, false);
            }
            if (launcher.Stamp() != stamp) throw new InvalidOperationException("The native launcher changed during inspection.");
            return observed with { Launcher = launcher, LauncherStamp = stamp };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (Expected(error)) { return Unavailable(kind, name, error.Message); }
    }

    private ToolCacheObservation InspectGradle(string? scope)
    {
        string configured = _environment("GRADLE_USER_HOME") ?? Path.Join(_userProfile, ".gradle");
        string? root = ExistingRoot(configured);
        var resources = ToolCacheProjectResources.Gradle(scope, root);
        return new(ToolCacheKind.Gradle, "Gradle", "Not evaluated", root,
            root is null ? ToolCacheDisposition.Unavailable : ToolCacheDisposition.ManagedByTool,
            root is null ? "No observed Gradle home" : "Native retention policy",
            "Environment/default home only: a per-invocation Gradle option can override it. Gradle maintains generated and downloaded caches with separate retention and locks. Custom init scripts are not evaluated, changed or executed. "
            + (root is not null && Directory.Exists(Path.Join(root, "init.d")) ? "Init scripts are present; effective policy remains owner-controlled." : "No background Gradle command is started."),
            "Generated data costs rebuild time; downloaded resources can require network and credentials. Existing native policy remains in control.", resources, false);
    }

    private async Task CheckPnpmAsync(ToolCacheObservation observation, CancellationToken token)
    {
        var status = await RunAsync(observation.Launcher!, PnpmArguments(observation, "status"), token,
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (status.ExitCode != 0) throw new InvalidOperationException("pnpm did not verify its store as unchanged. Pruning is kept unavailable. " + Compact(status.Error));
    }

    private Task<ToolCacheCommandResult> QueryAsync(ToolCacheLauncher launcher, IReadOnlyList<string> args, CancellationToken token)
        => CheckedQueryAsync(launcher, args, token);
    private async Task<ToolCacheCommandResult> CheckedQueryAsync(ToolCacheLauncher launcher, IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await RunAsync(launcher, args, token, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("Native inspection failed. " + Compact(result.Error));
        return result;
    }

    private async Task<ToolCacheCommandResult> RunAsync(ToolCacheLauncher launcher, IReadOnlyList<string> args, CancellationToken token, TimeSpan timeout)
    {
        // A neutral host-owned directory keeps repository package-manager config
        // and project-pinned auto-download behavior out of discovery.
        using var workingLease = ToolCachePaths.Lease(_workingDirectory);
        if (new[] { "package.json", ".npmrc", "pnpm-workspace.yaml", ".pnpmfile.cjs", ".pnpmfile.mjs" }
            .Any(file => File.Exists(Path.Join(_workingDirectory, file))))
            throw new InvalidOperationException("The inspection working folder contains project configuration; choose a neutral host folder.");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NODE_OPTIONS"] = "", ["NODE_PATH"] = "", ["NPM_CONFIG_IGNORE_SCRIPTS"] = "true", ["NPM_CONFIG_OFFLINE"] = "true",
            ["NPM_CONFIG_UPDATE_NOTIFIER"] = "false", ["NPM_CONFIG_AUDIT"] = "false", ["NPM_CONFIG_FUND"] = "false",
            ["NPM_CONFIG_MANAGE_PACKAGE_MANAGER_VERSIONS"] = "false", ["PNPM_CONFIG_MANAGE_PACKAGE_MANAGER_VERSIONS"] = "false",
            ["PNPM_CONFIG_IGNORE_PNPMFILE"] = "true", ["PNPM_CONFIG_IGNORE_SCRIPTS"] = "true", ["PNPM_CONFIG_OFFLINE"] = "true",
            ["COREPACK_ENABLE_NETWORK"] = "0",
            ["COREPACK_ENABLE_PROJECT_SPEC"] = "0", ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1", ["CARGO_NET_OFFLINE"] = "true",
            ["RUSTUP_AUTO_INSTALL"] = "0", ["NO_COLOR"] = "1", ["CI"] = "true"
        };
        return await _run(new(launcher.Executable, Array.AsReadOnly(launcher.Prefix.Concat(args).ToArray()),
            _workingDirectory, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(environment), timeout), token).ConfigureAwait(false);
    }

    private static string[] ActionArguments(ToolCacheObservation observation) => observation.Kind switch
    {
        ToolCacheKind.Npm => ["--global", "--cache", observation.Root!, "--offline", "--ignore-scripts", "cache", "verify"],
        ToolCacheKind.Pnpm => PnpmArguments(observation, "prune"),
        _ => throw new InvalidOperationException("No native maintenance action is supported for this cache.")
    };

    private static string[] PnpmArguments(ToolCacheObservation observation, string action) =>
        ["--ignore-workspace", "--config.ignore-pnpmfile=true", "--config.cache-dir=" + observation.CacheRoot,
            "--store-dir", observation.StoreBase!, "store", action];

    private static void RequireSame(ToolCacheObservation previous, ToolCacheObservation current)
    {
        if (!previous.CanPrepare || !current.CanPrepare || previous.Kind != current.Kind || previous.Version != current.Version
            || !string.Equals(previous.Root, current.Root, StringComparison.OrdinalIgnoreCase)
            || previous.StoreBase != current.StoreBase || previous.CacheRoot != current.CacheRoot || previous.Launcher is null || current.Launcher is null
            || previous.LauncherStamp.Length == 0 || previous.LauncherStamp != current.LauncherStamp
            || previous.Launcher.Stamp() != current.LauncherStamp)
            throw new InvalidOperationException("The installed tool, configured root or supported layout changed. Inspect and review again.");
    }

    private void CheckSeparateScope(string root)
    {
        if (OptimizationPolicy.Covers(root, _workingDirectory) || OptimizationPolicy.Covers(_workingDirectory, root))
            throw new InvalidOperationException("The configured cache overlaps the application's private inspection folder; maintenance is protected.");
    }

    internal static string ParseVersion(string output)
    {
        var match = Regex.Match(output.Trim(), @"(?:^|\s)([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:[-+][A-Za-z0-9.-]+)?)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : "";
    }
    internal static string ParsePath(string output)
    {
        output = output.Trim();
        if (output.StartsWith('"')) output = JsonSerializer.Deserialize<string>(output) ?? "";
        if (output.Length is 0 or > 32767 || output.Contains('\n') || output.Contains('\r')) throw new InvalidDataException("The tool did not report one unambiguous cache path.");
        return OptimizationPolicy.NormalizePath(output);
    }
    internal static IReadOnlyList<ToolCacheResource> ParseNuGetRoots(string output)
    {
        var resources = new List<ToolCacheResource>();
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int split = line.IndexOf(':'); if (split < 0) continue;
            string kind = line[..split].Trim();
            if (kind is not ("global-packages" or "http-cache" or "temp" or "plugins-cache")) continue;
            string path = OptimizationPolicy.NormalizePath(line[(split + 1)..].Trim());
            resources.Add(new(kind, path, "Configured location reported by dotnet nuget locals; usage and removability are not established.",
                "Native restore may require network or private feeds."));
        }
        if (resources.Count == 0) throw new InvalidDataException("NuGet did not report recognizable configured cache locations.");
        return resources.AsReadOnly();
    }
    private static string? ExistingRoot(string path)
    { try { return ToolCachePaths.Root(path); } catch (Exception error) when (Expected(error)) { return null; } }
    private static ToolCacheObservation Unavailable(ToolCacheKind kind, string name, string message)
        => new(kind, name, "Unavailable", null, ToolCacheDisposition.Unavailable, "Unavailable", message, "No maintenance action is authorized.", [], false);
    private static string Compact(string text) => text.Trim().Length <= 600 ? text.Trim() : text.Trim()[..600] + "…";
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception
        or InvalidOperationException or ArgumentException or JsonException or NotSupportedException;
}
