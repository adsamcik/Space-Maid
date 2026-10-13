using System.Windows;

namespace SpaceMaid.App;

public partial class App : Application
{
    [STAThread]
    private static void Main()
    {
        // Applying stays behind the verified, user-selected restart action.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
