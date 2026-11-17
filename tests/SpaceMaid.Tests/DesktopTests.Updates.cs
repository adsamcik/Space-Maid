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
            manager.CheckError = new IOException("Owned fixture offline");
            var failed = updates.CheckAsync(); PumpUntil(() => failed.IsCompleted, TimeSpan.FromSeconds(5)); failed.GetAwaiter().GetResult();
            typeof(MainWindow).GetMethod("UpdateUpdateButton", flags)!.Invoke(main, null);
            var sidebar = (Button)main.FindName("UpdateButton");
            Assert.Equal(Visibility.Visible, sidebar.Visibility); Assert.Equal("Retry update check", sidebar.Content);
            Assert.Equal("Retry check", ((Button)view.FindName("CheckButton")).Content);
            Assert.Contains("No successful check", ((TextBlock)view.FindName("CheckHistoryText")).Text);
            Assert.Null(updates.AvailableVersion);
            Render(view, Path.Join(FindRepository(), "artifacts", "ui", $"updates-offline-{theme}.png"), 430, 430);
            manager.CheckError = null;
            var check = updates.CheckAsync(); PumpUntil(() => check.IsCompleted, TimeSpan.FromSeconds(5)); check.GetAwaiter().GetResult();
            Assert.Equal("Download update", ((Button)view.FindName("ActionButton")).Content);
            var automatic = (CheckBox)view.FindName("AutomaticBox");
            var downloads = (CheckBox)view.FindName("DownloadBox");
            Assert.False(downloads.IsChecked); Assert.True(downloads.IsEnabled);
            automatic.IsChecked = false; automatic.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(downloads.IsEnabled);
            automatic.IsChecked = true; automatic.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            manager.Release.PublishedUtc = DateTimeOffset.UtcNow.AddHours(-1);
            downloads.IsChecked = true; downloads.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => !updates.Busy, TimeSpan.FromSeconds(5));
            Assert.False(updates.Ready); Assert.Equal(0, manager.Downloads);
            Assert.Contains("24-hour protection", ((TextBlock)view.FindName("StatusText")).Text);
            Assert.True(((Button)view.FindName("ActionButton")).IsEnabled);
            Render(view, Path.Join(FindRepository(), "artifacts", "ui", $"updates-protection-{theme}.png"), 430, 400);
            manager.Release.PublishedUtc = DateTimeOffset.UtcNow.AddDays(-2);
            check = updates.CheckAsync(); PumpUntil(() => check.IsCompleted, TimeSpan.FromSeconds(5)); check.GetAwaiter().GetResult();
            Assert.True(updates.AutomaticDownloads); Assert.True(updates.Ready);
            Render(view, Path.Join(FindRepository(), "artifacts", "ui", $"updates-{theme}.png"), 430, 380);
            var action = (Button)view.FindName("ActionButton");
            Assert.Equal("Restart and update", action.Content); Assert.True(action.IsEnabled);
            manager.CheckError = new IOException("Owned fixture offline after download");
            check = updates.CheckAsync(); PumpUntil(() => check.IsCompleted, TimeSpan.FromSeconds(5)); check.GetAwaiter().GetResult();
            Assert.True(updates.Ready); Assert.False(updates.AvailableVerified);
            Assert.True(action.IsEnabled); Assert.Equal("Restart and update", action.Content);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<CancellationToken, Task> browse = _ => gate.Task;
            var browseTask = (Task)typeof(MainWindow).GetMethod("BrowseAsync", flags)!.Invoke(main, [browse])!;
            Assert.False(action.IsEnabled);
            Assert.Equal(Visibility.Visible, ((TextBlock)view.FindName("BusyHint")).Visibility);
            gate.SetResult(); PumpUntil(() => browseTask.IsCompleted, TimeSpan.FromSeconds(5)); browseTask.GetAwaiter().GetResult();
            Assert.True(action.IsEnabled); // Host idle transition updates the open dialog without reactivation.
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, restarts);
        }
        finally
        {
            view.Close(); updatesField.SetValue(main, previousUpdates); windowField.SetValue(main, previousWindow);
            typeof(MainWindow).GetMethod("UpdateUpdateButton", flags)!.Invoke(main, null);
        }
    }
}
