using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Apps;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class AppsTests
{
    [Fact]
    public void RegistrationsGroupSharedFoldersWithoutSummingEstimatesAndMarkNestedUsage()
    {
        var groups = AppDiscovery.Group([
            new("Editor", "Vendor", "1", @"G:\Apps\Editor", 100, "Windows installation record"),
            new("Editor", "Vendor", "1", @"G:\Apps\Editor", 120, "Windows installation record"),
            new("Tools", "Vendor", "2", @"G:\Apps\Editor", 200, "Windows installation record"),
            new("Plugin", "Vendor", "3", @"G:\Apps\Editor\Plugin", 30, "Windows installation record"),
            new("Independent", null, null, @"G:\Apps\EditorOther", 40, "App folder"),
            new("Case-sensitive", null, null, @"G:\Apps\editor", 50, "App folder")]);
        Assert.Equal(4, groups.Count);
        var shared = Assert.Single(groups, app => app.Path == @"G:\Apps\Editor");
        Assert.Equal(2, shared.Names.Count); Assert.Null(shared.ReportedBytes); Assert.True(shared.Overlaps);
        Assert.True(Assert.Single(groups, app => app.Name == "Plugin").Overlaps);
        Assert.False(Assert.Single(groups, app => app.Name == "Independent").Overlaps);
        Assert.False(Assert.Single(groups, app => app.Name == "Case-sensitive").Overlaps);
        Assert.Equal(120, Assert.Single(AppDiscovery.Group([
            new("Editor", null, null, @"G:\Apps\Editor", 100, "Windows installation record"),
            new("Editor", null, null, @"G:\Apps\Editor", 120, "Windows installation record")])).ReportedBytes);
    }

    [Fact]
    public void RegistrySizesAndLocationsAreConservative()
    {
        Assert.Equal(1024, AppDiscovery.ReportedSize(1));
        Assert.Equal(4294967295L * 1024, AppDiscovery.ReportedSize(-1));
        Assert.Null(AppDiscovery.ReportedSize(0)); Assert.Null(AppDiscovery.ReportedSize(long.MaxValue));
        Assert.Null(AppDiscovery.NormalizeFolder(@"C:\")); Assert.Null(AppDiscovery.NormalizeFolder(@"C:\Program Files"));
        Assert.Null(AppDiscovery.NormalizeFolder(@"C:\Windows\System32")); Assert.Null(AppDiscovery.NormalizeFolder(@"C:\Apps\.."));
        Assert.Null(AppDiscovery.NormalizeFolder(@"\\server\apps\Tool")); Assert.Null(AppDiscovery.NormalizeFolder("relative"));
        Assert.Equal(@"G:\Apps\Tool", AppDiscovery.NormalizeFolder("\"G:/Apps/Tool/\""));
        using var directory = new TestDirectory();
        string executable = Path.Join(directory.Path, "with,comma.exe"); File.WriteAllBytes(executable, [0]);
        Assert.Equal(directory.Path, AppDiscovery.FolderFromIcon('"' + executable + "\",0"));
        Assert.Equal(directory.Path, AppDiscovery.FolderFromIcon(executable + ",-2"));
        Assert.Null(AppDiscovery.FolderFromIcon(executable + " --run"));
        Assert.Null(AppDiscovery.FolderFromIcon(@"C:\Windows\System32\msiexec.exe,0"));
    }

    [Fact]
    public async Task FolderSizesReuseCurrentScanAndRefreshLockedContentsWithoutReadingThem()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "app")).FullName;
        string file = Path.Join(folder, "app.exe"); await File.WriteAllBytesAsync(file, new byte[5000]);
        ScanSnapshot? scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        bool stale = false; int discoveries = 0;
        var inventory = new ExtensionInventory(() => scan, () => stale, () => { discoveries++; return new Noop(); });
        var cached = await inventory.ReadFolderSizeAsync(folder, false, default);
        Assert.NotNull(cached); Assert.Equal(5000, cached.LogicalBytes); Assert.NotNull(cached.AllocatedBytes);
        Assert.Equal("Drive scan", cached.Source); Assert.Equal(0, discoveries);
        Assert.True(CreateHardLinkW(Path.Join(folder, "alias.exe"), file, 0));
        await File.WriteAllBytesAsync(file, new byte[9000]);
        using var exclusive = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var measured = await inventory.ReadFolderSizeAsync(folder, true, default);
        Assert.NotNull(measured); Assert.Equal(9000, measured.LogicalBytes); Assert.NotNull(measured.AllocatedBytes);
        Assert.Equal(1, discoveries);
        stale = true; Assert.Null(await inventory.ReadFolderSizeAsync(folder, false, default));
        stale = false; scan = null; Assert.Null(await inventory.ReadFolderSizeAsync(folder, false, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inventory.ReadFolderSizeAsync(folder, true, cancelled.Token));
    }

    [Fact]
    public async Task CrossAppHardLinksRequireScopedSizesRatherThanGlobalDriveAttribution()
    {
        using var fixture = new TestDirectory();
        string first = Directory.CreateDirectory(Path.Join(fixture.Path, "first")).FullName;
        string second = Directory.CreateDirectory(Path.Join(fixture.Path, "vendor", "second")).FullName;
        string file = Path.Join(first, "shared.exe"); File.WriteAllBytes(file, new byte[8192]);
        Assert.True(CreateHardLinkW(Path.Join(second, "shared.exe"), file, 0));
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var inventory = new ExtensionInventory(() => scan, () => false, () => new Noop());
        foreach (string path in new[] { first, second, Path.GetDirectoryName(second)!, fixture.Path })
            Assert.Null(await inventory.ReadFolderSizeAsync(path, false, default));
        Assert.Equal(8192, (await inventory.ReadFolderSizeAsync(first, true, default))!.LogicalBytes);
        Assert.Equal(8192, (await inventory.ReadFolderSizeAsync(second, true, default))!.LogicalBytes);
    }

    [Fact]
    public async Task MissingAndLinkedInstallFoldersCannotBecomeZeroByteMeasurements()
    {
        using var fixture = new TestDirectory();
        var inventory = new ExtensionInventory(() => null, () => false, () => new Noop());
        await Assert.ThrowsAsync<FileNotFoundException>(() => inventory.ReadFolderSizeAsync(Path.Join(fixture.Path, "missing"), true, default));
        string target = Directory.CreateDirectory(Path.Join(fixture.Path, "target")).FullName;
        string link = Path.Join(fixture.Path, "link");
        // Junction creation does not require the symbolic-link privilege.
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = $"/c mklink /J \"{link}\" \"{target}\"";
        using var process = Process.Start(start)!; await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        try { await Assert.ThrowsAsync<IOException>(() => inventory.ReadFolderSizeAsync(link, true, default)); }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void RowsKeepUnknownPartialLogicalAndEstimateSizesDistinct()
    {
        var app = new DetectedApp("App", "Vendor", "1", @"G:\Apps\App", 2048, "Windows installation record", ["App"], true);
        var row = new AppRow(app);
        Assert.Equal("Installer estimate", row.Basis); Assert.Equal("2 KiB", row.SizeText);
        row.SetMeasurement(new(0, 0, true, DateTime.UtcNow, "Folder measurement", []));
        Assert.Equal("On disk", row.Basis); Assert.Equal("0 B", row.SizeText);
        row.SetMeasurement(new(4096, null, false, DateTime.UtcNow, "Folder measurement", ["Access denied"]));
        Assert.Equal("File size · partial", row.Basis); Assert.Contains("Access denied", row.Details); Assert.Contains("overlaps", row.Details);
        row.SetMeasurement(null, "Missing folder"); Assert.Equal("Installer estimate", row.Basis); Assert.Equal(2048, row.SizeBytes);
        var unknown = new AppRow(app with { ReportedBytes = null }); Assert.Equal("—", unknown.SizeText);
    }

    // Read-only real-machine receipt; no machine-dependent count assertion.
    [Fact]
    public void LiveInstallationRecordsAreReadWithoutLaunchingApps()
    {
        var warnings = new List<string>(); var timer = Stopwatch.StartNew();
        var records = AppDiscovery.ReadRegistrations(warnings, default);
        Assert.All(records, record => Assert.False(string.IsNullOrWhiteSpace(record.Name)));
        long registryMilliseconds = timer.ElapsedMilliseconds;
        timer.Restart();
        var discovery = AppDiscovery.Discover(Path.GetPathRoot(Environment.SystemDirectory)!, [], default);
        Assert.All(discovery.Apps, app => Assert.NotNull(app.Path));
        Directory.CreateDirectory(Path.Join(Repository(), "artifacts", "apps"));
        File.WriteAllText(Path.Join(Repository(), "artifacts", "apps", "registry-probe.json"), System.Text.Json.JsonSerializer.Serialize(new
        { Records = records.Count, Located = records.Count(record => record.Path is not null), RegistryMilliseconds = registryMilliseconds,
            AppFolders = discovery.Apps.Count, DiscoveryMilliseconds = timer.ElapsedMilliseconds, Warnings = discovery.Warnings }));
    }
    private static string Repository()
    {
        string? configured = Environment.GetEnvironmentVariable("SPACEMAID_TEST_REPOSITORY");
        if (configured is not null)
            return File.Exists(Path.Join(configured, "SpaceMaid.slnx")) ? Path.GetFullPath(configured)
                : throw new DirectoryNotFoundException("SPACEMAID_TEST_REPOSITORY must name the repository root.");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "SpaceMaid.slnx"))) directory = directory.Parent;
        return directory!.FullName;
    }
    private sealed class Noop : IDisposable { public void Dispose() { } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string name, string existing, nint security);
}
