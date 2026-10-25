using System.IO;
using System.Text.Json;
using SpaceMaid.Core.Optimization;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ToolCacheTests
{
    [Fact]
    public async Task UnknownToolMajorsCannotPrepareOrExecuteMaintenance()
    {
        using var fixture = new ToolCacheFixture();
        var known = (await fixture.Service.InspectAsync()).Single(r => r.Kind == ToolCacheKind.Npm);
        fixture.NpmVersion = "12.0.0";
        var unknown = (await fixture.Service.InspectAsync()).Single(r => r.Kind == ToolCacheKind.Npm);
        Assert.False(unknown.CanPrepare); Assert.Contains("supported native maintenance contract", unknown.Details);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(known));
        Assert.DoesNotContain(fixture.Commands, c => c.Arguments.Contains("verify"));
    }

    [Fact]
    public async Task InspectionOnlyQueriesNativeLocationsAndKeepsUnsupportedMaintenanceProtected()
    {
        using var fixture = new ToolCacheFixture();
        var rows = await fixture.Service.InspectAsync();
        Assert.Equal(5, rows.Count);
        Assert.Equal(fixture.NpmRoot, rows.Single(r => r.Kind == ToolCacheKind.Npm).Root);
        Assert.True(rows.Single(r => r.Kind == ToolCacheKind.Npm).CanPrepare);
        Assert.True(rows.Single(r => r.Kind == ToolCacheKind.Pnpm).CanPrepare);
        var nuget = rows.Single(r => r.Kind == ToolCacheKind.NuGet);
        Assert.False(nuget.CanPrepare); Assert.Contains("up-to-date restores", nuget.Details);
        Assert.Equal(4, nuget.Resources.Count); Assert.Equal(OptimizationDisposition.Protected, nuget.Evidence.Disposition);
        Assert.False(rows.Single(r => r.Kind == ToolCacheKind.Cargo).CanPrepare);
        Assert.False(rows.Single(r => r.Kind == ToolCacheKind.Gradle).CanPrepare);
        Assert.DoesNotContain(fixture.Commands, c => c.Arguments.Contains("verify") || c.Arguments.Contains("prune") || c.Arguments.Contains("clean"));
        Assert.All(fixture.Commands, c =>
        {
            Assert.DoesNotContain("cmd.exe", c.Executable, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("true", c.Environment["NPM_CONFIG_IGNORE_SCRIPTS"]);
            Assert.Equal("true", c.Environment["NPM_CONFIG_OFFLINE"]);
            Assert.Equal("0", c.Environment["COREPACK_ENABLE_NETWORK"]);
            Assert.Equal("", c.Environment["NODE_OPTIONS"]);
        });
        Assert.All(fixture.Commands.Where(c => c.Executable == fixture.Launcher(ToolCacheKind.Pnpm).Executable), c =>
        {
            Assert.Contains("--ignore-workspace", c.Arguments);
            Assert.Contains("--ignore-pnpmfile", c.Arguments);
        });
    }

    [Fact]
    public async Task NpmMaintenanceRevalidatesItsConfiguredRootAndUsesOnlyFixedOfflineArguments()
    {
        using var fixture = new ToolCacheFixture();
        var row = (await fixture.Service.InspectAsync()).Single(r => r.Kind == ToolCacheKind.Npm);
        var plan = await fixture.Service.PrepareAsync(row);
        Assert.Equal(OptimizationDisposition.Eligible, plan.Evidence.Disposition);
        Assert.Contains(fixture.NpmRoot, plan.CommandPreview);
        var result = await fixture.Service.ExecuteAsync(plan);
        Assert.True(result.Completed); Assert.False(result.Cancelled);
        var mutation = Assert.Single(fixture.Commands, c => c.Arguments.Contains("verify"));
        Assert.Equal(new[] { "--global", "--cache", fixture.NpmRoot, "--offline", "--ignore-scripts", "cache", "verify" }, mutation.Arguments);
        Assert.Equal(TimeSpan.FromMinutes(2), mutation.Timeout);
        Assert.True(fixture.Commands.Count(c => c.Arguments.Contains("cache") && c.Arguments.Contains("get")) >= 3);
    }

    [Fact]
    public async Task ChangedRootLauncherAndReplacedNativeCacheCannotWidenAPlan()
    {
        using var fixture = new ToolCacheFixture();
        var row = (await fixture.Service.InspectAsync()).Single(r => r.Kind == ToolCacheKind.Npm);
        string other = Directory.CreateDirectory(Path.Join(fixture.Path, "other")).FullName;
        Directory.CreateDirectory(Path.Join(other, "_cacache")); fixture.ReportedNpmRoot = other;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(row));
        fixture.ReportedNpmRoot = fixture.NpmRoot;
        File.AppendAllText(fixture.Launcher(ToolCacheKind.Npm).Executable, "modified launcher");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(row));
        row = (await fixture.Service.InspectAsync()).Single(r => r.Kind == ToolCacheKind.Npm);
        var plan = await fixture.Service.PrepareAsync(row);
        string replacementBackup = fixture.NpmRoot + "-original";
        Assert.StartsWith(fixture.Path + Path.DirectorySeparatorChar, Path.GetFullPath(replacementBackup), StringComparison.OrdinalIgnoreCase);
        Directory.Move(fixture.NpmRoot, replacementBackup);
        Directory.CreateDirectory(Path.Join(fixture.NpmRoot, "_cacache"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecuteAsync(plan));
        Assert.DoesNotContain(fixture.Commands, c => c.Arguments.Contains("verify"));
    }

    [Fact]
    public async Task PnpmBindsTheStoreBaseWithoutAppendingTheVersionTwiceAndChecksIntegrity()
    {
        using var fixture = new ToolCacheFixture();
        var row = (await fixture.Service.InspectAsync()).Single(r => r.Kind == ToolCacheKind.Pnpm);
        var plan = await fixture.Service.PrepareAsync(row);
        var result = await fixture.Service.ExecuteAsync(plan);
        Assert.True(result.Completed);
        var prune = Assert.Single(fixture.Commands, c => c.Arguments.Contains("prune"));
        Assert.Equal(new[] { "--ignore-workspace", "--ignore-pnpmfile", "--store-dir", fixture.PnpmBase, "store", "prune" }, prune.Arguments);
        Assert.Equal(2, fixture.Commands.Count(c => c.Arguments.Contains("status")));
        Assert.Contains("downloads", plan.Consequence);
        Assert.DoesNotContain(fixture.Commands, c => c.Arguments.Contains("dedupe") || c.Arguments.Contains("install"));
    }

    [Fact]
    public async Task ModifiedOrUnrecognizedPnpmStoresDoNotStartPruning()
    {
        using var fixture = new ToolCacheFixture();
        var row = (await fixture.Service.InspectAsync()).Single(r => r.Kind == ToolCacheKind.Pnpm);
        fixture.ModifiedStore = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(row));
        Assert.DoesNotContain(fixture.Commands, c => c.Arguments.Contains("prune"));
        fixture.ModifiedStore = false;
        fixture.ReportedPnpmRoot = Directory.CreateDirectory(Path.Join(fixture.Path, "unrecognized-store")).FullName;
        var changed = (await fixture.Service.InspectAsync()).Single(r => r.Kind == ToolCacheKind.Pnpm);
        Assert.False(changed.CanPrepare);
        Assert.Equal(ToolCacheDisposition.ManagedByTool, changed.Disposition);
    }

    [Fact]
    public async Task UnsafeConfiguredRootsAndProjectWorkingFoldersFailClosed()
    {
        using var fixture = new ToolCacheFixture();
        fixture.ReportedNpmRoot = Path.GetPathRoot(fixture.Path)!;
        var rows = await fixture.Service.InspectAsync();
        Assert.False(rows.Single(r => r.Kind == ToolCacheKind.Npm).CanPrepare);
        Assert.Equal(ToolCacheDisposition.Unavailable, rows.Single(r => r.Kind == ToolCacheKind.Npm).Disposition);
        fixture.ReportedNpmRoot = fixture.Path;
        Directory.CreateDirectory(Path.Join(fixture.Path, "_cacache"));
        rows = await fixture.Service.InspectAsync();
        Assert.Contains("private inspection folder", rows.Single(r => r.Kind == ToolCacheKind.Npm).Details);
        fixture.ReportedNpmRoot = fixture.NpmRoot;
        File.WriteAllText(Path.Join(fixture.Working, "package.json"), "{}");
        int calls = fixture.Commands.Count;
        rows = await fixture.Service.InspectAsync();
        Assert.Equal(calls, fixture.Commands.Count);
        Assert.All(rows.Where(r => r.Kind != ToolCacheKind.Gradle), r => Assert.False(r.CanPrepare));
        Assert.Throws<InvalidDataException>(() => ToolCacheService.ParsePath("C:\\one\nC:\\two"));
    }

    [Fact]
    public async Task CancellationAndNativeFailureRetainUncertainOutcomeInsteadOfClaimingReclaimedBytes()
    {
        using var fixture = new ToolCacheFixture();
        var row = (await fixture.Service.InspectAsync()).Single(r => r.Kind == ToolCacheKind.Npm);
        var plan = await fixture.Service.PrepareAsync(row);
        fixture.FailMutation = true;
        var failed = await fixture.Service.ExecuteAsync(plan);
        Assert.False(failed.Completed); Assert.Contains("already have changed", failed.Summary);
        fixture.FailMutation = false; fixture.StallMutation = true;
        using var work = new CancellationTokenSource();
        var task = fixture.Service.ExecuteAsync(plan, work.Token);
        await fixture.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); work.Cancel();
        var cancelled = await task;
        Assert.True(cancelled.Cancelled); Assert.False(cancelled.Completed);
        Assert.Contains("not undone", cancelled.Summary);
    }

    [Fact]
    public async Task CargoAndGradleExposeExactObservedVariantsWithoutEvaluatingOrDeletingThem()
    {
        using var fixture = new ToolCacheFixture();
        string project = Directory.CreateDirectory(Path.Join(fixture.Path, "project")).FullName;
        File.WriteAllText(Path.Join(project, "Cargo.toml"), "[package]\nname='fixture'\nversion='0.1.0'\n");
        string config = Directory.CreateDirectory(Path.Join(project, ".cargo")).FullName;
        string relocated = Directory.CreateDirectory(Path.Join(fixture.Path, "shared-target")).FullName;
        Directory.CreateDirectory(Path.Join(relocated, "release", ".fingerprint"));
        Directory.CreateDirectory(Path.Join(relocated, "aarch64-pc-windows-msvc", "debug", ".fingerprint"));
        File.WriteAllText(Path.Join(config, "config.toml"), "[build]\ntarget-dir = " + JsonSerializer.Serialize(relocated));
        File.WriteAllText(Path.Join(project, "build.gradle.kts"), "// must never be evaluated");
        Directory.CreateDirectory(Path.Join(project, "build"));
        Directory.CreateDirectory(Path.Join(fixture.GradleRoot, "caches", "8.12"));
        var rows = await fixture.Service.InspectAsync(project);
        var cargo = rows.Single(r => r.Kind == ToolCacheKind.Cargo);
        Assert.Equal(2, cargo.Resources.Count);
        Assert.Contains(cargo.Resources, r => r.Path == Path.Join(relocated, "release"));
        Assert.Contains(cargo.Resources, r => r.Path == Path.Join(relocated, "aarch64-pc-windows-msvc", "debug"));
        Assert.Contains(rows.Single(r => r.Kind == ToolCacheKind.Gradle).Resources, r => r.Path == Path.Join(project, "build"));
        Assert.DoesNotContain(fixture.Commands, c => c.Arguments.Contains("metadata") || c.Arguments.Contains("build") || c.Arguments.Contains("clean"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(cargo));
    }

    [Fact]
    public async Task OlderCargoVersionsDoNotClaimSupportedAutomaticGcAndComplexConfigStaysUnknown()
    {
        using var fixture = new ToolCacheFixture(); fixture.CargoVersion = "1.78.0";
        string project = Directory.CreateDirectory(Path.Join(fixture.Path, "project")).FullName;
        File.WriteAllText(Path.Join(project, "Cargo.toml"), "[package]\n");
        Directory.CreateDirectory(Path.Join(project, ".cargo"));
        File.WriteAllText(Path.Join(project, ".cargo", "config.toml"), "[build]\ntarget-dir={ complex=true }\n");
        var row = (await fixture.Service.InspectAsync(project)).Single(r => r.Kind == ToolCacheKind.Cargo);
        Assert.Contains("does not establish stable", row.Details);
        Assert.Contains("ambiguous", Assert.Single(row.Resources).Evidence);
        Assert.False(row.CanPrepare);
    }
}

internal sealed class ToolCacheFixture : IDisposable
{
    private readonly TestDirectory _fixture = new();
    private readonly Dictionary<ToolCacheKind, ToolCacheLauncher> _launchers = [];
    internal string Path => _fixture.Path;
    internal string Working { get; }
    internal string NpmRoot { get; }
    internal string PnpmBase { get; }
    internal string PnpmRoot { get; }
    internal string GradleRoot { get; }
    internal string ReportedNpmRoot { get; set; }
    internal string ReportedPnpmRoot { get; set; }
    internal string CargoVersion { get; set; } = "1.90.0";
    internal string NpmVersion { get; set; } = "11.6.0";
    internal bool ModifiedStore { get; set; }
    internal bool FailMutation { get; set; }
    internal bool StallMutation { get; set; }
    internal List<ToolCacheCommand> Commands { get; } = [];
    internal TaskCompletionSource MutationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ToolCacheService Service { get; }
    internal ToolCacheFixture()
    {
        Working = Directory.CreateDirectory(System.IO.Path.Join(Path, "inspection")).FullName;
        NpmRoot = Directory.CreateDirectory(System.IO.Path.Join(Path, "npm-cache")).FullName;
        Directory.CreateDirectory(System.IO.Path.Join(NpmRoot, "_cacache")); ReportedNpmRoot = NpmRoot;
        PnpmBase = Directory.CreateDirectory(System.IO.Path.Join(Path, "pnpm-store")).FullName;
        PnpmRoot = Directory.CreateDirectory(System.IO.Path.Join(PnpmBase, "v10")).FullName;
        Directory.CreateDirectory(System.IO.Path.Join(PnpmRoot, "files")); ReportedPnpmRoot = PnpmRoot;
        GradleRoot = Directory.CreateDirectory(System.IO.Path.Join(Path, "gradle-home")).FullName;
        foreach (var kind in Enum.GetValues<ToolCacheKind>().Where(k => k != ToolCacheKind.Gradle))
        {
            string file = System.IO.Path.Join(Path, kind + ".exe"); File.WriteAllText(file, "fixture executable; never launched");
            _launchers.Add(kind, new(file, Array.Empty<string>()));
        }
        Service = new(Working, kind => _launchers.GetValueOrDefault(kind), RunAsync,
            name => name == "GRADLE_USER_HOME" ? GradleRoot : name == "CARGO_HOME" ? Path : null, Path);
    }
    internal ToolCacheLauncher Launcher(ToolCacheKind kind) => _launchers[kind];
    private async Task<ToolCacheCommandResult> RunAsync(ToolCacheCommand command, CancellationToken token)
    {
        Commands.Add(command);
        var kind = _launchers.Single(pair => pair.Value.Executable == command.Executable).Key;
        var args = command.Arguments;
        if (args.Contains("--version")) return new(0, kind switch { ToolCacheKind.Cargo => "cargo " + CargoVersion + " (fixture)", ToolCacheKind.NuGet => "NuGet Command Line\n7.0.0.1", ToolCacheKind.Npm => NpmVersion, _ => "10.18.0" }, "");
        if (args.Contains("get")) return new(0, JsonSerializer.Serialize(ReportedNpmRoot), "");
        if (args.Contains("path")) return new(0, ReportedPnpmRoot, "");
        if (args.Contains("status")) return new(ModifiedStore ? 1 : 0, "", ModifiedStore ? "Store package was modified" : "");
        if (args.Contains("locals")) return new(0, string.Join('\n', new[] { "global-packages", "http-cache", "temp", "plugins-cache" }
            .Select(name => name + ": " + System.IO.Path.Join(Path, "NuGet", name))), "");
        if (args.Contains("verify") || args.Contains("prune"))
        {
            MutationStarted.TrySetResult();
            if (StallMutation) await Task.Delay(Timeout.Infinite, token);
            return new(FailMutation ? 1 : 0, "Native fixture report", FailMutation ? "Owner declined maintenance" : "");
        }
        throw new InvalidOperationException("Unexpected native command in fixture: " + command.Preview);
    }
    public void Dispose() => _fixture.Dispose();
}
