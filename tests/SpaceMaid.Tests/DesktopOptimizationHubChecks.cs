using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckOptimizationHub(Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory(); using var native = new ToolCacheFixture();
        OptimizationTests.MakePython(fixture.Path, "first"); OptimizationTests.MakePython(fixture.Path, "second"); OptimizationTests.Age(fixture.Path);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(10)); var scan = scanning.GetAwaiter().GetResult();
        var findings = DeveloperOpportunityFixture.Find(scan);
        ISpaceMaidExtension[] providers = [new SpaceMaid.Extensions.Developer.DeveloperExtension(),
            new SpaceMaid.Extensions.Containers.ContainersExtension(), new SpaceMaid.Extensions.Models.ModelsExtension(), new SpaceMaid.Extensions.Agents.AgentsExtension()];
        var discovery = OptimizationCatalog.Discover(providers); Assert.Empty(discovery.Errors); Assert.Equal(10, discovery.Actions.Count);
        var integration = new OptimizationIntegration(fixture.Path, discovery,
            providers.ToDictionary(provider => provider.Descriptor.Id, provider => provider.Descriptor.Name), native.Service,
            (plan, token) => native.Service.ExecuteAsync(plan, token));
        string saved = Path.Join(fixture.Path, "settings.json");
        var policy = new OptimizationPolicy { ScopePath = fixture.Path };
        var window = new OptimizationWindow(scan, findings, new FileOperations(), new(policy),
            chosen => new OptimizationSettings(chosen).Save(saved), (_, _, _) => throw new InvalidOperationException("This fixture never deletes generated files."), integration: integration);
        try
        {
            window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000; window.Top = -10000; window.Show();
            var actions = (ListBox)window.FindName("ActionList"); var preview = (Button)window.FindName("PreviewButton");
            var inspect = (Button)window.FindName("InspectToolButton"); var review = (Button)window.FindName("ReviewNativeButton");
            var run = (Button)window.FindName("RunButton"); var files = (DataGrid)window.FindName("FileList");
            var generated = actions.Items.Cast<OptimizationActionRow>().Single(row => row.Rule is not null);
            Assert.Empty(native.Commands); Assert.False(((CheckBox)window.FindName("AutomaticBox")).IsChecked);
            actions.SelectedItem = generated;
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.Equal(2, files.Items.Count); Assert.True(run.IsEnabled);
            ((TabItem)window.FindName("AutomaticTab")).IsSelected = true;
            Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("PreviewText")).Visibility);
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var consent = OptimizationSettings.Load(saved).Policy; Assert.False(consent.Enabled); Assert.Empty(consent.EnabledRules);
            Assert.False(run.IsEnabled);
            ((TabItem)window.FindName("ActionsTab")).IsSelected = true;
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme; string name = theme == ThemeMode.Light ? "light" : "dark";
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"optimize-generated-{name}.png"), 1100, 800);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"optimize-generated-compact-{name}.png"), 840, 640);
                Assert.True(files.ActualHeight >= 180);
                ((TabItem)window.FindName("AutomaticTab")).IsSelected = true;
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"optimize-automatic-{name}.png"), 1100, 800);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"optimize-automatic-compact-{name}.png"), 840, 640);
                ((TabItem)window.FindName("ActionsTab")).IsSelected = true;
            }
#pragma warning restore WPF0001
            var pnpm = actions.Items.Cast<OptimizationActionRow>().Single(row => row.Contribution?.Action.NativeTool == ToolCacheKind.Pnpm);
            actions.SelectedItem = pnpm; Assert.False(run.IsEnabled); Assert.Equal(Visibility.Collapsed, run.Visibility);
            inspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => inspect.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.True(review.IsEnabled); Assert.Contains(native.PnpmRoot, ((TextBlock)window.FindName("NativeRootText")).Text);
            Assert.All(native.Commands, command => Assert.Equal(native.Launcher(ToolCacheKind.Pnpm).Executable, command.Executable));
            native.StallInspection = true;
            inspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => native.InspectionStarted.Task.IsCompleted, TimeSpan.FromSeconds(10));
            ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => inspect.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.False(review.IsEnabled); Assert.Equal(Visibility.Collapsed, review.Visibility); Assert.Null(pnpm.Observation);
            native.StallInspection = false;
            inspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => inspect.IsEnabled, TimeSpan.FromSeconds(10)); Assert.True(review.IsEnabled);
            window.ConfirmMaintenance = plan => { Assert.Equal(2, plan.Scopes.Count); Assert.Contains(native.PnpmCacheRoot, plan.ConfirmationScopes); return false; };
            review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => inspect.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(native.Commands, command => command.Arguments.Contains("prune"));
            window.AddExclusions([Path.Join(native.PnpmCacheRoot, "keep")]);
            ((TabItem)window.FindName("ActionsTab")).IsSelected = true;
            window.ConfirmMaintenance = _ => throw new InvalidOperationException("Excluded native scopes must be blocked before confirmation.");
            review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => inspect.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.Contains("excluded location", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.DoesNotContain(native.Commands, command => command.Arguments.Contains("prune"));
            ((TabItem)window.FindName("ExclusionsTab")).IsSelected = true; window.UpdateLayout();
            Descendants((ListBox)window.FindName("ExclusionsList")).OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TabItem)window.FindName("ActionsTab")).IsSelected = true;
            window.ConfirmMaintenance = _ => true;
            review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => inspect.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.Single(native.Commands, command => command.Arguments.Contains("prune"));
            Assert.Equal(Visibility.Collapsed, review.Visibility); Assert.Contains("Native fixture report", ((TextBox)window.FindName("ReportText")).Text);
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme; string name = theme == ThemeMode.Light ? "light" : "dark";
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"optimize-native-{name}.png"), 1100, 800);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"optimize-native-compact-{name}.png"), 840, 640);
            }
#pragma warning restore WPF0001
            actions.SelectedItem = actions.Items.Cast<OptimizationActionRow>().Single(row => row.Contribution?.Action.NativeTool == ToolCacheKind.NuGet);
            inspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => inspect.IsEnabled, TimeSpan.FromSeconds(10));
            Assert.Equal(Visibility.Collapsed, review.Visibility);
            ((TextBox)window.FindName("ActionSearch")).Text = "Ollama"; Assert.Single(actions.Items);
            ((Button)window.FindName("OpenWorkflowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("space-maid.models", window.RequestedWorkflow?.OwnerId); Assert.Equal("ollama", window.RequestedWorkflow?.Action.Workflow);

            var noScan = new OptimizationWindow(null, [], new FileOperations(), new(policy with { Enabled = true,
                EnabledRules = ["previous-consent"], ScopePath = fixture.Path }), chosen => new OptimizationSettings(chosen).Save(saved),
                (_, _, _) => throw new InvalidOperationException("No scan can authorize generated deletion."), integration: integration);
            try
            {
                noScan.ShowActivated = false; noScan.ShowInTaskbar = false; noScan.WindowStartupLocation = WindowStartupLocation.Manual; noScan.Left = -10000; noScan.Top = -10000; noScan.Show();
                Assert.True(((CheckBox)noScan.FindName("AutomaticBox")).IsEnabled);
                noScan.AddExclusions([Path.Join(fixture.Path, "keep")]);
                ((Button)noScan.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var retained = OptimizationSettings.Load(saved).Policy; Assert.True(retained.Enabled); Assert.Equal(new[] { "previous-consent" }, retained.EnabledRules); Assert.Equal(fixture.Path, retained.ScopePath);
                ((TabItem)noScan.FindName("AutomaticTab")).IsSelected = true;
                ((CheckBox)noScan.FindName("AutomaticBox")).IsChecked = false;
                ((Button)noScan.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var stopped = OptimizationSettings.Load(saved).Policy; Assert.False(stopped.Enabled); Assert.Equal(retained.ScopePath, stopped.ScopePath); Assert.Equal(retained.EnabledRules, stopped.EnabledRules);
            }
            finally { noScan.Close(); }
            var savedElsewhere = policy with { Enabled = true, ScopePath = Path.Join(fixture.Path, "other-drive"), EnabledRules = ["previous-consent"] };
            var scopeWindow = new OptimizationWindow(scan, findings, new FileOperations(), new(savedElsewhere), chosen => new OptimizationSettings(chosen).Save(saved),
                (_, _, _) => throw new InvalidOperationException("Scope editing must not delete files."),
                integration: integration with { NativeTools = null, ExecuteNative = null, Discovery = discovery with { Errors = ["Native inspection unavailable in this fixture."] } });
            try
            {
                scopeWindow.ShowActivated = false; scopeWindow.ShowInTaskbar = false; scopeWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                scopeWindow.Left = -10000; scopeWindow.Top = -10000; scopeWindow.Show();
                var unavailableActions = (ListBox)scopeWindow.FindName("ActionList");
                unavailableActions.SelectedItem = unavailableActions.Items.Cast<OptimizationActionRow>().Single(row => row.Contribution?.Action.NativeTool == ToolCacheKind.Pnpm);
                Assert.False(((Button)scopeWindow.FindName("InspectToolButton")).IsEnabled);
                Assert.Contains("unavailable", ((TextBlock)scopeWindow.FindName("NativeStatusText")).Text);
                scopeWindow.AddExclusions([Path.Join(fixture.Path, "keep")]);
                var saveButton = (Button)scopeWindow.FindName("SaveButton"); saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var preserved = OptimizationSettings.Load(saved).Policy;
                Assert.True(preserved.Enabled); Assert.Equal(savedElsewhere.ScopePath, preserved.ScopePath); Assert.Equal(savedElsewhere.EnabledRules, preserved.EnabledRules);
                Assert.Contains("elsewhere", ((Button)scopeWindow.FindName("AutomaticStatusButton")).Content.ToString());
                ((TabItem)scopeWindow.FindName("AutomaticTab")).IsSelected = true;
                Render(scopeWindow, Path.Join(artifactRoot, "artifacts", "ui", "optimize-automatic-other-scope-dark.png"), 840, 640);
                ((CheckBox)scopeWindow.FindName("AutomaticBox")).IsChecked = true;
                ((ItemsControl)scopeWindow.FindName("RuleList")).Items.Cast<OptimizationWindow.RuleRow>().Single().Enabled = true;
                saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var replaced = OptimizationSettings.Load(saved).Policy; Assert.True(replaced.Enabled); Assert.Equal(fixture.Path, replaced.ScopePath); Assert.Single(replaced.EnabledRules);
                scopeWindow.AddExclusions([Path.Join(fixture.Path, "also-keep")]); saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var later = OptimizationSettings.Load(saved).Policy; Assert.True(later.Enabled); Assert.Equal(replaced.ScopePath, later.ScopePath); Assert.Equal(replaced.EnabledRules, later.EnabledRules);
                ((TabItem)scopeWindow.FindName("ActionsTab")).IsSelected = true;
                var actionList = (ListBox)scopeWindow.FindName("ActionList"); actionList.SelectedItem = actionList.Items.Cast<OptimizationActionRow>().Single(row => row.Rule is not null);
                var reviewFiles = (Button)scopeWindow.FindName("PreviewButton"); reviewFiles.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => reviewFiles.IsEnabled, TimeSpan.FromSeconds(10));
                Assert.Equal(2, ((DataGrid)scopeWindow.FindName("FileList")).Items.Count);
            }
            finally { scopeWindow.Close(); }
            Assert.Equal(2, Directory.GetFiles(Path.Join(fixture.Path, "__pycache__")).Length);
        }
        finally
        {
            window.Close(); foreach (var provider in providers) provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning disable WPF0001
            app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        }
    }

    private static void CheckOptimizationHostIntegration(Application app)
    {
        using var fixture = new TestDirectory();
        var existing = app.Windows.OfType<MainWindow>().FirstOrDefault();
        var host = existing ?? new MainWindow { LogDirectory = Path.Join(fixture.Path, "logs"), ExtensionDataRoot = Path.Join(fixture.Path, "extensions"),
            DriveHistoryPath = Path.Join(fixture.Path, "drives.json"), ColumnLayoutPath = Path.Join(fixture.Path, "columns.json"),
            CleanupSettingsPath = Path.Join(fixture.Path, "cleanup.json"), OptimizationSettingsPath = Path.Join(fixture.Path, "optimization.json"),
            WorktreeLifecycleSettingsPath = Path.Join(fixture.Path, "worktrees.json"), UpdateSettingsPath = Path.Join(fixture.Path, "updates.json") };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            if (existing is null)
            {
                Directory.CreateDirectory(host.ExtensionDataRoot!);
                var disabled = Directory.GetDirectories(Path.Join(AppContext.BaseDirectory, "Extensions")).Select(Path.GetFileName)
                    .Where(id => id is not ("space-maid.developer" or "space-maid.containers" or "space-maid.models" or "space-maid.agents")).Select(id => id!).ToList();
                File.WriteAllText(Path.Join(host.ExtensionDataRoot!, "extensions.json"), JsonSerializer.Serialize(new ExtensionSettings([], disabled)));
                File.WriteAllText(Path.Join(fixture.Path, "owned.txt"), "owned fixture");
                host.SetScanTarget(fixture.Path); var scanning = host.ScanCurrentPathAsync();
                PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(15)); Assert.True(scanning.GetAwaiter().GetResult());
                host.InitializeOptimization(); typeof(MainWindow).GetField("_startupOffered", flags)!.SetValue(host, true);
                host.ShowActivated = false; host.ShowInTaskbar = false; host.Left = -10000; host.Top = -10000; host.Show();
            }
            var runtime = (ExtensionRuntime)typeof(MainWindow).GetField("_extensions", flags)!.GetValue(host)!;
            var discovery = runtime.DiscoverOptimization(); Assert.Empty(discovery.Errors); Assert.Equal(10, discovery.Actions.Count);
            foreach (var contribution in discovery.Actions.Where(action => action.Action.Workflow is not null))
            {
                var entry = runtime.Entries.Single(entry => entry.Id == contribution.OwnerId);
                var route = Assert.IsAssignableFrom<IOptimizationWorkflowView>(entry.View);
                Assert.True(host.OpenExtension(entry.Id)); Assert.True(route.OpenOptimizationWorkflow(contribution.Action.Workflow!));
                Assert.False(route.IsOptimizationBusy); Assert.False(route.OpenOptimizationWorkflow("unknown"));
            }
            var containers = runtime.Entries.Single(entry => entry.Id == "space-maid.containers").View!;
            var disk = (FrameworkElement)((ContentControl)containers.FindName("WslContent")).Content;
            var busyField = disk.GetType().GetField("_busy", flags)!;
            try
            {
                busyField.SetValue(disk, true); Assert.True(runtime.HasOptimizationWork);
                Assert.False((bool)typeof(MainWindow).GetMethod("CanRestartForUpdate", flags)!.Invoke(host, null)!);
                Assert.False(((IOptimizationWorkflowView)containers).OpenOptimizationWorkflow("docker"));
            }
            finally { busyField.SetValue(disk, false); }
            using var native = new ToolCacheFixture();
            var inspect = native.Service.InspectAsync(ToolCacheKind.Pnpm); PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(10));
            var prepare = native.Service.PrepareAsync(inspect.GetAwaiter().GetResult()); PumpUntil(() => prepare.IsCompleted, TimeSpan.FromSeconds(10));
            native.StallMutation = true;
            var operation = (Task<ToolCacheRunResult>)typeof(MainWindow).GetMethod("ExecuteToolCacheAsync", flags)!.Invoke(host, [native.Service, prepare.GetAwaiter().GetResult(), CancellationToken.None])!;
            PumpUntil(() => native.MutationStarted.Task.IsCompleted, TimeSpan.FromSeconds(10));
            Assert.False(((Grid)host.FindName("ExtensionPages")).IsEnabled);
            ((Button)host.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => operation.IsCompleted, TimeSpan.FromSeconds(10)); Assert.True(operation.GetAwaiter().GetResult().Cancelled);
            Assert.True(((Grid)host.FindName("ExtensionPages")).IsEnabled);
            if (existing is not null) ((RadioButton)host.FindName("ExploreTab")).IsChecked = true;
        }
        finally { if (existing is null) { host.Close(); PumpUntil(() => !app.Windows.Cast<Window>().Contains(host), TimeSpan.FromSeconds(10)); } }
    }
}
