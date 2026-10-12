using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App.Extensions;

internal sealed record ExtensionManifest(string Id, string Name, string Version, int ContractVersion,
    string EntryAssembly, string EntryType, string[]? Tools = null);
internal sealed record ExtensionLocation(string Directory, bool Enabled = true);
internal sealed record ExtensionSettings(List<ExtensionLocation> Directories, List<string> DisabledIds);
internal sealed record LoadedExtension(string Id, string Name, string Directory, string Status,
    FrameworkElement? View = null, string? Error = null);

internal sealed class ExtensionRuntime : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _dataRoot, _bundleRoot, _settingsPath;
    private readonly Action<string> _reveal;
    private readonly ScanLog _diagnostics;
    private readonly ExtensionInventory _inventory;
    private readonly ExtensionJobs _jobs;
    private readonly ExifToolBroker _tools;
    private readonly List<ISpaceMaidExtension> _loaded = [];
    private readonly List<ExtensionLoadContext> _contexts = [];
    private readonly List<LoadedExtension> _entries = [];
    private readonly bool _administrator;
    private ExtensionSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _initializing;
    private bool _disposed, _initialized;
    internal IReadOnlyList<LoadedExtension> Entries => _entries;
    internal IReadOnlyList<ExtensionJobRecord> RecentJobs => _jobs.Recent;
    internal string? LogPath => _diagnostics.FilePath;
    internal string? SettingsError { get; private set; }
    internal bool IsAdministrator => _administrator;
    internal OpportunityCatalog Opportunities { get; }
    internal event Action? JobsChanged { add => _jobs.Changed += value; remove => _jobs.Changed -= value; }

    internal ExtensionRuntime(Func<ScanSnapshot?> currentScan, Func<bool> isScanStale, Action<string> reveal,
        string? dataRoot = null, string? bundleRoot = null, string? logDirectory = null, bool? administrator = null)
    {
        _administrator = administrator ?? Elevation.IsAdministrator;
        _dataRoot = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceMaid", "Extensions");
        _bundleRoot = bundleRoot ?? Path.Combine(AppContext.BaseDirectory, "Extensions");
        _settingsPath = Path.Combine(_dataRoot, "extensions.json");
        _reveal = reveal;
        _diagnostics = new ScanLog(logDirectory ?? ScanLog.DefaultDirectory);
        Opportunities = new(_diagnostics.Record, _lifetime.Token);
        _jobs = new(_diagnostics.Record);
        _inventory = new(currentScan, isScanStale, _jobs.BeginDiscovery, _lifetime.Token);
        _tools = new(_jobs.WaitForDiscoveryAsync, _diagnostics.Record, _administrator);
        _settings = ReadSettings();
    }
    internal Task InitializeAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return _initializing ?? Task.CompletedTask;
        _initialized = true;
        _initializing = InitializeCoreAsync(token);
        return _initializing;
    }
    private async Task InitializeCoreAsync(CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
        CancellationToken token = linked.Token;
        var directories = new List<(string Path, bool Enabled)>();
        if (System.IO.Directory.Exists(_bundleRoot))
            directories.AddRange(System.IO.Directory.EnumerateDirectories(_bundleRoot).Take(64).Select(path => (path, true)));
        directories.AddRange(_settings.Directories.Take(64).Select(value => (value.Directory, value.Enabled)));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in directories)
        {
            token.ThrowIfCancellationRequested();
            string id = Path.GetFileName(candidate.Path), name = id;
            ISpaceMaidExtension? instance = null;
            try
            {
                string directory = Path.GetFullPath(candidate.Path);
                if (!paths.Add(directory)) continue;
                var manifest = ReadManifest(directory); id = manifest.Id; name = manifest.Name;
                if (!ids.Add(id)) throw new InvalidDataException("Another extension has the same ID. Only one directory per extension is supported.");
                bool enabled = candidate.Enabled && !_settings.DisabledIds.Contains(id, StringComparer.OrdinalIgnoreCase);
                if (!enabled) { _entries.Add(new(id, name, directory, "Disabled · restart to apply changes")); continue; }
                if (SettingsError is not null) { _entries.Add(new(id, name, directory, "Unavailable", Error: SettingsError)); continue; }
                if (_administrator)
                { _entries.Add(new(id, name, directory, "Unavailable", Error: "Reopen Space Maid without administrator access to use extensions.")); continue; }
                string assemblyPath = Path.Combine(directory, manifest.EntryAssembly);
                ValidateEntryPath(directory, assemblyPath);
                var context = new ExtensionLoadContext(assemblyPath);
                _contexts.Add(context);
                Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
                if (assembly.GetReferencedAssemblies().Any(reference => reference.Name is "SpaceMaid.App"))
                    throw new InvalidDataException("Extensions must reference the shared Contracts API instead of App implementation types.");
                Type type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
                if (!type.IsPublic || type.IsAbstract || !typeof(ISpaceMaidExtension).IsAssignableFrom(type))
                    throw new InvalidDataException("The entry type does not implement the supported extension API.");
                instance = (ISpaceMaidExtension?)Activator.CreateInstance(type) ?? throw new InvalidDataException("The extension entry type could not be created.");
                if (instance.Descriptor != new ExtensionDescriptor(manifest.Id, manifest.Name, manifest.Version, manifest.ContractVersion))
                    throw new InvalidDataException("The extension descriptor differs from its manifest.");
                string dataDirectory = Path.Combine(_dataRoot, id), cacheDirectory = Path.Combine(dataDirectory, "Cache");
                System.IO.Directory.CreateDirectory(dataDirectory); System.IO.Directory.CreateDirectory(cacheDirectory);
                var host = new Host(dataDirectory, cacheDirectory, _inventory, _jobs.ForOwner(id), _tools, _reveal,
                    (eventName, value) => _diagnostics.Record("extension-" + eventName, new { Owner = id, Data = value }));
                await instance.InitializeAsync(host, token);
                token.ThrowIfCancellationRequested();
                FrameworkElement? view = instance.CreateView();
                if (instance is IOpportunityExtension opportunities) Opportunities.Register(id, opportunities);
                _loaded.Add(instance); instance = null;
                _entries.Add(new(id, name, directory, "Loaded", view));
                _diagnostics.Record("extension-loaded", new { Id = id, manifest.Version, manifest.ContractVersion });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { if (instance is not null) await instance.DisposeAsync(); throw; }
            catch (Exception ex)
            {
                if (instance is not null) { try { await instance.DisposeAsync(); } catch (Exception) { } }
                _entries.Add(new(id, name, candidate.Path, "Failed to load", Error: ex.GetBaseException().Message));
                _diagnostics.Record("extension-load-failed", new { Id = id, Error = ex.GetBaseException().Message });
            }
        }
    }
    internal void SetDiscoveryBusy(bool busy) => _jobs.SetDiscoveryBusy(busy);
    internal ExtensionsWindow CreateManagerWindow(Window owner) => new(this) { Owner = owner };
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
        path = Path.GetFullPath(path); var manifest = ReadManifest(path); ValidateEntryPath(path, Path.Combine(path, manifest.EntryAssembly));
        if (_settings.Directories.Count >= 64) throw new InvalidOperationException("At most 64 trusted extension directories are supported.");
        if (_settings.Directories.Any(value => value.Directory.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This extension directory has already been added.");
        var directories = _settings.Directories.ToList(); directories.Add(new(path));
        SaveSettings(new(directories, DisabledIdsForExplicitSave().Where(value => !value.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase)).ToList()));
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
    private ExtensionSettings ReadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new([], []);
            if (new FileInfo(_settingsPath).Length > 64 * 1024) throw new InvalidDataException("The extension settings file is too large.");
            var settings = JsonSerializer.Deserialize<ExtensionSettings>(File.ReadAllText(_settingsPath), Json) ?? throw new InvalidDataException("The extension settings are empty.");
            if (settings.Directories is null || settings.DisabledIds is null || settings.Directories.Count > 64 || settings.DisabledIds.Count > 128)
                throw new InvalidDataException("The extension settings have invalid limits.");
            if (settings.Directories.Any(value => value is null || string.IsNullOrWhiteSpace(value.Directory) || !Path.IsPathFullyQualified(value.Directory))
                || settings.DisabledIds.Any(value => string.IsNullOrWhiteSpace(value)))
                throw new InvalidDataException("The extension settings contain an invalid directory or ID.");
            return settings;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { SettingsError = "Extension settings could not be read: " + ex.Message; return new([], []); }
    }
    private void SaveSettings(ExtensionSettings settings)
    {
        System.IO.Directory.CreateDirectory(_dataRoot);
        string temp = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json)); File.Move(temp, _settingsPath, true); _settings = settings; SettingsError = null; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static ExtensionManifest ReadManifest(string directory)
    {
        string path = Path.Combine(directory, "extension.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("A supported extension.json manifest is required (maximum 64 KiB).");
        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("The extension manifest is empty.");
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
    private static void ValidateEntryPath(string directory, string assemblyPath)
    {
        ExtensionInventory.ValidateAncestors(directory);
        if (!File.Exists(assemblyPath) || (File.GetAttributes(assemblyPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The extension entry DLL is missing or is a reparse file.");
        if (!File.Exists(Path.ChangeExtension(assemblyPath, ".deps.json")))
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
    private sealed record Host(string DataDirectory, string CacheDirectory, IInventoryReader Inventory,
        IExtensionJobs Jobs, IToolBroker Tools, Action<string> Reveal, Action<string, object> Diagnostics) : IExtensionHost
    {
        public void RevealLocation(string path) => Reveal(path);
        public void Log(string eventName, object data) => Diagnostics(eventName, data);
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
