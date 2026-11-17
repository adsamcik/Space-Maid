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
        CheckOptimizationHub(app, artifactRoot);
        CheckOptimizationHostIntegration(app);
        CheckOptimizationExclusions(app, artifactRoot);
        CheckOptimizationInventoryContinuation();
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
        CheckOptimizationInterruptedResultRetention(scan, findings, operations, policy);
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
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"optimization-compact-{name}.png"), 840, 640);
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
            ((TabItem)window.FindName("AutomaticTab")).IsSelected = true;
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(OptimizationSettings.Load(settingsPath).Policy.Enabled);
            Assert.Equal(20, OptimizationSettings.Load(settingsPath).Policy.KeepRecentDays);
            ((TabItem)window.FindName("ActionsTab")).IsSelected = true;
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.Equal(1, executions);
            Assert.Contains("24 files deleted", ((TextBlock)window.FindName("StatusText")).Text);
            var results = (DataGrid)window.FindName("ResultList");
            Assert.Equal(24, results.Items.Count);
            Assert.All(results.Items.Cast<OptimizationWindow.DisplayResult>(), row => Assert.Contains("Deleted", row.Reason));
            Assert.Empty(Directory.GetFiles(Path.Join(fixture.Path, "__pycache__")));
            Assert.Equal(24, Directory.GetFiles(fixture.Path, "*.py").Length);
            Assert.False(run.IsEnabled);
            window.AddExclusions([Path.Join(fixture.Path, "retained")]);
            Assert.Equal(24, results.Items.Count);
            Assert.False(run.IsEnabled);
        }
        finally { window.Close(); }
        CheckAutomaticOptimizationSchedule(app);
    }

    private static void CheckOptimizationInterruptedResultRetention(ScanSnapshot scan,
        IReadOnlyList<SpaceMaid.Core.Analysis.Opportunity> findings, FileOperations operations, OptimizationPolicy policy)
    {
        int attempts = 0;
        var window = new OptimizationWindow(scan, findings, operations, new(policy), _ => { },
            (preview, _, _) =>
            {
                attempts++;
                if (attempts > 1) throw new IOException("Fixture revalidation unavailable.");
                // Receipt presentation only: this callback never mutates fixture files.
                return Task.FromResult(new OptimizationResult([
                    new(preview.Files[0].Path, OptimizationOutcome.Skipped, 0, "Fixture changed file kept."),
                    new(preview.Files[1].Path, OptimizationOutcome.Failed, 0, "Fixture locked file kept.")], true));
            });
        try
        {
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.Show();
            var preview = (Button)window.FindName("PreviewButton"); var run = (Button)window.FindName("RunButton");
            var results = (DataGrid)window.FindName("ResultList");
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.True(run.IsEnabled);
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
            var receipts = results.Items.Cast<OptimizationWindow.DisplayResult>().ToArray();
            Assert.Equal(2, receipts.Length);
            Assert.Contains(receipts, row => row.Reason.Contains("Fixture changed file kept."));
            Assert.Contains(receipts, row => row.Reason.Contains("Fixture locked file kept."));
            Assert.Contains("Cancelled", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.False(run.IsEnabled);

            // A fresh preview supplies new authority, but does not replace receipts.
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.Equal(receipts, results.Items.Cast<OptimizationWindow.DisplayResult>().Take(2).ToArray());
            ((TabItem)window.FindName("GeneratedFilesTab")).IsSelected = true;
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.Equal(2, attempts);
            Assert.Contains("Fixture revalidation unavailable", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.Equal(receipts, results.Items.Cast<OptimizationWindow.DisplayResult>().ToArray());
            Assert.False(run.IsEnabled);

            window.AddExclusions([Path.Join(scan.RootPath, "retained")]);
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(receipts, results.Items.Cast<OptimizationWindow.DisplayResult>().ToArray());
            ((TabItem)window.FindName("ActionsTab")).IsSelected = true;
            ((TextBox)window.FindName("ActionSearch")).Text = "no matching fixture action";
            ((TextBox)window.FindName("ActionSearch")).Text = "";
            Assert.Equal(receipts, results.Items.Cast<OptimizationWindow.DisplayResult>().ToArray());
            Assert.False(run.IsEnabled);
        }
        finally { window.Close(); }
    }

    private static void CheckOptimizationInventoryContinuation()
    {
        foreach (bool refreshFailure in new[] { false, true })
        {
            using var fixture = new TestDirectory();
            OptimizationTests.MakePython(fixture.Path, "first"); OptimizationTests.MakePython(fixture.Path, "second"); OptimizationTests.Age(fixture.Path);
            var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
            PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(10));
            var scan = scanning.GetAwaiter().GetResult();
            var findings = DeveloperOpportunityFixture.Find(scan);
            var rule = Assert.Single(findings).AutomaticCleanup!;
            var policy = new OptimizationPolicy { ScopePath = fixture.Path, MaxFilesPerRun = 1,
                EnabledRules = [rule.ConsentKey(DeveloperOpportunityFixture.ProviderId)] };
            var operations = new FileOperations();
            var inventory = new OptimizationInventory(scan, findings, true, false);
            int executions = 0, rechecks = 0;
            async Task Refresh(CancellationToken token)
            {
                var updated = await operations.RecheckAsync(inventory.Scan, [new(fixture.Path)], token);
                inventory = new(updated, DeveloperOpportunityFixture.Find(updated), true, false);
            }
            var window = new OptimizationWindow(scan, findings, operations, new(policy), _ => { }, async (preview, chosen, token) =>
            {
                Assert.True(inventory.IsCurrent);
                executions++;
                var result = await operations.OptimizeAsync(inventory.Scan, inventory.Findings, preview, chosen,
                    DeveloperOpportunityFixture.Find, token, (_, _) => { });
                if (refreshFailure && executions == 1) inventory = inventory with { IsCurrent = false };
                else await Refresh(token);
                return result;
            })
            {
                ReadInventory = () => inventory,
                RecheckInventory = async token =>
                {
                    rechecks++;
                    if (rechecks == 1) throw new IOException("Fixture metadata temporarily unavailable.");
                    await Refresh(token);
                }
            };
            try
            {
                window.ShowActivated = false; window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.Show();
                var preview = (Button)window.FindName("PreviewButton"); var run = (Button)window.FindName("RunButton");
                var files = (DataGrid)window.FindName("FileList"); var results = (DataGrid)window.FindName("ResultList");
                void Review()
                {
                    preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
                }
                Review(); Assert.True(run.IsEnabled);
                string firstPath = Assert.Single(files.Items.Cast<OptimizationWindow.DisplayFile>()).Path;
                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
                Assert.Equal(1, executions); Assert.False(run.IsEnabled); Assert.Single(results.Items.Cast<object>());
                Assert.False(window.RequestedRescan);
                if (refreshFailure)
                {
                    Assert.Equal("Recheck affected folders", preview.Content);
                    Review(); // A read failure must retain receipts and must not resubmit deletion.
                    Assert.Equal(1, executions); Assert.Equal(1, rechecks); Assert.False(run.IsEnabled);
                    Assert.Single(results.Items.Cast<object>());
                    Assert.Equal("Recheck affected folders", preview.Content);
                }
                else Assert.Equal("Review files", preview.Content);
                Review(); Assert.True(run.IsEnabled);
                Assert.NotEqual(firstPath, Assert.Single(files.Items.Cast<OptimizationWindow.DisplayFile>()).Path);
                Assert.Single(results.Items.Cast<object>()); Assert.False(window.RequestedRescan);
                Assert.Equal(refreshFailure ? 2 : 0, rechecks);
                ((TabItem)window.FindName("GeneratedFilesTab")).IsSelected = true;
                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
                Assert.Equal(2, executions); Assert.Empty(Directory.GetFiles(Path.Join(fixture.Path, "__pycache__")));
                inventory = inventory with { IsCurrent = false, RequiresFullRescan = true };
                Review();
                Assert.True(window.RequestedRescan); Assert.Equal(2, executions);
            }
            finally { window.Close(); }
        }
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
            Assert.Equal(24, pages); Assert.Equal("Review files", preview.Content);
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
            var button = (Button)host.FindName("OptimizationButton");
            var hint = (TextBlock)host.FindName("OptimizationHint");
            Assert.False(button.IsEnabled); Assert.Equal("Scan for generated files", hint.Text);
            host.SetBusy(true); Assert.False(button.IsEnabled); Assert.Equal("Scan for generated files", hint.Text);
            host.SetBusy(false);
            Assert.True(ToolTipService.GetShowOnDisabled(button));
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme;
                string name = theme == ThemeMode.Light ? "light" : "dark";
                Render(host, Path.Join(FindRepository(), "artifacts", "ui", $"optimization-scan-first-{name}.png"), 1020, 820);
            }
#pragma warning restore WPF0001
            var scan = host.ScanCurrentPathAsync();
            Assert.False(button.IsEnabled); Assert.Equal("Available after scan", hint.Text);
            Assert.Contains("scan to finish", System.Windows.Automation.AutomationProperties.GetHelpText(button));
            Render(host, Path.Join(FindRepository(), "artifacts", "ui", "optimization-scanning-dark.png"), 1020, 820);
            PumpUntil(() => scan.IsCompleted, TimeSpan.FromSeconds(15)); Assert.True(scan.GetAwaiter().GetResult());
            Assert.True(button.IsEnabled); Assert.Equal(Visibility.Collapsed, hint.Visibility);
            host.SetBusy(true);
            Assert.False(button.IsEnabled); Assert.Equal("Available when idle", hint.Text);
            host.SetBusy(false); Assert.True(button.IsEnabled);
            Render(host, Path.Join(FindRepository(), "artifacts", "ui", "optimization-ready-dark.png"), 1020, 820);
#pragma warning disable WPF0001
            app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
            var disabled = host.TryAutomaticOptimizationAsync(); PumpUntil(() => disabled.IsCompleted, TimeSpan.FromSeconds(5)); disabled.GetAwaiter().GetResult();
            Assert.Equal(2, Directory.GetFiles(Path.Join(source.Path, "__pycache__")).Length);
            var snapshot = (ScanSnapshot)typeof(MainWindow).GetField("_snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
            var finding = Assert.Single(DeveloperOpportunityFixture.Find(snapshot));
            var policy = new OptimizationPolicy { Enabled = true, ScopePath = source.Path, MaxFilesPerRun = 1,
                EnabledRules = [finding.AutomaticCleanup!.ConsentKey(finding.ProviderId)] };
            CheckOptimizationPreparationFailures(host, snapshot, policy, source.Path, state.Path);
            new OptimizationSettings(policy).Save(host.OptimizationSettingsPath); host.InitializeOptimization();
            var automatic = host.TryAutomaticOptimizationAsync(); PumpUntil(() => automatic.IsCompleted, TimeSpan.FromSeconds(15)); automatic.GetAwaiter().GetResult();
            Assert.Single(Directory.GetFiles(Path.Join(source.Path, "__pycache__")));
            var refreshedInventory = Assert.IsType<OptimizationInventory>(host.ReadOptimizationInventory(source.Path));
            Assert.True(refreshedInventory.IsCurrent); Assert.NotSame(snapshot, refreshedInventory.Scan);
            Assert.Null(host.ReadOptimizationInventory(Path.Join(source.Path, "other-scope")));
            Assert.NotNull(OptimizationSettings.Load(host.OptimizationSettingsPath).LastRunUtc);
            var throttled = host.TryAutomaticOptimizationAsync(); PumpUntil(() => throttled.IsCompleted, TimeSpan.FromSeconds(5)); throttled.GetAwaiter().GetResult();
            Assert.Single(Directory.GetFiles(Path.Join(source.Path, "__pycache__")));
            var previous = OptimizationSettings.Load(host.OptimizationSettingsPath);
            (previous with { LastRunUtc = DateTime.UtcNow.AddHours(-2) }).Save(host.OptimizationSettingsPath); host.InitializeOptimization();
            var next = host.TryAutomaticOptimizationAsync(); PumpUntil(() => next.IsCompleted, TimeSpan.FromSeconds(15)); next.GetAwaiter().GetResult();
            Assert.Empty(Directory.GetFiles(Path.Join(source.Path, "__pycache__")));
            Assert.Equal(2, Directory.GetFiles(source.Path, "*.py").Length);
            Assert.Equal(2, Directory.GetFiles(Path.Join(state.Path, "logs", "Optimization"), "*.jsonl").Length);
            var snapshotField = typeof(MainWindow).GetField("_snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var retainedScan = (ScanSnapshot)snapshotField.GetValue(host)!;
            var tree = (ListBox)host.FindName("SpaceTree");
            var retainedRows = tree.ItemsSource;
            var retainedSelection = tree.SelectedItem;
            string retainedPath = host.CurrentPath;
            host.MarkScanStale("Fixture inventory requires a full rescan.", [new(retainedScan.RootPath)]);
            host.OfferFullInventoryRescan(retainedScan);
            var recoveryInventory = Assert.IsType<OptimizationInventory>(host.ReadOptimizationInventory(source.Path));
            Assert.False(recoveryInventory.IsCurrent); Assert.True(recoveryInventory.RequiresFullRescan);
            var cancelled = host.ScanCurrentPathAsync();
            Assert.False(button.IsEnabled); Assert.Equal("Available after scan", hint.Text);
            Assert.Same(retainedScan, snapshotField.GetValue(host));
            Assert.Same(retainedRows, tree.ItemsSource);
            Assert.False(((FrameworkElement)host.FindName("ExplorePanel")).IsEnabled);
            ((Button)host.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => cancelled.IsCompleted, TimeSpan.FromSeconds(15)); Assert.False(cancelled.GetAwaiter().GetResult());
            Assert.True(button.IsEnabled); Assert.Equal(Visibility.Collapsed, hint.Visibility);
            Assert.Same(retainedScan, snapshotField.GetValue(host));
            Assert.Same(retainedRows, tree.ItemsSource); Assert.Same(retainedSelection, tree.SelectedItem);
            Assert.Equal(retainedPath, host.CurrentPath);
            Assert.Equal("Rescan inventory", ((Button)host.FindName("ScanButton")).Content);
            Assert.Contains("Previous inventory", ((TextBlock)host.FindName("StatusText")).Text);

            // A disconnected target must retain the earlier results and their
            // recovery requirement, without treating a failed read as mutation.
            string unavailable = source.Path + "-unavailable";
            Directory.Move(source.Path, unavailable);
            try
            {
                var unavailableScan = host.ScanCurrentPathAsync();
                PumpUntil(() => unavailableScan.IsCompleted, TimeSpan.FromSeconds(15)); Assert.False(unavailableScan.GetAwaiter().GetResult());
                Assert.Same(retainedScan, snapshotField.GetValue(host));
                Assert.Same(retainedRows, tree.ItemsSource); Assert.Same(retainedSelection, tree.SelectedItem);
                Assert.Equal(retainedPath, host.CurrentPath);
                Assert.Equal("Rescan inventory", ((Button)host.FindName("ScanButton")).Content);
                Assert.Contains("Previous inventory", ((TextBlock)host.FindName("StatusText")).Text);
            }
            finally { Directory.Move(unavailable, source.Path); }
            var recovered = host.NavigatePathAsync(source.Path, forceScan: true);
            PumpUntil(() => recovered.IsCompleted, TimeSpan.FromSeconds(15)); Assert.True(recovered.GetAwaiter().GetResult());
            Assert.NotSame(retainedScan, snapshotField.GetValue(host));
            Assert.Equal("Scan", ((Button)host.FindName("ScanButton")).Content);
            host.SetScanTarget(Path.Join(source.Path, "missing"));
            Assert.Null(snapshotField.GetValue(host)); // New scopes cannot inherit unrelated results.
            var failed = host.ScanCurrentPathAsync();
            PumpUntil(() => failed.IsCompleted, TimeSpan.FromSeconds(15)); Assert.False(failed.GetAwaiter().GetResult());
            Assert.True(button.IsEnabled); Assert.Equal("Scan for generated files", hint.Text);
        }
        finally
        {
            host.Close(); PumpUntil(() => !app.Windows.Cast<Window>().Contains(host), TimeSpan.FromSeconds(10));
        }
    }

    private static void CheckOptimizationPreparationFailures(MainWindow host, ScanSnapshot snapshot,
        OptimizationPolicy policy, string source, string state)
    {
        string settings = host.OptimizationSettingsPath, logs = host.LogDirectory;
        var snapshotField = typeof(MainWindow).GetField("_snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var staleField = typeof(MainWindow).GetField("_scanStale", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var settingsField = typeof(MainWindow).GetField("_optimizationSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var tree = (ListBox)host.FindName("SpaceTree");
        var rows = tree.ItemsSource; var selected = tree.SelectedItem;
        string[] files = Directory.GetFiles(Path.Join(source, "__pycache__"));
        void CheckUnchanged()
        {
            Assert.Same(snapshot, snapshotField.GetValue(host));
            Assert.False((bool)staleField.GetValue(host)!);
            Assert.Same(rows, tree.ItemsSource); Assert.Same(selected, tree.SelectedItem);
            Assert.Equal(files, Directory.GetFiles(Path.Join(source, "__pycache__")));
            Assert.NotNull(((OptimizationSettings)settingsField.GetValue(host)!).LastRunUtc);
        }
        void Attempt()
        {
            var operation = host.TryAutomaticOptimizationAsync();
            PumpUntil(() => operation.IsCompleted, TimeSpan.FromSeconds(10)); operation.GetAwaiter().GetResult();
        }
        try
        {
            new OptimizationSettings(policy).Save(settings); host.InitializeOptimization();
            string blockedLogs = Path.Join(state, "blocked-optimization-log");
            File.WriteAllText(blockedLogs, "Owned fixture file blocks a directory.");
            host.LogDirectory = blockedLogs;
            Attempt(); CheckUnchanged();
            Assert.Contains("writable activity log", ((TextBlock)host.FindName("StatusText")).Text);
            var lastAttempt = ((OptimizationSettings)settingsField.GetValue(host)!).LastRunUtc;
            host.LogDirectory = logs;
            Attempt(); CheckUnchanged();
            Assert.Equal(lastAttempt, ((OptimizationSettings)settingsField.GetValue(host)!).LastRunUtc);

            new OptimizationSettings(policy).Save(settings); host.InitializeOptimization();
            host.LogDirectory = Path.Join(state, "preparation-logs");
            host.OptimizationSettingsPath = Directory.CreateDirectory(Path.Join(state, "settings-destination-is-a-folder")).FullName;
            Attempt(); CheckUnchanged();
            Assert.Equal("Scan", ((Button)host.FindName("ScanButton")).Content);
        }
        finally
        {
            host.LogDirectory = logs; host.OptimizationSettingsPath = settings;
            new OptimizationSettings(policy).Save(settings); host.InitializeOptimization();
        }
    }
}
