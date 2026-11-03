using System.Windows;

namespace SpaceMaid.App;

public partial class App : Application
{
    [STAThread]
    private static void Main()
    {
        if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() == "--agent-stdio")
        {
            Environment.ExitCode = AgentAccess.AgentConnection.RunStdioAsync().GetAwaiter().GetResult();
            return;
        }
        // Applying stays behind the verified, user-selected restart action.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--cleanup-scheduled")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!CleanupScheduledEntrypoint.TryParse(e.Args, out Guid definitionId, out int revision)) { Shutdown(2); return; }
            var cancellation = new CancellationTokenSource();
            SessionEndingCancelEventHandler sessionEnding = (_, _) => cancellation.Cancel();
            SessionEnding += sessionEnding;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                int exitCode;
                try { exitCode = await CleanupScheduledEntrypoint.RunAsync(definitionId, revision, cancellation.Token); }
                catch { exitCode = 1; } // No UI or elevation prompt in an unattended invocation.
                finally { SessionEnding -= sessionEnding; cancellation.Dispose(); }
                Shutdown(exitCode);
            }));
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
