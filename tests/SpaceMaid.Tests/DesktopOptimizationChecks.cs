using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckOptimization(Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        using var settingsFixture = new TestDirectory();
        for (int i = 0; i < 24; i++) OptimizationTests.MakePython(fixture.Path, $"module_{i:00}");
        OptimizationTests.Age(fixture.Path);
        var scanTask = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanTask.IsCompleted, TimeSpan.FromSeconds(10));
        var scan = scanTask.GetAwaiter().GetResult(); var findings = DeveloperOpportunityFixture.Find(scan);
        var rule = Assert.Single(findings).AutomaticCleanup!;
        var policy = new OptimizationPolicy { ScopePath = fixture.Path, EnabledRules = [rule.ConsentKey(DeveloperOpportunityFixture.ProviderId)] };
        string settingsPath = Path.Join(settingsFixture.Path, "optimization-settings.json");
        var operations = new FileOperations(); int executions = 0;
        CheckProofLimitedPreviewCompletion(scan, findings, operations, policy);
        var window = new OptimizationWindow(scan, findings, operations, new(policy),
            p => new OptimizationSettings(p).Save(settingsPath), async (preview, chosen, token) =>
            { executions++; return await operations.OptimizeAsync(scan, findings, preview, chosen, DeveloperOpportunityFixture.Find, token, (_, _) => { }); });
        try
        {
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
            window.Show();
            var run = (Button)window.FindName("RunButton"); var preview = (Button)window.FindName("PreviewButton");
            Assert.False(((CheckBox)window.FindName("AutomaticBox")).IsChecked);
            Assert.False(run.IsEnabled);
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.True(run.IsEnabled);
            var files = (DataGrid)window.FindName("FileList"); Assert.Equal(24, files.Items.Count);
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme;
                string name = theme == ThemeMode.Light ? "light" : "dark";
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"optimization-{name}.png"), 980, 790);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"optimization-compact-{name}.png"), 760, 640);
                Assert.True(files.ActualHeight >= 180, "Generated files should retain a useful working area.");
                Assert.True(run.IsVisible);
            }
            app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
            ((TextBox)window.FindName("DaysBox")).Text = "20";
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, executions); Assert.Contains("Settings changed", ((TextBlock)window.FindName("StatusText")).Text);
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(OptimizationSettings.Load(settingsPath).Policy.Enabled);
            Assert.Equal(20, OptimizationSettings.Load(settingsPath).Policy.KeepRecentDays);
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.Equal(1, executions);
            Assert.Contains("24 files deleted", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.Empty(Directory.GetFiles(Path.Join(fixture.Path, "__pycache__")));
            Assert.Equal(24, Directory.GetFiles(fixture.Path, "*.py").Length);
            Assert.False(run.IsEnabled);
        }
        finally { window.Close(); }
        CheckAutomaticOptimizationSchedule(app);
    }

    private static void CheckProofLimitedPreviewCompletion(ScanSnapshot scan,
        IReadOnlyList<SpaceMaid.Core.Analysis.Opportunity> findings, FileOperations operations, OptimizationPolicy policy)
    {
        var window = new OptimizationWindow(scan, findings, operations, new(policy with { MaxProofBytesPerRun = 1 }),
            _ => throw new InvalidOperationException("Preview must not save settings."),
            (_, _, _) => throw new InvalidOperationException("Preview must not run cleanup."));
        try
        {
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.Show();
            var preview = (Button)window.FindName("PreviewButton"); var run = (Button)window.FindName("RunButton");
            int pages = 0;
            do
            {
                preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10)); pages++;
                Assert.False(run.IsEnabled);
            } while ((string)preview.Content == "Continue preview" && pages < 25);
            Assert.Equal(24, pages); Assert.Equal("Preview", preview.Content);
            Assert.Contains("Partial inspection", ((TextBlock)window.FindName("PreviewText")).Text);
        }
        finally { window.Close(); }
    }

    private static void CheckAutomaticOptimizationSchedule(Application app)
    {
        using var source = new TestDirectory(); using var state = new TestDirectory();
        OptimizationTests.MakePython(source.Path, "first"); OptimizationTests.MakePython(source.Path, "second"); OptimizationTests.Age(source.Path);
        var host = new MainWindow
        {
            LogDirectory = Path.Join(state.Path, "logs"), ExtensionDataRoot = Path.Join(state.Path, "extensions"),
            DriveHistoryPath = Path.Join(state.Path, "history.json"), ColumnLayoutPath = Path.Join(state.Path, "columns.json"),
            CleanupSettingsPath = Path.Join(state.Path, "cleanup.json"), OptimizationSettingsPath = Path.Join(state.Path, "optimization.json"),
            WorktreeLifecycleSettingsPath = Path.Join(state.Path, "managed-worktrees.json")
        };
        // This headless scheduler fixture needs only the metadata provider.
        // Avoid loading a second copy of WPF view assemblies into the same
        // Application's process-wide pack-resource cache.
        Directory.CreateDirectory(host.ExtensionDataRoot!);
        var disabledViews = Directory.GetDirectories(Path.Join(AppContext.BaseDirectory, "Extensions"))
            .Select(Path.GetFileName).Where(id => id != "space-maid.developer").Select(id => id!).ToList();
        File.WriteAllText(Path.Join(host.ExtensionDataRoot!, "extensions.json"),
            System.Text.Json.JsonSerializer.Serialize(new ExtensionSettings([], disabledViews)));
        try
        {
            host.InitializeOptimization(); host.SetScanTarget(source.Path);
            var scan = host.ScanCurrentPathAsync(); PumpUntil(() => scan.IsCompleted, TimeSpan.FromSeconds(15)); Assert.True(scan.GetAwaiter().GetResult());
            var disabled = host.TryAutomaticOptimizationAsync(); PumpUntil(() => disabled.IsCompleted, TimeSpan.FromSeconds(5)); disabled.GetAwaiter().GetResult();
            Assert.Equal(2, Directory.GetFiles(Path.Join(source.Path, "__pycache__")).Length);
            var snapshot = (ScanSnapshot)typeof(MainWindow).GetField("_snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
            var finding = Assert.Single(DeveloperOpportunityFixture.Find(snapshot));
            var policy = new OptimizationPolicy { Enabled = true, ScopePath = source.Path, MaxFilesPerRun = 1,
                EnabledRules = [finding.AutomaticCleanup!.ConsentKey(finding.ProviderId)] };
            new OptimizationSettings(policy).Save(host.OptimizationSettingsPath); host.InitializeOptimization();
            var automatic = host.TryAutomaticOptimizationAsync(); PumpUntil(() => automatic.IsCompleted, TimeSpan.FromSeconds(15)); automatic.GetAwaiter().GetResult();
            Assert.Single(Directory.GetFiles(Path.Join(source.Path, "__pycache__")));
            Assert.NotNull(OptimizationSettings.Load(host.OptimizationSettingsPath).LastRunUtc);
            var throttled = host.TryAutomaticOptimizationAsync(); PumpUntil(() => throttled.IsCompleted, TimeSpan.FromSeconds(5)); throttled.GetAwaiter().GetResult();
            Assert.Single(Directory.GetFiles(Path.Join(source.Path, "__pycache__")));
            var previous = OptimizationSettings.Load(host.OptimizationSettingsPath);
            (previous with { LastRunUtc = DateTime.UtcNow.AddHours(-2) }).Save(host.OptimizationSettingsPath); host.InitializeOptimization();
            var next = host.TryAutomaticOptimizationAsync(); PumpUntil(() => next.IsCompleted, TimeSpan.FromSeconds(15)); next.GetAwaiter().GetResult();
            Assert.Empty(Directory.GetFiles(Path.Join(source.Path, "__pycache__")));
            Assert.Equal(2, Directory.GetFiles(source.Path, "*.py").Length);
            Assert.Equal(2, Directory.GetFiles(Path.Join(state.Path, "logs", "Optimization"), "*.jsonl").Length);
        }
        finally
        {
            host.Close(); PumpUntil(() => !app.Windows.Cast<Window>().Contains(host), TimeSpan.FromSeconds(10));
        }
    }
}
