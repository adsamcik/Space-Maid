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
        int operationCalls = 0;
        var changed = new List<bool>();
        var window = new WorktreeLifecycleWindow([repo.Checkout], service, (operation, token) =>
        { operationCalls++; return operation(token); }, moved => changed.Add(moved));
        try
        {
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.Show();
            var check = (Button)window.FindName("CheckButton"); var enroll = (Button)window.FindName("EnrollButton");
            var quarantine = (Button)window.FindName("QuarantineButton"); var status = (TextBlock)window.FindName("StatusText");
            Assert.False(((CheckBox)window.FindName("ManagedBox")).IsChecked); Assert.False(((CheckBox)window.FindName("AutomaticBox")).IsChecked);
            var unmanage = (Button)window.FindName("UnmanageButton"); var removeHistory = (Button)window.FindName("RemoveHistoryButton");
            Assert.False(unmanage.IsEnabled); Assert.False(removeHistory.IsEnabled);
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => check.IsEnabled, TimeSpan.FromSeconds(15));
            Assert.Contains("integrated locally", status.Text);
            enroll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("Opt this reproducible checkout", status.Text); Assert.Empty(service.Load().Policies);
            ((CheckBox)window.FindName("ManagedBox")).IsChecked = true;
            enroll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => enroll.IsEnabled, TimeSpan.FromSeconds(15));
            Assert.Single(service.Load().Policies);
            Assert.True(unmanage.IsEnabled);
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
            var record = Assert.Single(service.Load().Records);
            Assert.False(unmanage.IsEnabled); Assert.False(removeHistory.IsEnabled);
            string lifecycle = Path.Join(fixture.Path, "lifecycle.json");
            File.WriteAllText(lifecycle, System.Text.Json.JsonSerializer.Serialize(service.Load() with
            { Records = [record with { State = WorktreeRetirementState.NeedsReview }] }));
            typeof(WorktreeLifecycleWindow).GetMethod("RefreshRecovery", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
            Assert.False(((Button)window.FindName("FinalizeButton")).IsEnabled);
            Assert.False(((Button)window.FindName("RestoreButton")).IsEnabled);
            var reconcile = (Button)window.FindName("ReconcileButton");
            reconcile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => reconcile.IsEnabled, TimeSpan.FromSeconds(20));
            Assert.Equal(WorktreeRetirementState.Quarantined, Assert.Single(service.Load().Records).State);
            Assert.Contains(record.RecoveryReference, ((TextBox)window.FindName("RecoveryDetails")).Text);
            Assert.True(((Button)window.FindName("FinalizeButton")).IsEnabled);
            ((TabItem)window.FindName("RecoveryTab")).IsSelected = true;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "worktree-recovery-compact.png"), 780, 600);
            Assert.True(((TextBox)window.FindName("RecoveryDetails")).ActualHeight >= 54);
            var restore = (Button)window.FindName("RestoreButton");
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => check.IsEnabled, TimeSpan.FromSeconds(15));
            Assert.Equal(WorktreeRetirementState.Restored, Assert.Single(service.Load().Records).State);
            Assert.True(File.Exists(repo.Source)); Assert.Contains("Automatic retirement paused", status.Text);
            Assert.False(restore.IsEnabled);
            Assert.True(unmanage.IsEnabled); Assert.True(removeHistory.IsEnabled);

            // Model the durable boundary independently: a launcher can stop
            // after the intent was saved, without the view reaching its happy path.
            var restored = Assert.Single(service.Load().Records);
            var intent = restored with { Id = Guid.NewGuid(), State = WorktreeRetirementState.Prepared,
                QuarantinedUtc = DateTime.UtcNow.AddMinutes(1), Entries = [], Message = "Prepared recovery intent retained." };
            changed.Clear();
            Task StoppedMutation(Func<CancellationToken, Task> operation) => (Task)typeof(WorktreeLifecycleWindow)
                .GetMethod("PerformMutation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [operation])!;
            var stopped = StoppedMutation(_ =>
            {
                File.WriteAllText(lifecycle, System.Text.Json.JsonSerializer.Serialize(service.Load() with { Records = [restored, intent] }));
                throw new IOException("Fixture Git launch unavailable after intent was recorded.");
            });
            PumpUntil(() => stopped.IsCompleted, TimeSpan.FromSeconds(15)); stopped.GetAwaiter().GetResult();
            Assert.Equal(2, recovery.Items.Count);
            Assert.Equal(restored.Id, ((WorktreeLifecycleWindow.RecoveryRow)recovery.SelectedItem).Id);
            Assert.Contains("Fixture Git launch unavailable", status.Text);
            Assert.Contains(true, changed); // A stopped mutation with Prepared intent may already have moved files.

            recovery.SelectedItem = recovery.Items.Cast<WorktreeLifecycleWindow.RecoveryRow>().Single(row => row.Id == intent.Id);
            var interrupted = intent with { State = WorktreeRetirementState.NeedsReview, Message = "Inspect the recorded checkout before retrying." };
            stopped = StoppedMutation(_ =>
            {
                File.WriteAllText(lifecycle, System.Text.Json.JsonSerializer.Serialize(service.Load() with { Records = [restored, interrupted] }));
                throw new OperationCanceledException();
            });
            PumpUntil(() => stopped.IsCompleted, TimeSpan.FromSeconds(15)); stopped.GetAwaiter().GetResult();
            Assert.Equal(intent.Id, ((WorktreeLifecycleWindow.RecoveryRow)recovery.SelectedItem).Id);
            Assert.Equal(nameof(WorktreeRetirementState.NeedsReview), ((WorktreeLifecycleWindow.RecoveryRow)recovery.SelectedItem).State);
            Assert.Contains("Cancelled", status.Text); Assert.Contains(true, changed);
            Assert.False(restore.IsEnabled); Assert.False(((Button)window.FindName("FinalizeButton")).IsEnabled);

            var previousRows = recovery.ItemsSource;
            string readable = File.ReadAllText(lifecycle);
            changed.Clear();
            stopped = StoppedMutation(_ =>
            {
                File.WriteAllText(lifecycle, "unreadable fixture record");
                throw new IOException("Fixture operation stopped before completion.");
            });
            PumpUntil(() => stopped.IsCompleted, TimeSpan.FromSeconds(15)); stopped.GetAwaiter().GetResult();
            Assert.Same(previousRows, recovery.ItemsSource);
            Assert.Equal(intent.Id, ((WorktreeLifecycleWindow.RecoveryRow)recovery.SelectedItem).Id);
            Assert.Contains("Fixture operation stopped", status.Text); Assert.Contains("could not be refreshed", status.Text);
            Assert.False(reconcile.IsEnabled); Assert.False(restore.IsEnabled);
            Assert.False(unmanage.IsEnabled); Assert.False(removeHistory.IsEnabled);
            Assert.Contains(true, changed); // Unknown durable state cannot certify the interrupted mutation as untouched.

            File.WriteAllText(lifecycle, readable);
            changed.Clear();
            int attempts = operationCalls;
            ((Button)window.FindName("RefreshRecoveryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(attempts, operationCalls); // A records refresh never repeats a mutation.
            Assert.Empty(changed);
            Assert.Equal(intent.Id, ((WorktreeLifecycleWindow.RecoveryRow)recovery.SelectedItem).Id);
            Assert.True(reconcile.IsEnabled); Assert.False(restore.IsEnabled);
            Assert.False(unmanage.IsEnabled); Assert.False(removeHistory.IsEnabled);
            Assert.Contains("No file operation was repeated", status.Text);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "worktree-recovery-stopped.png"), 780, 600);

            // Removing completed metadata cannot discard the remaining unknown
            // recovery. Once it is explicitly restored, an unchecked policy can
            // be saved as an opt-out without deleting this checkout.
            File.WriteAllText(lifecycle, System.Text.Json.JsonSerializer.Serialize(service.Load() with { Records = [restored] }));
            ((Button)window.FindName("RefreshRecoveryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            recovery.SelectedIndex = 0;
            Assert.True(removeHistory.IsEnabled); Assert.True(unmanage.IsEnabled);
            ((CheckBox)window.FindName("ManagedBox")).IsChecked = false;
            enroll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => enroll.IsEnabled, TimeSpan.FromSeconds(15));
            Assert.Empty(service.Load().Policies); Assert.Single(service.Load().Records);
            Assert.True(File.Exists(repo.Source)); Assert.False(unmanage.IsEnabled);
            Assert.Contains("Management stopped", status.Text);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "worktree-terminal-history-actions.png"), 780, 600);
        }
        finally { window.Close(); }
    }
}
