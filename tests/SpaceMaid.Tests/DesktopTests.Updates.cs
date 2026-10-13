using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void VerifyUpdatesUi(Window owner, string theme)
    {
        using var fixture = new TestDirectory();
        var manager = new UpdateTests.StubUpdateManager(fixture.Path);
        using var updates = new AppUpdates(manager, Path.Join(fixture.Path, "settings.json"), Path.Join(fixture.Path, "Logs"));
        var main = (MainWindow)owner;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var updatesField = typeof(MainWindow).GetField("_updates", flags)!;
        var windowField = typeof(MainWindow).GetField("_updatesWindow", flags)!;
        object? previousUpdates = updatesField.GetValue(main), previousWindow = windowField.GetValue(main);
        var canRestart = typeof(MainWindow).GetMethod("CanRestartForUpdate", flags)!.CreateDelegate<Func<bool>>(main);
        int restarts = 0;
        var view = new UpdatesWindow(updates, canRestart, () => restarts++) { Owner = owner };
        updatesField.SetValue(main, updates); windowField.SetValue(main, view);
        try
        {
            var check = updates.CheckAsync(); PumpUntil(() => check.IsCompleted, TimeSpan.FromSeconds(5)); check.GetAwaiter().GetResult();
            Assert.Equal("Download update", ((Button)view.FindName("ActionButton")).Content);
            Render(view, Path.Join(FindRepository(), "artifacts", "ui", $"updates-{theme}.png"), 430, 270);
            var download = updates.DownloadAsync(); PumpUntil(() => download.IsCompleted, TimeSpan.FromSeconds(5)); download.GetAwaiter().GetResult();
            var action = (Button)view.FindName("ActionButton");
            Assert.Equal("Restart and update", action.Content); Assert.True(action.IsEnabled);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<CancellationToken, Task> browse = _ => gate.Task;
            var browseTask = (Task)typeof(MainWindow).GetMethod("BrowseAsync", flags)!.Invoke(main, [browse])!;
            Assert.False(action.IsEnabled);
            Assert.Equal(Visibility.Visible, ((TextBlock)view.FindName("BusyHint")).Visibility);
            gate.SetResult(); PumpUntil(() => browseTask.IsCompleted, TimeSpan.FromSeconds(5)); browseTask.GetAwaiter().GetResult();
            Assert.True(action.IsEnabled); // Host idle transition updates the open dialog without reactivation.
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, restarts);
        }
        finally { view.Close(); updatesField.SetValue(main, previousUpdates); windowField.SetValue(main, previousWindow); }
    }
}
