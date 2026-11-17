using SpaceMaid.Core.IO;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App.Extensions;

internal sealed record ExtensionManifest(string Id, string Name, string Version, int ContractVersion,
    string EntryAssembly, string EntryType, string[]? Tools = null)
{
    public string? NavigationIcon { get; init; }
}
internal sealed record ExtensionLocation(string Directory, bool Enabled = true);
internal sealed record ExtensionSettings(List<ExtensionLocation> Directories, List<string> DisabledIds);
internal sealed record LoadedExtension(string Id, string Name, string Directory, string Status,
    FrameworkElement? View = null, string? Error = null, string? Version = null, bool SupportsOpportunities = false)
{
    public BitmapSource? NavigationIcon { get; init; }
}

internal sealed partial class ExtensionRuntime : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _dataRoot, _bundleRoot, _settingsPath;
    private readonly Action<string> _reveal;
    private readonly ScanLog _diagnostics;
    private readonly ExtensionInventory _inventory;
    private readonly ExtensionJobs _jobs;
    private readonly ExifToolBroker _tools;
    private readonly DiskAccess _files;
    private readonly List<ISpaceMaidExtension> _loaded = [];
    private readonly List<ExtensionLoadContext> _contexts = [];
    private readonly Dictionary<string, (ExtensionLoadContext Context, string Assembly, string Fingerprint)> _candidateContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unsafeRetries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _loadedAssemblyFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LoadedExtension> _entries = [];
    private readonly bool _administrator;
    private readonly Func<IInventoryEvidenceSession, IReadOnlyList<EvidenceId>, CancellationToken, Task<EvidenceCleanupReviewResult>>? _evidenceReview;
    private readonly IOpportunityBrowserHost? _opportunityBrowser;
    private readonly IExtensionNavigation? _navigation;
    private readonly ICloudStorageServices? _cloudStorage;
    private readonly Dictionary<string, bool> _initialEnabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _addedIds = new(StringComparer.OrdinalIgnoreCase);
    private ExtensionSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _initializing;
    private bool _disposed, _initialized;
    private bool _createViews;
    internal IReadOnlyList<LoadedExtension> Entries => _entries;
    internal IReadOnlyList<ExtensionJobRecord> RecentJobs => _jobs.Recent;
    internal string? LogPath => _diagnostics.FilePath;
    internal string? SettingsError { get; private set; }
    internal bool IsAdministrator => _administrator;
    internal IInventoryEvidence Evidence => _inventory;
    internal OpportunityCatalog Opportunities { get; }
    internal event Action? JobsChanged { add => _jobs.Changed += value; remove => _jobs.Changed -= value; }

    internal ExtensionRuntime(Func<ScanSnapshot?> currentScan, Func<bool> isScanStale, Action<string> reveal,
        string? dataRoot = null, string? bundleRoot = null, string? logDirectory = null, bool? administrator = null,
        Func<IInventoryEvidenceSession, IReadOnlyList<EvidenceId>, CancellationToken, Task<EvidenceCleanupReviewResult>>? evidenceReview = null,
        FileOperations? operations = null, IOpportunityBrowserHost? opportunityBrowser = null, IExtensionNavigation? navigation = null,
        ICloudStorageServices? cloudStorage = null)
    {
        _evidenceReview = evidenceReview; _opportunityBrowser = opportunityBrowser;
        _navigation = navigation;
        _cloudStorage = cloudStorage;
        _administrator = administrator ?? Elevation.IsAdministrator;
        _dataRoot = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceMaid", "Extensions");
        _bundleRoot = bundleRoot ?? Path.Combine(AppContext.BaseDirectory, "Extensions");
        _settingsPath = Path.Combine(_dataRoot, "extensions.json");
        _reveal = reveal;
        _diagnostics = new ScanLog(logDirectory ?? ScanLog.DefaultDirectory);
        Opportunities = new(_diagnostics.Record, _lifetime.Token);
        _jobs = new(_diagnostics.Record);
        operations ??= new FileOperations();
        _files = operations.Files;
        _inventory = new(currentScan, isScanStale, _jobs.BeginDiscovery, _lifetime.Token, operations, _diagnostics.Record);
        _tools = new(_files, _jobs.WaitForDiscoveryAsync, _diagnostics.Record, _administrator);
        _settings = ReadSettings();
    }
    internal Task InitializeAsync(CancellationToken token = default)
        => InitializeOnceAsync(createViews: true, token);

    // Scheduled cleanup loads classifiers without creating views or starting
    // unrelated UI-owned services. This runtime has its own process lifetime.
    internal Task InitializeDiscoveryAsync(CancellationToken token = default)
        => InitializeOnceAsync(createViews: false, token);

    private Task InitializeOnceAsync(bool createViews, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return _initializing ?? Task.CompletedTask;
        _initialized = true; _createViews = createViews;
        _initializing = InitializeCoreAsync(createViews, token);
        return _initializing;
    }
    internal bool CanRetry => !_administrator && !_disposed && _initializing?.IsCompleted != false
        && !RecentJobs.Any(job => job.State is ExtensionJobState.Queued or ExtensionJobState.Running);

    internal Task RetryFailedAsync(CancellationToken token = default)
    {
        if (!CanRetry) throw new InvalidOperationException("Wait for extension work to finish before retrying.");
        if (!_initialized) return InitializeAsync(token);
        if (SettingsError is not null)
        {
            var retained = _settings; SettingsError = null;
            var read = ReadSettingsCore(requireExisting: true);
            if (SettingsError is null) _settings = read; else _settings = retained;
        }
        _initializing = InitializeCoreAsync(_createViews, token, retry: true);
        return _initializing;
    }

    private async Task InitializeCoreAsync(bool createViews, CancellationToken callerToken, bool retry = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();
        var directories = new List<(string Path, bool Enabled)>();
        if (_files.DirectoryExists(_bundleRoot))
            directories.AddRange(_files.EnumerateDirectories(_bundleRoot).Take(64).Select(path => (path, true)));
        directories.AddRange(_settings.Directories.Take(64).Select(value => (value.Directory, value.Enabled)));
        var ids = (retry ? _entries.Where(entry => entry.Status is "Loaded" or "Disabled" or "Not needed for background discovery")
            .Select(entry => entry.Id) : Enumerable.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in directories)
        {
            token.ThrowIfCancellationRequested();
            string id = Path.GetFileName(candidate.Path), name = id;
            ISpaceMaidExtension? instance = null;
            ExtensionLoadContext? attemptedContext = null;
            try
            {
                string directory = Path.GetFullPath(candidate.Path);
                if (!paths.Add(directory)) continue;
                if (retry && _entries.Any(entry => entry.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase)
                    && entry.Status is "Loaded" or "Disabled" or "Not needed for background discovery")) continue;
                if (_unsafeRetries.Contains(directory)) throw new InvalidOperationException("Extension shutdown did not finish. Restart Space Maid before retrying this extension.");
                var manifest = ReadManifestUsing(_files, directory); id = manifest.Id; name = manifest.Name;
                if (retry && _addedIds.Contains(id)) continue; // Newly trusted code still uses the advertised restart boundary.
                if (!ids.Add(id)) throw new InvalidDataException("Another extension has the same ID. Only one directory per extension is supported.");
                bool enabled = candidate.Enabled && !_settings.DisabledIds.Contains(id, StringComparer.OrdinalIgnoreCase);
                _initialEnabled[id] = enabled;
                if (!enabled) { PublishEntry(new(id, name, directory, "Disabled", Version: manifest.Version)); continue; }
                if (SettingsError is not null) { PublishEntry(new(id, name, directory, "Unavailable", Error: SettingsError)); continue; }
                if (_administrator)
                { PublishEntry(new(id, name, directory, "Unavailable", Error: "Reopen Space Maid without administrator access to use extensions.")); continue; }
                string assemblyPath = Path.Combine(directory, manifest.EntryAssembly);
                ValidateEntryPath(directory, assemblyPath, _files);
                var fingerprint = AssemblyFingerprint(assemblyPath);
                ExtensionLoadContext context;
                if (_candidateContexts.TryGetValue(directory, out var cached))
                {
                    if (!cached.Assembly.Equals(assemblyPath, StringComparison.OrdinalIgnoreCase)
                        || cached.Context.Assemblies.Any() && cached.Fingerprint != fingerprint)
                        throw new InvalidOperationException("Extension code changed after loading. Restart Space Maid to use the changed binaries.");
                    foreach (var loaded in cached.Context.Assemblies.Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location)))
                        if (_loadedAssemblyFingerprints.TryGetValue(loaded.Location, out string? expected)
                            && AssemblyFingerprint(loaded.Location) != expected)
                            throw new InvalidOperationException("An extension dependency changed after loading. Restart Space Maid to use the changed binaries.");
                    context = cached.Context;
                }
                else { context = new ExtensionLoadContext(assemblyPath); _contexts.Add(context); }
                _candidateContexts[directory] = (context, assemblyPath, fingerprint);
                attemptedContext = context;
                Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
                if (assembly.GetReferencedAssemblies().Any(reference => reference.Name is "SpaceMaid.App"))
                    throw new InvalidDataException("Extensions must reference the shared Contracts API instead of App implementation types.");
                Type type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
                if (!type.IsPublic || type.IsAbstract || !typeof(ISpaceMaidExtension).IsAssignableFrom(type))
                    throw new InvalidDataException("The entry type does not implement the supported extension API.");
                if (!createViews && !typeof(IOpportunityExtension).IsAssignableFrom(type))
                {
                    PublishEntry(new(id, name, directory, "Not needed for background discovery", Version: manifest.Version));
                    continue;
                }
                instance = (ISpaceMaidExtension?)Activator.CreateInstance(type) ?? throw new InvalidDataException("The extension entry type could not be created.");
                if (instance.Descriptor != new ExtensionDescriptor(manifest.Id, manifest.Name, manifest.Version, manifest.ContractVersion))
                    throw new InvalidDataException("The extension descriptor differs from its manifest.");
                string dataDirectory = Path.Combine(_dataRoot, id), cacheDirectory = Path.Combine(dataDirectory, "Cache");
                _files.CreateDirectory(dataDirectory); _files.CreateDirectory(cacheDirectory);
                var host = new Host(dataDirectory, cacheDirectory, _files, _opportunityBrowser, _navigation, _cloudStorage, _inventory, _jobs.ForOwner(id), _tools, _reveal, ReviewEvidenceAsync,
                    (eventName, value) => _diagnostics.Record("extension-" + eventName, new { Owner = id, Data = value }));
                await instance.InitializeAsync(host, token);
                token.ThrowIfCancellationRequested();
                FrameworkElement? view = createViews ? instance.CreateView() : null;
                var navigationIcon = !createViews ? null : ReadNavigationIcon(_files, directory, manifest.NavigationIcon,
                    reason => _diagnostics.Record("extension-navigation-icon-unavailable", new { Id = id, manifest.NavigationIcon, Reason = reason }));
                if (instance is IOpportunityExtension opportunities) Opportunities.Register(id, opportunities);
                bool supportsOpportunities = instance is IOpportunityExtension;
                _loaded.Add(instance); instance = null;
                PublishEntry(new(id, name, directory, "Loaded", view, Version: manifest.Version, SupportsOpportunities: supportsOpportunities)
                {
                    NavigationIcon = navigationIcon
                });
                _diagnostics.Record("extension-loaded", new { Id = id, manifest.Version, manifest.ContractVersion });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (instance is not null)
                    try { await instance.DisposeAsync(); }
                    catch (Exception cleanup) { _unsafeRetries.Add(Path.GetFullPath(candidate.Path)); _diagnostics.Record("extension-failed-initialization-cleanup", new { Id = id, Error = cleanup.Message }); }
                throw;
            }
            catch (Exception ex)
            {
                if (instance is not null)
                {
                    try { await instance.DisposeAsync(); }
                    catch (Exception cleanup) { _unsafeRetries.Add(Path.GetFullPath(candidate.Path)); _diagnostics.Record("extension-failed-initialization-cleanup", new { Id = id, Error = cleanup.Message }); }
                }
                PublishEntry(new(id, name, candidate.Path, "Failed to load", Error: ex.GetBaseException().Message));
                _diagnostics.Record("extension-load-failed", new { Id = id, Error = ex.GetBaseException().Message });
            }
            finally
            {
                if (attemptedContext is not null)
                    foreach (var loaded in attemptedContext.Assemblies.Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location)))
                        if (!_loadedAssemblyFingerprints.ContainsKey(loaded.Location))
                            try { _loadedAssemblyFingerprints.Add(loaded.Location, AssemblyFingerprint(loaded.Location)); }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                            {
                                _unsafeRetries.Add(Path.GetFullPath(candidate.Path));
                                _diagnostics.Record("extension-code-identity-unavailable", new { Id = id, Error = error.Message });
                            }
            }
        }
    }
    private string AssemblyFingerprint(string path)
    { using var input = _files.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }

    private void PublishEntry(LoadedExtension entry)
    {
        int previous = _entries.FindIndex(item => item.Directory.Equals(entry.Directory, StringComparison.OrdinalIgnoreCase));
        if (previous < 0) _entries.Add(entry); else _entries[previous] = entry;
    }

    internal void SetDiscoveryBusy(bool busy) => _jobs.SetDiscoveryBusy(busy);
    internal async Task<EvidenceCleanupReviewResult> ReviewEvidenceAsync(IInventoryEvidenceSession session,
        IReadOnlyList<EvidenceId> selected, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        if (_administrator || session is not InventoryEvidenceSession owned || !ReferenceEquals(owned.Owner, _inventory)
            || _evidenceReview is null) throw new InvalidOperationException("Host cleanup review is unavailable for this evidence session.");
        if (selected is null || selected.Count is < 1 or > 20) throw new ArgumentException("Select 1–20 investigation items.");
        var summary = await session.SummarizeAsync(selected, linked.Token);
        if (!summary.Coverage.Complete) throw new InvalidOperationException("Refresh this scope before requesting cleanup review.");
        foreach (var id in selected)
        {
            var entry = await session.DetailsAsync(id, linked.Token);
            if (!entry.IsDirectory || entry.ReviewPolicy.Disposition == EvidenceReviewDisposition.Blocked)
                throw new InvalidOperationException("Only regular unblocked folders can be mapped to cleanup rules.");
            string prefix = entry.Path.EndsWith('\\') ? entry.Path : entry.Path + "\\";
            if (session.Scope.ExcludedPaths.Any(path => path.Equals(entry.Path, StringComparison.Ordinal) || path.StartsWith(prefix, StringComparison.Ordinal)))
                throw new InvalidOperationException("A selected folder contains a user exclusion. Choose a folder that does not contain excluded data.");
        }
        return await _evidenceReview(session, selected, linked.Token);
    }
    internal bool HasPendingChange(LoadedExtension entry) => SettingsError is null
        && (_initialEnabled.ContainsKey(entry.Id) || _addedIds.Contains(entry.Id))
        && IsEnabled(entry) != _initialEnabled.GetValueOrDefault(entry.Id);
    internal bool IsEnabled(LoadedExtension entry) => SettingsError is null && !_settings.DisabledIds.Contains(entry.Id, StringComparer.OrdinalIgnoreCase)
        && !_settings.Directories.Any(value => value.Directory.Equals(entry.Directory, StringComparison.OrdinalIgnoreCase) && !value.Enabled);
    internal void SetEnabled(LoadedExtension entry, bool enabled)
    {
        var disabled = DisabledIdsForExplicitSave().Where(value => !value.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!enabled) disabled.Add(entry.Id);
        var directories = _settings.Directories.Select(value => value.Directory.Equals(entry.Directory, StringComparison.OrdinalIgnoreCase)
            ? value with { Enabled = enabled } : value).ToList();
        SaveSettings(new(directories, disabled));
    }
    internal ExtensionManifest AddTrustedDirectory(string path)
    {
        path = Path.GetFullPath(path); var manifest = ReadManifestUsing(_files, path); ValidateEntryPath(path, Path.Combine(path, manifest.EntryAssembly), _files);
        if (_settings.Directories.Count >= 64) throw new InvalidOperationException("At most 64 trusted extension directories are supported.");
        if (_settings.Directories.Any(value => value.Directory.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This extension directory has already been added.");
        if (_entries.Any(entry => entry.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
            || _addedIds.Contains(manifest.Id))
            throw new InvalidOperationException("An extension with this ID is already installed.");
        var directories = _settings.Directories.ToList(); directories.Add(new(path));
        SaveSettings(new(directories, DisabledIdsForExplicitSave().Where(value => !value.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase)).ToList()));
        _addedIds.Add(manifest.Id);
        return manifest;
    }
    internal bool TryAddTrustedDirectory(string path, out ExtensionManifest? manifest, out string? error)
    {
        try { manifest = AddTrustedDirectory(path); error = null; return true; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or ArgumentException)
        { manifest = null; error = ex.Message; return false; }
    }
    private IEnumerable<string> DisabledIdsForExplicitSave() => SettingsError is null ? _settings.DisabledIds
        : _settings.DisabledIds.Concat(_entries.Select(entry => entry.Id)).Distinct(StringComparer.OrdinalIgnoreCase);
    private ExtensionSettings ReadSettings() => ReadSettingsCore(requireExisting: false);

    private ExtensionSettings ReadSettingsCore(bool requireExisting)
    {
        try
        {
            using var input = _files.OpenRead(_settingsPath);
            var bytes = new byte[64 * 1024 + 1]; int count = 0, read;
            while (count < bytes.Length && (read = input.Read(bytes.AsSpan(count))) > 0) count += read;
            if (count == bytes.Length) throw new InvalidDataException("The extension settings file is too large.");
            using var text = new StreamReader(new MemoryStream(bytes, 0, count), detectEncodingFromByteOrderMarks: true);
            var settings = JsonSerializer.Deserialize<ExtensionSettings>(text.ReadToEnd(), Json) ?? throw new InvalidDataException("The extension settings are empty.");
            if (settings.Directories is null || settings.DisabledIds is null || settings.Directories.Count > 64 || settings.DisabledIds.Count > 128)
                throw new InvalidDataException("The extension settings have invalid limits.");
            if (settings.Directories.Any(value => value is null || string.IsNullOrWhiteSpace(value.Directory) || !Path.IsPathFullyQualified(value.Directory))
                || settings.DisabledIds.Any(value => string.IsNullOrWhiteSpace(value)))
                throw new InvalidDataException("The extension settings contain an invalid directory or ID.");
            return settings;
        }
        catch (FileNotFoundException) when (!requireExisting) { return new([], []); }
        catch (DirectoryNotFoundException) when (!requireExisting && !_files.DirectoryExists(_dataRoot)) { return new([], []); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { SettingsError = "Extension settings could not be read: " + ex.Message; return new([], []); }
    }
    private void SaveSettings(ExtensionSettings settings)
    {
        _files.CreateDirectory(_dataRoot);
        string temp = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { _files.WriteAllText(temp, JsonSerializer.Serialize(settings, Json)); _files.MoveFile(temp, _settingsPath, true); _settings = settings; SettingsError = null; }
        finally { if (_files.FileExists(temp)) _files.DeleteFile(temp); }
    }
    internal static ExtensionManifest ReadManifest(string directory)
        => ReadManifestUsing(DiskAccess.Default, directory);

    private static ExtensionManifest ReadManifestUsing(DiskAccess files, string directory)
    {
        string path = Path.Combine(directory, "extension.json");
        if (!files.FileExists(path) || files.FileLength(path) > 64 * 1024) throw new InvalidDataException("A supported extension.json manifest is required (maximum 64 KiB).");
        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(files.ReadAllText(path), Json) ?? throw new InvalidDataException("The extension manifest is empty.");
        if (string.IsNullOrEmpty(manifest.Id) || !Regex.IsMatch(manifest.Id, "^[a-z][a-z0-9.-]{2,63}$") || manifest.Id.Contains("..", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100 || !Version.TryParse(manifest.Version, out _))
            throw new InvalidDataException("The extension ID, name or version is invalid.");
        if (manifest.ContractVersion != ExtensionApi.Version) throw new InvalidDataException($"Unsupported extension API version {manifest.ContractVersion}; this app supports {ExtensionApi.Version}.");
        if (string.IsNullOrEmpty(manifest.EntryAssembly) || manifest.EntryAssembly != Path.GetFileName(manifest.EntryAssembly)
            || !manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(manifest.EntryType))
            throw new InvalidDataException("The entry assembly must be a DLL filename inside its extension directory.");
        if (manifest.Tools?.Any(tool => tool != "ExifTool") == true) throw new InvalidDataException("The manifest declares a tool this host does not support.");
        return manifest;
    }
    private static void ValidateEntryPath(string directory, string assemblyPath, DiskAccess files)
    {
        FileOperations.ValidateLocalAncestors(directory);
        if (!files.FileExists(assemblyPath) || (files.GetAttributes(assemblyPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The extension entry DLL is missing or is a reparse file.");
        if (!files.FileExists(Path.ChangeExtension(assemblyPath, ".deps.json")))
            throw new InvalidDataException("The extension dependency manifest (.deps.json) is missing.");
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _lifetime.Cancel();
        if (_initializing is not null)
            try { await _initializing; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _diagnostics.Record("extension-initialization-failed-during-disposal", new { Type = ex.GetType().Name, Error = ex.Message }); }
        // Stop owned work before plugin/catalog disposal.
        await _jobs.DisposeAsync(); await _tools.DisposeAsync(); await Opportunities.DisposeAsync();
        foreach (var extension in _loaded)
            try { await extension.DisposeAsync(); } catch (Exception ex) { _diagnostics.Record("extension-dispose-failed", new { Error = ex.Message }); }
        _loaded.Clear(); _diagnostics.Dispose(); _lifetime.Dispose();
        // Contexts intentionally remain loaded until process exit; settings apply on restart.
    }
    private sealed record Host(string DataDirectory, string CacheDirectory, DiskAccess Files, IOpportunityBrowserHost? OpportunityBrowser, IExtensionNavigation? Navigation, ICloudStorageServices? CloudStorage, IInventoryReader Inventory,
        IExtensionJobs Jobs, IToolBroker Tools, Action<string> Reveal,
        Func<IInventoryEvidenceSession, IReadOnlyList<EvidenceId>, CancellationToken, Task<EvidenceCleanupReviewResult>> EvidenceReview,
        Action<string, object> Diagnostics) : IExtensionHost, IExtensionEvidenceReview, IOpportunityBrowserServices, IExtensionNavigationHost, ICloudStorageHost
    {
        public void RevealLocation(string path) => Reveal(path);
        public void Log(string eventName, object data) => Diagnostics(eventName, data);
        public Task<EvidenceCleanupReviewResult> ReviewEvidenceAsync(IInventoryEvidenceSession session, IReadOnlyList<EvidenceId> selectedItems, CancellationToken token)
            => EvidenceReview(session, selectedItems, token);
    }
    private sealed class ExtensionLoadContext(string entryAssembly) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(entryAssembly);
        private static readonly HashSet<string> FrameworkAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator).Where(path => Path.GetDirectoryName(path) == Path.GetDirectoryName(typeof(object).Assembly.Location)
                || Path.GetDirectoryName(path) == Path.GetDirectoryName(typeof(FrameworkElement).Assembly.Location))
            .Select(Path.GetFileNameWithoutExtension).Where(name => name is not null).Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == typeof(ISpaceMaidExtension).Assembly.GetName().Name) return typeof(ISpaceMaidExtension).Assembly;
            if (assemblyName.Name == typeof(IOpportunityInventory).Assembly.GetName().Name) return typeof(IOpportunityInventory).Assembly;
            if (assemblyName.Name is { } name && FrameworkAssemblies.Contains(name))
                return Default.LoadFromAssemblyName(assemblyName);
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        { string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName); return path is null ? 0 : LoadUnmanagedDllFromPath(path); }
    }
}
