using System.ComponentModel;
using System.IO;
using System.Windows;

namespace SpaceMaid.Installer;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var payloads = new InstallerPayloads(typeof(App).Assembly);
        if (!payloads.IsPortableLauncher)
        {
            MainWindow = new InstallerWindow();
            MainWindow.Show();
            return;
        }

        try
        {
            int exitCode = await Task.Run(() => PortableLauncher.LaunchAsync(payloads, e.Args));
            Shutdown(exitCode);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show("Space Maid could not start. Close other copies and try again. If it still fails, clear "
                + "%LOCALAPPDATA%\\SpaceMaid.Portable\\Cache or download a fresh copy.\n\n" + error.Message,
                "Space Maid", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
