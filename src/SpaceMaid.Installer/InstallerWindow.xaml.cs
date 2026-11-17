using SpaceMaid.Core.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpaceMaid.Installer;

public partial class InstallerWindow : Window
{
    private readonly InstallerPayloads _payloads = new(Assembly.GetExecutingAssembly());
    private readonly Func<InstalledApplication?> _findInstallation;
    private string _normalDestination = "", _portableDestination = "";
    private string? _preservedFolder, _destinationError;
    private bool _showingPortable;
    private InstalledApplication? _installation;
    private CancellationTokenSource? _cancellation;
    private bool _nativeInstall;
    private bool _completed;
    private string? _portableFolder;

    public InstallerWindow() : this(InstallationDiscovery.Find) { }

    internal InstallerWindow(Func<InstalledApplication?> findInstallation) : this(findInstallation, InstallationDiscovery.DefaultFolder) { }

    internal InstallerWindow(Func<InstalledApplication?> findInstallation, string defaultFolder)
    {
        _findInstallation = findInstallation;
        _normalDestination = defaultFolder;
        _portableDestination = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Space Maid");
        try { (_normalDestination, _preservedFolder) = InstallationDiscovery.NewDestination(defaultFolder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { _destinationError = "The default location could not be checked. Choose an accessible empty application folder. " + error.Message; }
        InitializeComponent();
        _installation = _findInstallation();
        VersionLabel.Text = _payloads.Version;
        FolderPath.Text = _normalDestination;
        RuntimePanel.Visibility = DesktopRuntime.IsAvailable() ? Visibility.Collapsed : Visibility.Visible;
        RefreshChoice();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (FolderPanel is null) return;
        RefreshChoice();
    }

    private void RefreshChoice()
    {
        if (FolderPath is null) return;
        bool portable = PortableMode.IsChecked == true;
        if (FolderPath.Text.Length > 0)
        {
            if (_showingPortable) _portableDestination = FolderPath.Text;
            else _normalDestination = FolderPath.Text;
        }
        _showingPortable = portable;
        bool update = _installation?.NeedsUpdate(_payloads.Version) == true;
        FolderPanel.Visibility = portable || _installation is null ? Visibility.Visible : Visibility.Collapsed;
        FolderPath.Text = portable ? _portableDestination : _normalDestination;
        System.Windows.Automation.AutomationProperties.SetName(FolderPath, portable ? "Portable destination folder" : "Application destination folder");
        RuntimePanel.Visibility = portable && !DesktopRuntime.IsAvailable() ? Visibility.Visible : Visibility.Collapsed;
        DestinationHint.Text = portable ? "Choose an empty folder. Portable settings stay with this app."
            : "Choose an empty application folder. Existing settings and catalogs keep their current location.";
        PreservationText.Text = _preservedFolder is null ? ""
            : "Existing files were found at " + _preservedFolder + ". They will stay there. Install the app in a separate empty folder; existing settings and catalogs remain available where readable.";
        PreservationText.Visibility = !portable && _installation is null && _preservedFolder is not null ? Visibility.Visible : Visibility.Collapsed;
        InstallModeTitle.Text = _installation is null ? "Install for me" : update ? "Update Space Maid" : "Open Space Maid";
        System.Windows.Automation.AutomationProperties.SetName(InstallMode, InstallModeTitle.Text);
        InstallModeDescription.Text = _installation is null ? "Start menu · Update checks"
            : update ? $"{_installation.Version} → {_payloads.Version}\nCloses Space Maid; keeps settings and catalogs"
            : _installation.Version.ToString() == _payloads.Version ? $"Version {_installation.Version} is already installed"
            : $"Newer version {_installation.Version} is installed";
        InstalledFolderLabel.Text = _installation?.Folder ?? "";
        InstalledFolderLabel.Visibility = _installation is null ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = portable ? "Create portable app" : _installation is null ? "Install" : update ? "Update" : "Open Space Maid";
        bool needsPayload = portable || _installation is null || update;
        ActionButton.IsEnabled = !needsPayload || _payloads.IsAvailable;
        Status.Text = needsPayload && !_payloads.IsAvailable
            ? "This build has no installation files. Download Setup from GitHub Releases." : !portable && _installation is null ? _destinationError ?? "" : "";
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose an empty folder", Multiselect = false };
        if (DiskAccess.Default.DirectoryExists(FolderPath.Text)) picker.InitialDirectory = FolderPath.Text;
        if (picker.ShowDialog(this) == true) FolderPath.Text = picker.FolderName;
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (_portableFolder is not null)
        {
            TryOpen(Path.Join(_portableFolder, "Space Maid.exe"));
            return;
        }
        bool portable = PortableMode.IsChecked == true;
        string destination = FolderPath.Text;
        InstalledApplication? installation = _installation;
        if (!portable)
        {
            InstalledApplication? current = _findInstallation();
            if (current != installation)
            {
                _installation = current;
                RefreshChoice();
                Status.Text = "The installation changed. Review the available action and try again.";
                return;
            }
            if (installation is not null && !installation.NeedsUpdate(_payloads.Version))
            {
                TryOpen(Path.Join(installation.Folder, "current", "SpaceMaid.App.exe"));
                return;
            }
        }
        _cancellation = new CancellationTokenSource();
        ModePanel.IsEnabled = FolderPanel.IsEnabled = ActionButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = !portable;
        Status.Text = portable ? "Preparing portable app…" : installation is null ? "Preparing installation…" : "Preparing update…";
        try
        {
            // Fail before extracting a payload as well as immediately before the
            // native handoff. An occupied or unreadable folder is never repaired
            // by overwriting it, even when discovery rejected its installation.
            if (!portable && installation is null) destination = PortableInstaller.ValidateDestination(destination);
            var progress = new Progress<double>(value => Progress.Value = value * 100);
            if (portable)
            {
                _portableFolder = await Task.Run(async () =>
                {
                    using Stream payload = _payloads.OpenPortable();
                    return await PortableInstaller.ExtractAsync(payload, _payloads.PortableSha256, destination, progress, _cancellation.Token);
                });
                Status.Text = "Ready";
                ActionButton.Content = "Open Space Maid";
                ActionButton.IsEnabled = true;
                CancelButton.Content = "Close";
                OpenFolderButton.Visibility = Visibility.Visible;
            }
            else
            {
                using PreparedPayload setup = installation is null
                    ? await _payloads.PrepareSetupAsync(_cancellation.Token)
                    : await _payloads.PrepareUpdateAsync(_cancellation.Token);
                _cancellation.Token.ThrowIfCancellationRequested();
                if (_findInstallation() != installation)
                    throw new IOException("The installation changed. Close Setup and try again.");
                // Native Setup replaces its entire destination. Never use it on existing data.
                if (installation is null) PortableInstaller.ValidateDestination(destination);
                // Once Setup applies changes, its own dialog owns cancellation and rollback.
                _nativeInstall = true;
                CancelButton.IsEnabled = false;
                Status.Text = installation is null ? "Installing…" : "Updating…";
                ProcessStartInfo command = installation?.UpdateCommand(setup.Path)
                    ?? InstallationDiscovery.InstallCommand(setup.Path, destination);
                using Process process = Process.Start(command)
                    ?? throw new IOException("Setup could not start. Try running Setup again.");
                await process.WaitForExitAsync();
                _nativeInstall = false;
                if (process.ExitCode != 0) throw new IOException(installation is null ? "Installation did not finish. Try again." : "Update did not finish. Try again.");
                InstalledApplication? installed = InstallationDiscovery.Read(installation?.Folder ?? destination);
                if (installed is null || installed.Version != Velopack.SemanticVersion.Parse(_payloads.Version))
                    throw new IOException("Setup did not finish. Close Space Maid and try again.");
                if (installation is not null && !TryOpen(Path.Join(installed.Folder, "current", "SpaceMaid.App.exe"))) return;
                _completed = true;
                Close();
            }
        }
        catch (OperationCanceledException) { Status.Text = "Cancelled"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception or ArgumentException or NotSupportedException)
        {
            Status.Text = error.Message;
        }
        finally
        {
            _nativeInstall = false;
            _cancellation.Dispose();
            _cancellation = null;
            Progress.Visibility = Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            if (_portableFolder is null)
            {
                ModePanel.IsEnabled = FolderPanel.IsEnabled = true;
                // Retain the result/error until the user changes mode or retries.
                _installation = _findInstallation();
                string result = Status.Text;
                RefreshChoice();
                Status.Text = result;
            }
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cancellation is null) Close();
        else if (!_nativeInstall) _cancellation.Cancel();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_cancellation is null || _completed) return;
        e.Cancel = true;
        if (!_nativeInstall) _cancellation.Cancel();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) { if (_portableFolder is not null) TryOpen(_portableFolder); }
    private void OnDownloadRuntime(object sender, RoutedEventArgs e) => TryOpen("https://dotnet.microsoft.com/en-us/download/dotnet/10.0");
    private void OnLicenses(object sender, RoutedEventArgs e)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        var text = new System.Text.StringBuilder();
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("SpaceMaid.Installer.Notices.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            text.AppendLine(name["SpaceMaid.Installer.Notices.".Length..]).AppendLine().AppendLine(reader.ReadToEnd()).AppendLine();
        }
        var window = new Window { Title = "Licenses", Owner = this, Width = 760, Height = 560, MinWidth = 500, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = text.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) } };
        window.SetResourceReference(BackgroundProperty, "WindowBackground");
        window.SetResourceReference(ForegroundProperty, "WindowForeground");
        window.ShowDialog();
    }
    private bool TryOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return true; }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException) { Status.Text = "Could not open it. " + error.Message; return false; }
    }
}
