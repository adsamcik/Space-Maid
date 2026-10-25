using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckWorktreeLifecycle(Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        var creation = GitWorktreeAnalyzerTests.GitFixture.Create(fixture.Path);
        PumpUntil(() => creation.IsCompleted, TimeSpan.FromSeconds(15)); using var repo = creation.GetAwaiter().GetResult();
        var service = new WorktreeRetirement(Path.Join(fixture.Path, "lifecycle.json"));
        var window = new WorktreeLifecycleWindow([repo.Checkout], service, (operation, token) => operation(token), _ => { });
        try
        {
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.Show();
            var check = (Button)window.FindName("CheckButton"); var enroll = (Button)window.FindName("EnrollButton");
            var quarantine = (Button)window.FindName("QuarantineButton"); var status = (TextBlock)window.FindName("StatusText");
            Assert.False(((CheckBox)window.FindName("ManagedBox")).IsChecked); Assert.False(((CheckBox)window.FindName("AutomaticBox")).IsChecked);
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => check.IsEnabled, TimeSpan.FromSeconds(15));
            Assert.Contains("integrated locally", status.Text);
            enroll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("Opt this reproducible checkout", status.Text); Assert.Empty(service.Load().Policies);
            ((CheckBox)window.FindName("ManagedBox")).IsChecked = true;
            enroll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => enroll.IsEnabled, TimeSpan.FromSeconds(15));
            Assert.Single(service.Load().Policies);
#pragma warning disable WPF0001
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = mode; string theme = mode == ThemeMode.Light ? "light" : "dark";
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"worktree-lifecycle-{theme}.png"), 1020, 730);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"worktree-lifecycle-compact-{theme}.png"), 780, 600);
                Assert.True(((DataGrid)window.FindName("CheckoutList")).ActualHeight >= 150);
            }
            app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
            quarantine.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => quarantine.IsEnabled, TimeSpan.FromSeconds(20));
            Assert.Equal(WorktreeRetirementState.Quarantined, Assert.Single(service.Load().Records).State);
            var recovery = (DataGrid)window.FindName("RecoveryList"); recovery.SelectedIndex = 0;
            var restore = (Button)window.FindName("RestoreButton");
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => restore.IsEnabled, TimeSpan.FromSeconds(15));
            Assert.Equal(WorktreeRetirementState.Restored, Assert.Single(service.Load().Records).State);
            Assert.True(File.Exists(repo.Source)); Assert.Contains("Automatic retirement paused", status.Text);
        }
        finally { window.Close(); }
    }
}
