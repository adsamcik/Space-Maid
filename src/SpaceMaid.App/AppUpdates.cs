using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpaceMaid.Core.Scanning;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Sources;

namespace SpaceMaid.App;

// One UI-owned update operation. Only an explicit Download reads package content;
// applying an update is deferred until the host has drained all of its workers.
internal sealed class AppUpdates : IDisposable
{
    private readonly UpdateManager? _manager;
    private readonly string _settingsPath;
    private readonly ScanLog _log;
    private CancellationTokenSource? _operationCancellation;
    private bool _stopped;
    private UpdateInfo? _available;
    private DateTimeOffset? _lastCheck;
    internal event Action? Changed;
    internal Task? Operation { get; private set; }
    internal bool Supported => _manager?.IsInstalled == true;
    internal bool AutomaticChecks { get; private set; } = true;
    internal bool Busy => Operation is not null;
    internal bool Ready { get; private set; }
    internal int Progress { get; private set; }
    internal string CurrentVersion => _manager?.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Development";
    internal string? AvailableVersion => _available?.TargetFullRelease.Version.ToString();
    internal string Status { get; private set; }
    internal string? Error { get; private set; }
    internal bool AutomaticCheckDue => Supported && AutomaticChecks && (!_lastCheck.HasValue
        || DateTimeOffset.UtcNow - _lastCheck.Value >= TimeSpan.FromDays(1));

    internal AppUpdates(UpdateManager? manager, string settingsPath, string logDirectory)
    {
        _manager = manager;
        _settingsPath = settingsPath;
        _log = new(logDirectory);
        Status = Supported ? "Check for updates" : "Updates are available in release installations.";
        try
        {
            if (System.IO.File.Exists(settingsPath))
            {
                if (new FileInfo(settingsPath).Length > 4096) throw new InvalidDataException("Update settings exceed the size limit.");
                var settings = JsonSerializer.Deserialize<UpdateSettings>(System.IO.File.ReadAllText(settingsPath))
                    ?? throw new InvalidDataException("Invalid update settings.");
                AutomaticChecks = settings.AutomaticChecks;
                _lastCheck = settings.LastCheckUtc;
                // A clock change must not disable checking indefinitely.
                if (_lastCheck > DateTimeOffset.UtcNow) _lastCheck = null;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { AutomaticChecks = false; Error = error.Message; _log.Record("update-settings-error", new { error.Message }); }
    }

    internal static AppUpdates Create(string settingsPath, string logDirectory)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var repository = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "GitHubRepository")?.Value;
        UpdateManager? manager = null;
        if (RepositoryUrl(repository) is { } url && VelopackLocator.Current.CurrentlyInstalledVersion is { } version)
            manager = new VerifiedUpdateManager(new GithubSource(url, null, version.IsPrerelease));
        return new(manager, settingsPath, logDirectory);
    }

    internal static string? RepositoryUrl(string? repository) => repository is not null
        && Regex.IsMatch(repository, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant)
        && repository.Split('/')[1] is not "." and not ".."
            ? "https://github.com/" + repository : null;

    internal bool SetAutomaticChecks(bool enabled)
    {
        bool previous = AutomaticChecks;
        AutomaticChecks = enabled;
        if (SaveSettings()) { Notify(); return true; }
        AutomaticChecks = previous; Notify(); return false;
    }

    internal Task CheckAsync() => StartOperation(CheckCoreAsync);
    internal Task DownloadAsync() => StartOperation(DownloadCoreAsync);

    private Task StartOperation(Func<CancellationToken, Task> work)
    {
        if (_stopped || !Supported) return Task.CompletedTask;
        if (Operation is not null) return Operation;
        _operationCancellation = new();
        Operation = RunAsync(work, _operationCancellation.Token);
        Notify();
        return Operation;
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken token)
    {
        await Task.Yield();
        Error = null;
        try { await work(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Status = Ready ? "Ready to restart" : "Cancelled"; _log.Record("update-cancelled", new { }); }
        catch (Exception error)
        { Status = "Update failed · Try again"; Error = error.ToString(); _log.Record("update-error", new { Error }); }
        finally
        {
            _operationCancellation?.Dispose(); _operationCancellation = null;
            Operation = null; Notify();
        }
    }

    private async Task CheckCoreAsync(CancellationToken token)
    {
        Status = "Checking…"; Notify();
        _log.Record("update-check", new { CurrentVersion });
        _lastCheck = DateTimeOffset.UtcNow;
        SaveSettings();
        // Velopack's feed API has no cancellation argument. WaitAsync bounds the UI
        // wait; the late feed result cannot update state or download anything.
        var update = await _manager!.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(30), token);
        token.ThrowIfCancellationRequested();
        if (update is not null) ValidateTarget(update.TargetFullRelease, _manager.AppId);
        if (_available?.TargetFullRelease.SHA256 != update?.TargetFullRelease.SHA256) Ready = false;
        _available = update;
        Status = update is null ? "Up to date" : Ready ? "Ready to restart" : $"Version {AvailableVersion} available";
        _log.Record("update-result", new { AvailableVersion });
    }

    internal static void ValidateTarget(VelopackAsset asset, string? appId)
    {
        if (!string.Equals(asset.PackageId, appId, StringComparison.Ordinal)
            || asset.Type != VelopackAssetType.Full || asset.Size <= 0
            || asset.SHA256 is null || !Regex.IsMatch(asset.SHA256, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)
            || string.IsNullOrWhiteSpace(asset.FileName) || Path.GetFileName(asset.FileName) != asset.FileName
            || asset.FileName.IndexOfAny(['/', '\\', ':']) >= 0 || !asset.FileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The release does not contain a valid Space Maid update package.");
    }

    private async Task DownloadCoreAsync(CancellationToken token)
    {
        if (_available is null) return;
        Ready = false; Progress = 0; Status = "Downloading…"; Notify();
        ValidateTarget(_available.TargetFullRelease, _manager!.AppId);
        var progress = new Progress<int>(value => { if (!_stopped && Operation is not null) { Progress = value; Notify(); } });
        await _manager.DownloadUpdatesAsync(_available, value => ((IProgress<int>)progress).Report(value), token);
        token.ThrowIfCancellationRequested();
        Ready = true; Progress = 100; Status = "Ready to restart";
        _log.Record("update-downloaded", new { AvailableVersion });
    }

    internal async Task ScheduleRestartAsync()
    {
        if (!Ready || _available is null) throw new InvalidOperationException("Download the update first.");
        if (_manager is VerifiedUpdateManager verified) await verified.VerifyPreparedAsync(_available.TargetFullRelease);
        _manager!.WaitExitThenApplyUpdates(_available.TargetFullRelease, silent: false, restart: true);
        _log.Record("update-apply", new { AvailableVersion });
    }

    internal void Cancel() => _operationCancellation?.Cancel();
    internal async Task StopAsync()
    {
        _stopped = true; Cancel();
        if (Operation is { } operation) await operation;
    }

    private bool SaveSettings()
    {
        string temp = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(new UpdateSettings(AutomaticChecks, _lastCheck)));
            System.IO.File.Move(temp, _settingsPath, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Error = error.Message; _log.Record("update-settings-error", new { error.Message }); return false; }
        finally { try { System.IO.File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private void Notify() { if (!_stopped) Changed?.Invoke(); }
    public void Dispose() { _stopped = true; Cancel(); _log.Dispose(); }
    private sealed record UpdateSettings(bool AutomaticChecks, DateTimeOffset? LastCheckUtc);
}

internal sealed class VerifiedUpdateManager(IUpdateSource source, IVelopackLocator? locator = null)
    : UpdateManager(source, locator: locator)
{
    public override async Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress = null, CancellationToken cancelToken = default)
    {
        AppUpdates.ValidateTarget(updates.TargetFullRelease, AppId);
        await base.DownloadUpdatesAsync(updates, progress, cancelToken);
        cancelToken.ThrowIfCancellationRequested();
        // Also verify an already-cached package: Velopack's cache fast path skips it.
        try { await VerifyPreparedAsync(updates.TargetFullRelease); }
        catch (ChecksumFailedException)
        {
            // Remove only this rejected update cache entry, so Retry can download it.
            File.Delete(Path.Combine(Locator.PackagesDir!, updates.TargetFullRelease.FileName));
            throw;
        }
    }

    internal Task VerifyPreparedAsync(VelopackAsset asset) => VerifyPackageChecksumAsync(asset);
}
