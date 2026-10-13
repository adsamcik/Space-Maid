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
    private CancellationTokenSource? _cancellation;
    private bool _nativeInstall;
    private bool _completed;
    private string? _portableFolder;

    public InstallerWindow()
    {
        InitializeComponent();
        VersionLabel.Text = _payloads.Version;
        FolderPath.Text = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Space Maid");
        RuntimePanel.Visibility = DesktopRuntime.IsAvailable() ? Visibility.Collapsed : Visibility.Visible;
        if (!_payloads.IsAvailable)
        {
            Status.Text = "This build has no installation files. Download Setup from GitHub Releases.";
            ActionButton.IsEnabled = false;
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (FolderPanel is null) return;
        FolderPanel.Visibility = PortableMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ActionButton.Content = PortableMode.IsChecked == true ? "Create portable app" : "Install";
        Status.Text = "";
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose an empty folder", Multiselect = false };
        if (Directory.Exists(FolderPath.Text)) picker.InitialDirectory = FolderPath.Text;
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
        _cancellation = new CancellationTokenSource();
        ModePanel.IsEnabled = FolderPanel.IsEnabled = ActionButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = !portable;
        Status.Text = portable ? "Preparing portable app…" : "Preparing installation…";
        try
        {
            var progress = new Progress<double>(value => Progress.Value = value * 100);
            if (portable)
            {
                string destination = FolderPath.Text;
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
                using PreparedSetup setup = await _payloads.PrepareSetupAsync(_cancellation.Token);
                _cancellation.Token.ThrowIfCancellationRequested();
                // Once Setup applies changes, its own dialog owns cancellation and rollback.
                _nativeInstall = true;
                CancelButton.IsEnabled = false;
                Status.Text = "Installing…";
                using Process process = Process.Start(new ProcessStartInfo(setup.Path) { UseShellExecute = true })
                    ?? throw new IOException("Setup could not start. Try running Setup again.");
                await process.WaitForExitAsync();
                _nativeInstall = false;
                if (process.ExitCode != 0) throw new IOException("Installation did not finish. Try again.");
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
            if (_portableFolder is null) ModePanel.IsEnabled = FolderPanel.IsEnabled = ActionButton.IsEnabled = _payloads.IsAvailable;
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
    private void TryOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException) { Status.Text = "Could not open it. " + error.Message; }
    }
}
