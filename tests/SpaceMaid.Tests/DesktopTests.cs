using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    [Fact]
    public void FluentDesktopLoadsScansPreviewsAndConfirmedFolderRecycling()
    {
        Exception? failure = null;
        string stage = "starting";
        var elapsed = Stopwatch.StartNew();
        string workflow = "navigation and folder operations";
        long workflowStarted = Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource();
        string timeline = Path.Join(FindRepository(), "artifacts", "desktop-workflows.tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(timeline)!);
        File.WriteAllText(timeline, "seconds\tstage\n");
        void SetStage(string value)
        {
            timeout.Token.ThrowIfCancellationRequested();
            stage = value;
            File.AppendAllText(timeline, $"{elapsed.Elapsed.TotalSeconds:F3}\t{value}\n");
        }
        void BeginWorkflow(string value)
        {
            Volatile.Write(ref workflow, value);
            Interlocked.Exchange(ref workflowStarted, Stopwatch.GetTimestamp());
            SetStage(value);
        }
        var thread = new Thread(() =>
        {
            _desktopCancellation = timeout.Token;
            Application? app = null;
            TestDirectory? fixture = null;
            MainWindow? window = null;
            try
            {
                // The component harness bypasses App.Main; reproduce its updater
                // startup before constructing views, including release metadata builds.
                Velopack.VelopackApp.Build().SetArgs([]).SetAutoApplyOnStartup(false).Run();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                fixture = new TestDirectory();
                string project = Directory.CreateDirectory(Path.Join(fixture.Path, "sample")).FullName;
                File.WriteAllText(Path.Join(project, "sample.csproj"), "<Project />");
                string bin = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
                File.WriteAllBytes(Path.Join(bin, "sample.dll"), new byte[8192]);
                BuildActivityFixture.AgeProject(project);
                SetStage("loading application resources");
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001
                app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SpaceMaid.App;component/Styles.xaml", UriKind.Relative) });
                using (var cancelledWait = new CancellationTokenSource())
                {
                    cancelledWait.Cancel();
                    _desktopCancellation = cancelledWait.Token;
                    try { Assert.Throws<OperationCanceledException>(() => PumpUntil(() => false, TimeSpan.FromSeconds(1))); }
                    finally { _desktopCancellation = timeout.Token; }
                }
                SetStage("constructing main window");
                window = new MainWindow();
                window.LogDirectory = Path.Join(fixture.Path, "diagnostics");
                window.ExtensionDataRoot = Path.Join(fixture.Path, "extensions");
                window.DriveHistoryPath = Path.Join(fixture.Path, "drive-history.json");
                window.ColumnLayoutPath = Path.Join(fixture.Path, "columns.json");
                window.CleanupSettingsPath = Path.Join(fixture.Path, "cleanup-settings.json");
                window.OptimizationSettingsPath = Path.Join(fixture.Path, "optimization-settings.json");
                window.WorktreeLifecycleSettingsPath = Path.Join(fixture.Path, "managed-worktrees.json");
                window.UpdateSettingsPath = Path.Join(fixture.Path, "update-settings.json");
                window.Operations = new(recycleBins: new(_ => "fixture-volume", _ => (0, 0), (_, _) => throw new InvalidOperationException("Unexpected empty-bin call.")));
                window.LoadBuildCleanupSettings();
                string fixtureRoot = Path.GetPathRoot(fixture.Path)!;
                window.DriveProvider = () => [new(fixtureRoot, "Development", DriveDiscovery.VolumeId(fixtureRoot), true, GiB(500), GiB(20)),
                    new(@"R:\", "", null, false, UnavailableReason: "Not available"), new(@"S:\", "Network drive", null, true)];
                window.DriveSpaceProvider = _ => (GiB(500), GiB(17.5));
                // Exercise real native layout offscreen. Detached content can be
                // remeasured with an unbounded viewport by ScrollIntoView.
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -10000; window.Top = -10000;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    foreach (var choice in app.Windows.OfType<StartupWindow>().ToArray()) choice.Close();
                }));
                window.Show();
                // Startup uses a modal choice and asynchronous extension loading.
                // Begin interactions only once both have finished, so their
                // focus changes cannot interrupt the path editor assertions.
                PumpUntil(() => window.ExtensionsReady && !app.Windows.OfType<StartupWindow>().Any(), TimeSpan.FromSeconds(15));
                SetStage("keeping the build retention preference bounded and persistent");
                var buildRetention = (TextBox)window.FindName("BuildKeepRecentDaysBox");
                Assert.Equal("3", buildRetention.Text);
                File.WriteAllText(window.CleanupSettingsPath, "{invalid");
                window.LoadBuildCleanupSettings();
                Assert.Contains("unavailable", ((TextBlock)window.FindName("StatusText")).Text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("{invalid", File.ReadAllText(window.CleanupSettingsPath));
                buildRetention.Text = "7";
                Assert.True(window.SaveBuildRetention());
                Assert.Equal(7, window.CurrentBuildPolicy.KeepRecentDays);
                Assert.Equal(7, BuildCleanupSettings.Load(window.CleanupSettingsPath).KeepRecentDays);
                buildRetention.Text = "0";
                Assert.False(window.SaveBuildRetention()); Assert.Equal(7, window.CurrentBuildPolicy.KeepRecentDays);
                buildRetention.Text = "3";
                Assert.True(window.SaveBuildRetention()); Assert.Equal(3, window.CurrentBuildPolicy.KeepRecentDays);
                ((ToggleButton)window.FindName("ViewOptionsToggle")).IsChecked = false;
                SetStage("choosing a drive without starting a scan");
                var drivesReady = window.RefreshDriveChoicesAsync();
                PumpUntil(() => drivesReady.IsCompleted, TimeSpan.FromSeconds(3)); drivesReady.GetAwaiter().GetResult();
                var drivesList = (ItemsControl)window.FindName("DriveChoices");
                var fixtureChoice = drivesList.Items.Cast<DriveChoice>().Single(choice => choice.RootPath == fixtureRoot);
                var unavailableChoice = drivesList.Items.Cast<DriveChoice>().Single(choice => !choice.Ready);
                string initialTarget = window.CurrentPath;
                window.ChooseDriveAsync(unavailableChoice).GetAwaiter().GetResult(); Assert.Equal(initialTarget, window.CurrentPath);
                var chooseDrive = window.ChooseDriveAsync(fixtureChoice);
                PumpUntil(() => chooseDrive.IsCompleted, TimeSpan.FromSeconds(3)); chooseDrive.GetAwaiter().GetResult();
                Assert.Equal(fixtureRoot, window.CurrentPath);
                Assert.Null(typeof(MainWindow).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.Empty(Directory.Exists(window.LogDirectory) ? Directory.GetFiles(window.LogDirectory, "*.jsonl") : []);
                SetStage("keeping the fast-scan enable button in sync with session state");
                var enableFast = (Button)window.FindName("ElevateButton");
                var fastFlag = typeof(MainWindow).GetField("_fastEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var availableVisibility = Elevation.IsAdministrator ? Visibility.Collapsed : Visibility.Visible;
                Assert.Equal(availableVisibility, enableFast.Visibility);
                window.SetBusy(true);
                Assert.Equal(availableVisibility, enableFast.Visibility); Assert.False(enableFast.IsEnabled);
                // Simulate successful opt-in without requesting UAC or reading a user volume.
                fastFlag.SetValue(window, true);
                window.SetBusy(false);
                Assert.Equal(Visibility.Collapsed, enableFast.Visibility); Assert.False(enableFast.IsEnabled);
                Assert.Null(typeof(MainWindow).GetField("_scanHelper", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                // The enable control reflects the session choice while work is busy.
                window.SetBusy(true); window.SetBusy(false);
                Assert.Equal(Visibility.Collapsed, enableFast.Visibility);
                // Cancellation/failure resets the choice; retry is offered only when idle.
                fastFlag.SetValue(window, false);
                window.SetBusy(true);
                Assert.Equal(availableVisibility, enableFast.Visibility); Assert.False(enableFast.IsEnabled);
                window.SetBusy(false);
                Assert.Equal(availableVisibility, enableFast.Visibility); Assert.Equal(!Elevation.IsAdministrator, enableFast.IsEnabled);
                var fixtureDrive = new DriveInfo(fixture.Path);
                SetStage("returning to the drive root without starting a scan");
                window.SetScanTarget(project);
                var rootButton = (Button)window.FindName("DriveRootButton");
                rootButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => window.CurrentPath == fixtureRoot && ((Border)window.FindName("PathBar")).IsEnabled, TimeSpan.FromSeconds(3));
                Assert.Empty(Directory.Exists(window.LogDirectory) ? Directory.GetFiles(window.LogDirectory, "*.jsonl") : []);
                Assert.Empty(((StackPanel)window.FindName("Breadcrumbs")).Children.OfType<Button>());
                window.SetScanTarget(fixture.Path);
                var editor = (TextBox)window.FindName("PathEditor");
                ((Button)window.FindName("EditPathButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(fixture.Path, editor.Text); Assert.Equal(editor.Text.Length, editor.SelectionLength);
                Assert.Equal(Visibility.Collapsed, rootButton.Visibility);
                Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("DriveSwitchLabel")).Visibility);
                editor.Text = Path.Join(fixture.Path, "missing-folder");
                var invalidPath = window.NavigatePathAsync(editor.Text);
                PumpUntil(() => invalidPath.IsCompleted, TimeSpan.FromSeconds(3)); Assert.False(invalidPath.GetAwaiter().GetResult());
                Assert.Equal(fixture.Path, window.CurrentPath); Assert.Equal(Visibility.Visible, editor.Visibility);
                var escapePath = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
                editor.RaiseEvent(escapePath); Assert.True(escapePath.Handled);
                Assert.Equal(Visibility.Collapsed, editor.Visibility); Assert.Equal(fixture.Path, editor.Text);
                Assert.Equal(Visibility.Visible, rootButton.Visibility);
                var scanButton = (Button)window.FindName("ScanButton");
                SetStage("scanning fixture through the UI");
                ((Button)window.FindName("EditPathButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editor.Text = fixture.Path;
                scanButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((Button)window.FindName("OpenLogButton")).Visibility == Visibility.Visible, TimeSpan.FromSeconds(3));
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("OpenLogButton")).Visibility);
                PumpUntil(() => scanButton.IsEnabled, TimeSpan.FromSeconds(15));
                string logFile = Assert.Single(Directory.GetFiles(window.LogDirectory, "*.jsonl"));
                string events = File.ReadAllText(logFile);
                Assert.Contains("backend-selected", events);
                Assert.Contains("app-scan-complete", events);
                Assert.Contains("Standard Windows scan", ((TextBlock)window.FindName("BackendStatusText")).Text);
                SetStage("showing historical drive suggestions and recent folders");
                drivesReady = window.RefreshDriveChoicesAsync();
                PumpUntil(() => drivesReady.IsCompleted, TimeSpan.FromSeconds(3)); drivesReady.GetAwaiter().GetResult();
                fixtureChoice = drivesList.Items.Cast<DriveChoice>().Single(choice => choice.RootPath == fixtureRoot);
                Assert.Single(fixtureChoice.Recommendations); Assert.Contains("Build files", fixtureChoice.Recommendations[0].Label);
                Assert.Contains("Last scan", fixtureChoice.HistoryCaption); Assert.Equal("Low space", fixtureChoice.SpaceHint);
                Assert.Contains(Path.GetFileName(fixture.Path), fixtureChoice.HistoryCaption);
                Assert.Equal(fixture.Path, fixtureChoice.RecentPath);
                var drivesButton = (Button)window.FindName("DrivesButton");
                var drivePopup = (System.Windows.Controls.Primitives.Popup)window.FindName("DrivePopup");
                drivesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => drivePopup.IsOpen && drivesList.ActualWidth > 0, TimeSpan.FromSeconds(3));
                var drivePanel = (Border)window.FindName("DrivePickerPanel");
#pragma warning disable WPF0001
                foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    app.ThemeMode = theme; PumpUntil(() => true, TimeSpan.FromSeconds(1)); window.UpdateLayout(); drivePanel.UpdateLayout();
                    if (!drivePopup.IsOpen) drivesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => drivePopup.IsOpen && PresentationSource.FromVisual(drivePanel) is not null, TimeSpan.FromSeconds(3));
                    string appearance = theme == ThemeMode.Light ? "light" : "dark";
                    Render(window, Path.Join(FindRepository(), "artifacts", "ui", $"drive-location-{appearance}.png"), 1220, 940);
                    RenderMenu(drivePanel, Path.Join(FindRepository(), "artifacts", "ui", $"drive-picker-{(theme == ThemeMode.Light ? "light" : "dark")}.png"));
                    window.Width = window.MinWidth; window.UpdateLayout(); drivePanel.UpdateLayout();
                    Render(window, Path.Join(FindRepository(), "artifacts", "ui", $"drive-location-compact-{appearance}.png"), 1020, 820);
                    Assert.True(((ScrollViewer)window.FindName("PathScroll")).ActualWidth > 150);
                    window.Width = 1220; window.UpdateLayout();
                }
                app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
                PumpUntil(() => true, TimeSpan.FromSeconds(1));
                if (!drivePopup.IsOpen) drivesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => drivePopup.IsOpen && PresentationSource.FromVisual(drivePanel) is not null, TimeSpan.FromSeconds(3));
                var closeDrives = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(drivePanel), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                drivePanel.RaiseEvent(closeDrives); Assert.True(closeDrives.Handled); Assert.False(drivePopup.IsOpen);
                SetStage("changing drives while editing a full path");
                ((Button)window.FindName("EditPathButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Render(window, Path.Join(FindRepository(), "artifacts", "ui", "drive-location-edit.png"), 1220, 940);
                drivesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => drivePopup.IsOpen && drivesList.ActualWidth > 0, TimeSpan.FromSeconds(3));
                Assert.Equal(Visibility.Collapsed, editor.Visibility);
                var chooseRecent = Descendants(drivesList).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, drivesList.Items.Cast<DriveChoice>().Single(choice => choice.Current)) && Equals(button.Tag, "recent"));
                chooseRecent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => !drivePopup.IsOpen && window.CurrentPath == fixture.Path && ((Border)window.FindName("PathBar")).IsEnabled, TimeSpan.FromSeconds(3));
                Assert.Single(Directory.GetFiles(window.LogDirectory, "*.jsonl"));
                chooseDrive = window.ChooseDriveAsync(fixtureChoice, fixtureChoice.RecentPath);
                PumpUntil(() => chooseDrive.IsCompleted, TimeSpan.FromSeconds(3)); chooseDrive.GetAwaiter().GetResult();
                Assert.Equal(fixture.Path, window.CurrentPath);
                SetStage("opening a drive recommendation from the matching inventory");
                ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex = 1;
                ((TextBox)window.FindName("OpportunitySearch")).Text = "no matches";
                drivesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => drivePopup.IsOpen && drivesList.ActualWidth > 0, TimeSpan.FromSeconds(3));
                var recommendationButton = Descendants(drivesList).OfType<Button>().Single(button => button.DataContext is DriveRecommendationLink);
                int scanLogCount = Directory.GetFiles(window.LogDirectory, "*.jsonl").Length;
                recommendationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((Border)window.FindName("PathBar")).IsEnabled && ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked == true, TimeSpan.FromSeconds(3));
                Assert.False(drivePopup.IsOpen);
                Assert.Equal(scanLogCount, Directory.GetFiles(window.LogDirectory, "*.jsonl").Length);
                Assert.Equal("Build files", ((TextBlock)window.FindName("OpportunityHeading")).Text);
                Assert.Equal(0, ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex);
                Assert.Equal("", ((TextBox)window.FindName("OpportunitySearch")).Text);
                Assert.False(Assert.IsType<OpportunityRow>(((ListBox)window.FindName("OpportunityGrid")).Items[0]).Selected);
                var recommended = fixtureChoice.Recommendations[0];
                var renamed = window.OpenRecommendationAsync(recommended with { Recommendation = new("Old caption", 8192, "cleanup:build") });
                PumpUntil(() => renamed.IsCompleted, TimeSpan.FromSeconds(3)); Assert.True(renamed.GetAwaiter().GetResult());
                var legacy = window.OpenRecommendationAsync(recommended with { Recommendation = new("Build files", 8192) });
                PumpUntil(() => legacy.IsCompleted, TimeSpan.FromSeconds(3)); Assert.True(legacy.GetAwaiter().GetResult());
                var gone = window.OpenRecommendationAsync(recommended with { Recommendation = new("Old caches", 8192, "cleanup:cache") });
                PumpUntil(() => gone.IsCompleted, TimeSpan.FromSeconds(3)); Assert.False(gone.GetAwaiter().GetResult());
                Assert.Equal(Visibility.Visible, ((Border)window.FindName("CleanupOverview")).Visibility);
                Assert.Contains("no longer found", ((TextBlock)window.FindName("StatusText")).Text);
                SetStage("scanning before opening a recommendation when inventory is absent");
                // A drive fixture is deliberately bounded to this temporary
                // directory. The shortcut still runs the real scanner and UI.
                var bounded = recommended with { Drive = recommended.Drive with { RootPath = fixture.Path } };
                window.SetScanTarget(fixture.Path);
                var discoverRecommendation = window.OpenRecommendationAsync(bounded);
                PumpUntil(() => discoverRecommendation.IsCompleted, TimeSpan.FromSeconds(5)); Assert.True(discoverRecommendation.GetAwaiter().GetResult());
                Assert.Equal(scanLogCount + 1, Directory.GetFiles(window.LogDirectory, "*.jsonl").Length);
                Assert.Equal("Build files", ((TextBlock)window.FindName("OpportunityHeading")).Text);
                window.SetScanTarget(Path.Join(fixture.Path, "missing-drive"));
                var failedRecommendation = window.OpenRecommendationAsync(bounded with { Drive = bounded.Drive with { RootPath = Path.Join(fixture.Path, "missing-drive") } });
                PumpUntil(() => failedRecommendation.IsCompleted, TimeSpan.FromSeconds(3)); Assert.False(failedRecommendation.GetAwaiter().GetResult());
                Assert.False(((RadioButton)window.FindName("OpportunitiesTab")).IsChecked);
                Assert.Contains("Scan could not finish", ((TextBlock)window.FindName("StatusText")).Text);
                discoverRecommendation = window.OpenRecommendationAsync(bounded);
                PumpUntil(() => discoverRecommendation.IsCompleted, TimeSpan.FromSeconds(5)); Assert.True(discoverRecommendation.GetAwaiter().GetResult());
                ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var grid = (ListBox)window.FindName("OpportunityGrid");
                Assert.Equal(0, ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex);
                Assert.Equal(Visibility.Visible, ((Border)window.FindName("CleanupOverview")).Visibility);
                ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
                var viewOptions = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("ViewOptionsToggle");
                var viewPanel = (Border)window.FindName("ViewOptionsPanel");
                Assert.Equal(Visibility.Collapsed, viewPanel.Visibility);
                viewOptions.IsChecked = true; viewOptions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                Assert.Equal(Visibility.Visible, viewPanel.Visibility);
                Assert.True(((ComboBox)window.FindName("OpportunitySortPicker")).IsVisible);
                viewOptions.IsChecked = false;
                var initialCards = (ItemsControl)window.FindName("CleanupGroupCards");
                var initialGroup = Assert.IsType<OpportunityGroupRow>(Assert.Single(initialCards.Items.Cast<object>()));
                Assert.Equal("Build files", initialGroup.Name);
                ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
                window.UpdateLayout();
                Descendants(initialCards).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, initialGroup)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var row = Assert.IsType<OpportunityRow>(Assert.Single(grid.Items.Cast<object>()));
                Assert.Equal(bin, row.Path);
                Assert.Equal("8 KiB", row.Size);
                row.Selected = true;
                Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
                Assert.Equal("1 location selected", ((TextBlock)window.FindName("SelectionText")).Text);
                Assert.Equal(row.Item, ((StackPanel)window.FindName("DetailPanel")).DataContext);
                ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;

                SetStage("navigating the real folder tree and treemap");
                int existingScanLogs = Directory.GetFiles(window.LogDirectory, "*.jsonl").Length;
                ((Button)window.FindName("EditPathButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editor.Text = bin;
                var enterPath = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
                editor.RaiseEvent(enterPath);
                PumpUntil(() => ((Border)window.FindName("PathBar")).IsEnabled, TimeSpan.FromSeconds(3));
                Assert.True(enterPath.Handled); Assert.Equal(bin, window.CurrentPath);
                Assert.Equal(existingScanLogs, Directory.GetFiles(window.LogDirectory, "*.jsonl").Length); // Existing inventory, no new scan.
                var ancestorMenu = ((StackPanel)window.FindName("Breadcrumbs")).Children.OfType<Button>().Single(button => button.ContextMenu is not null).ContextMenu;
                ancestorMenu.Items.Cast<MenuItem>().Single(item => Equals(item.ToolTip, fixture.Path)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                PumpUntil(() => ((Border)window.FindName("PathBar")).IsEnabled, TimeSpan.FromSeconds(3));
                Assert.Equal(fixture.Path, window.CurrentPath);
                Assert.Equal(Visibility.Collapsed, ((TextBox)window.FindName("SelectedEntryPath")).Visibility);
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.Equal(fixture.Path, window.CurrentPath);
                Assert.Equal(fixture.Path, Assert.IsType<ExplorerRow>(((ListBox)window.FindName("SpaceTree")).SelectedItem).Path);
                // The following context-menu checks start with only the root expanded.
                // Address navigation deliberately retained the expanded project.
                var resetNavigation = window.InitializeExplorerAsync(Assert.IsType<ExplorerRows>(((ListBox)window.FindName("SpaceTree")).ItemsSource).Scan, [row.Item], default);
                PumpUntil(() => resetNavigation.IsCompleted, TimeSpan.FromSeconds(3)); resetNavigation.GetAwaiter().GetResult();
                var tree = (ListBox)window.FindName("SpaceTree");
                var explorer = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                Assert.True(tree.Items.Count >= 2);
                SetStage("opening folder operations through routed right-click events");
                int binLink = explorer.Scan.PrimaryLink(row.Item.DirectoryIndex);
                int projectLink = explorer.Scan.PrimaryLink(explorer.Scan.Entry(binLink).ParentIndex);
                var projectContainer = Assert.IsAssignableFrom<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(explorer.Find(projectLink)));
                SetStage("checking expansion hit targets beyond the arrow glyph");
                window.UpdateLayout();
                var projectExpander = Descendants(projectContainer).OfType<Button>().Single();
                Assert.True(projectExpander.ActualWidth >= 40 && projectExpander.ActualHeight >= 34);
                var expansionTargets = Descendants(projectExpander).Append(projectExpander).ToHashSet();
                foreach (var point in new[] { new Point(2, 2), new Point(projectExpander.ActualWidth - 2, 2),
                    new Point(2, projectExpander.ActualHeight - 2), new Point(projectExpander.ActualWidth - 2, projectExpander.ActualHeight - 2) })
                    Assert.Contains(Assert.IsAssignableFrom<DependencyObject>(tree.InputHitTest(projectExpander.TranslatePoint(point, tree))), expansionTargets);
                SetStage("opening folder operations through routed right-click events");
                string rootMapTotal = window.CurrentPath;
                RightClick(window, projectContainer);
                Assert.Equal(project, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(project, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                Assert.Equal(rootMapTotal, window.CurrentPath);
                PumpUntil(() => tree.ContextMenu.Items.Cast<MenuItem>().All(i => !Equals(i.Header, "Checking cloud availability…")), TimeSpan.FromSeconds(3));
                Assert.Equal(new[] { "Open in File Explorer", "Move to Recycle Bin", "Recheck this folder" }, tree.ContextMenu.Items.Cast<MenuItem>().Select(i => i.Header));
                Assert.True(tree.ContextMenu.Items.Cast<MenuItem>().All(i => i.IsEnabled));
                var capturedRecycle = (MenuItem)tree.ContextMenu.Items[1];
                tree.ContextMenu.IsOpen = false;
                var rootMap = (TreemapPanel)window.FindName("SpaceMap");
                var projectTile = rootMap.Buttons.Single(b => ((SpaceContribution)b.DataContext).Path == project);
                RightClick(window, projectTile);
                PumpUntil(() => projectTile.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(rootMapTotal, window.CurrentPath);
                PumpUntil(() => projectTile.ContextMenu.Items.Cast<MenuItem>().All(i => !Equals(i.Header, "Checking cloud availability…")), TimeSpan.FromSeconds(3));
                Assert.Equal(3, projectTile.ContextMenu.Items.Count);
                projectTile.ContextMenu.IsOpen = false;

                SetStage("revealing a map folder beneath a collapsed tree branch");
                var openProject = window.NavigateToEntryAsync(projectLink);
                PumpUntil(() => openProject.IsCompleted, TimeSpan.FromSeconds(10));
                openProject.GetAwaiter().GetResult();
                Assert.False(explorer.IsExpanded(projectLink));
                Assert.Equal(-1, explorer.Find(binLink));
                var binTile = rootMap.Buttons.Single(b => ((SpaceContribution)b.DataContext).Path == bin);
                string projectMapTotal = window.CurrentPath;
                RightClick(window, binTile);
                PumpUntil(() => binTile.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(bin, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                Assert.True(explorer.IsExpanded(projectLink));
                Assert.Same(binTile, rootMap.Buttons.Single(b => ((SpaceContribution)b.DataContext).Path == bin));
                Assert.Equal(projectMapTotal, window.CurrentPath);
                binTile.ContextMenu.IsOpen = false;
                RightClick(window, tree, keyboard: true);
                PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(bin, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                tree.ContextMenu.IsOpen = false;

                SetStage("sorting tree columns while preserving expansion, selection and map scope");
                CheckTreeColumns(window, tree);
                var expandedBefore = tree.Items.Cast<ExplorerRow>().Where(r => r.Expanded).Select(r => r.Entry.LinkIndex).ToArray();
                foreach (string header in new[] { "NameHeader", "NameHeader", "ModifiedHeader", "ModifiedHeader", "CreatedHeader", "TypeHeader", "ExtensionHeader", "AttributesHeader", "LogicalHeader", "AllocationHeader", "ShareHeader" })
                {
                    ((ButtonBase)window.FindName(header)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
                    Assert.Equal(FontWeights.SemiBold, ((ButtonBase)window.FindName(header)).FontWeight);
                    Assert.Equal(bin, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                    Assert.All(expandedBefore, link => Assert.True(explorer.IsExpanded(link)));
                    Assert.Equal(projectMapTotal, window.CurrentPath);
                }
                Assert.Equal("% parent ↓", ((GridViewColumnHeader)window.FindName("ShareHeader")).Content);
                ResetTreeColumns(window);
                PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));

                var navigation = window.NavigateToEntryAsync(explorer.Scan.PrimaryLink(row.Item.DirectoryIndex));
                PumpUntil(() => navigation.IsCompleted, TimeSpan.FromSeconds(10));
                navigation.GetAwaiter().GetResult();
                Assert.Equal(bin, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                SetStage("keeping a folder menu action bound to its original target");
                string? menuConfirmationPath = null;
                var menuTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
                {
                    var dialog = app.Windows.OfType<RecycleFolderWindow>().FirstOrDefault();
                    if (dialog is null) return;
                    menuConfirmationPath = ((TextBox)dialog.FindName("TargetPath")).Text;
                    dialog.Close();
                }, Dispatcher.CurrentDispatcher);
                capturedRecycle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                PumpUntil(() => scanButton.IsEnabled, TimeSpan.FromSeconds(10));
                menuTimer.Stop();
                Assert.Equal(project, menuConfirmationPath); // Current selection is bin, menu was opened on its parent.
                Assert.True(Directory.Exists(project));
                RightClick(window, tree, keyboard: true);
                PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(bin, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                tree.ContextMenu.IsOpen = false;
                var map = (TreemapPanel)window.FindName("SpaceMap");
                Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("EntryTypeText")).Visibility);
                var tile = Assert.IsType<Button>(Assert.Single(map.Children.Cast<object>()));
                Assert.Equal(8192, Assert.IsType<SpaceContribution>(tile.DataContext).Bytes);
                Assert.Contains("Inferred type: Ambiguous (.dll)", Assert.IsType<string>(tile.ToolTip));
                Assert.Contains("Candidates:", Assert.IsType<string>(tile.ToolTip));
                tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((StackPanel)window.FindName("EntryDetail")).DataContext is ExplorerRow { Entry.IsDirectory: false }, TimeSpan.FromSeconds(10));
                Assert.Equal(Path.Join(bin, "sample.dll"), Assert.IsType<ExplorerRow>(((StackPanel)window.FindName("EntryDetail")).DataContext).Path);
                var typeText = (TextBlock)window.FindName("EntryTypeText");
                Assert.Contains("Inferred type: Ambiguous (.dll)", typeText.Text);
                Assert.Equal(Visibility.Visible, typeText.Visibility);
                var fileContainer = Assert.IsAssignableFrom<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(tree.SelectedIndex));
                Assert.Contains(typeText.Text, Assert.IsType<string>(fileContainer.ToolTip));
                Render(window, Path.Join(FindRepository(), "artifacts", "ui", "registry-ambiguous-selection.png"), 1220, 910);

                SetStage("constructing cleanup review");
                var scan = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false).GetAwaiter().GetResult();
                var plan = CleanupPlanner.Create(scan, OpportunityDetector.Find(scan));
                var review = new ReviewWindow(plan);
                var clean = Descendants(review.Content as DependencyObject).OfType<Button>()
                    .Single(b => Equals(b.Content, "Move to Recycle Bin"));
                Assert.False(clean.IsEnabled);
                Assert.Equal(bin, Assert.IsType<CleanupReviewRow>(Assert.Single(((ListBox)review.FindName("LocationList")).Items.Cast<object>())).Path);
                var startup = new StartupWindow();
                var details = new ScanDetailsWindow(scan, TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(8));
                Assert.Contains("files", ((TextBlock)details.FindName("CountsText")).Text);
                Assert.Equal("No skipped-path warnings", ((TextBlock)details.FindName("CoverageText")).Text);
                Assert.Contains(Descendants(startup.Content as DependencyObject).OfType<Button>(), b => Equals(b.Content, "Continue without admin"));

                SetStage("checking folder Delete confirmation and its safe default");
                var returnToFolder = window.NavigateToEntryAsync(explorer.Scan.PrimaryLink(row.Item.DirectoryIndex));
                PumpUntil(() => returnToFolder.IsCompleted, TimeSpan.FromSeconds(10));
                returnToFolder.GetAwaiter().GetResult();
                Exception? confirmationFailure = null;
                bool confirmed = false;
                var confirmationTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
                {
                    var dialog = app.Windows.OfType<RecycleFolderWindow>().FirstOrDefault();
                    if (dialog is null) return;
                    try
                    {
                        Assert.Equal(bin, ((TextBox)dialog.FindName("TargetPath")).Text);
                        Assert.True(((Button)dialog.FindName("KeepButton")).IsDefault);
                        Assert.True(((Button)dialog.FindName("KeepButton")).IsCancel);
                        Assert.Equal("Cancel", ((Button)dialog.FindName("KeepButton")).Content);
                        Assert.Equal("bin", ((TextBlock)dialog.FindName("TargetName")).Text);
                        Assert.False(((Expander)dialog.FindName("DeletionDetails")).IsExpanded);
                        Assert.Equal(Visibility.Collapsed, ((TextBlock)dialog.FindName("PartialScanNote")).Visibility);
                        Assert.Contains("8 KiB", ((TextBlock)dialog.FindName("TargetSize")).Text);
                        confirmed = true;
                    }
                    catch (Exception ex) { confirmationFailure = ex; }
                    finally { dialog.Close(); }
                }, Dispatcher.CurrentDispatcher);
                var deleteKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tree), Environment.TickCount, Key.Delete)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                tree.RaiseEvent(deleteKey);
                PumpUntil(() => scanButton.IsEnabled, TimeSpan.FromSeconds(10));
                confirmationTimer.Stop();
                if (confirmationFailure is not null) throw confirmationFailure;
                Assert.True(deleteKey.Handled);
                Assert.True(confirmed);
                Assert.True(Directory.Exists(bin));
                Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
                // Delete while the exact path text field is focused must not
                // invoke folder recycling.
                var pathField = (TextBox)window.FindName("PathEditor");
                var fieldKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pathField), Environment.TickCount, Key.Delete)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                pathField.RaiseEvent(fieldKey);
                Assert.False(fieldKey.Handled);

                SetStage("recycling a fixture through the actual UI and marking its scan stale");
                var approveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
                {
                    var dialog = app.Windows.OfType<RecycleFolderWindow>().FirstOrDefault();
                    if (dialog is not null) ((Button)dialog.FindName("RecycleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, Dispatcher.CurrentDispatcher);
                var recycleTask = window.DeleteSelectedFolderAsync();
                PumpUntil(() => recycleTask.IsCompleted, TimeSpan.FromSeconds(10));
                approveTimer.Stop();
                var recycled = Assert.IsType<RecycleResult>(recycleTask.GetAwaiter().GetResult());
                try
                {
                    Assert.False(Directory.Exists(bin));
                    Assert.Contains("Updated", ((TextBlock)window.FindName("StatusText")).Text);
                    Assert.Contains("Partial refresh", ((TextBlock)window.FindName("TreeNote")).Text);
                    Assert.Equal("0 B", ((TextBlock)window.FindName("CandidateText")).Text);
                    Assert.False(((Button)window.FindName("ReviewButton")).IsEnabled);
                    var updatedRows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                    Assert.Null(updatedRows.Scan.FindEntry(bin));
                    Assert.Equal(project, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                    Assert.True(updatedRows.IsExpanded(updatedRows.Scan.FindEntry(project)!.Value));
                    Assert.Equal("Allocated ↓", ((GridViewColumnHeader)window.FindName("AllocationHeader")).Content);
                    capturedRecycle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.True(scanButton.IsEnabled); // Captured old-scan actions cannot operate on the refreshed inventory.
                }
                finally { DeletionTests.RestoreFixture(Assert.IsType<string>(recycled.RecycledPath), bin); }
                // A small viewport exercises scrolling with the existing owned
                // rows, without expanding every later native recheck fixture.
                tree.Height = 40; window.UpdateLayout();
                var treeScroll = Descendants(tree).OfType<ScrollViewer>().First();
                treeScroll.ScrollToBottom(); window.UpdateLayout();
                PumpUntil(() => treeScroll.VerticalOffset > 0, TimeSpan.FromSeconds(2));
                var topTreePath = Assert.IsType<ExplorerRows>(tree.ItemsSource).Row((int)treeScroll.VerticalOffset).Path;
                var sourceDescriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListBox));
                var publishedBlankTree = false; var publishedCollapsedBranch = false;
                EventHandler observeTreePublication = (_, _) =>
                {
                    if (tree.ItemsSource is not ExplorerRows rows) { publishedBlankTree = true; return; }
                    if (rows.Scan.FindEntry(project) is { } projectLink && !rows.IsExpanded(projectLink)) publishedCollapsedBranch = true;
                };
                sourceDescriptor.AddValueChanged(tree, observeTreePublication);
                var refreshedExplorer = window.RecheckFolderAsync(project);
                PumpUntil(() => refreshedExplorer.IsCompleted, TimeSpan.FromSeconds(10));
                refreshedExplorer.GetAwaiter().GetResult();
                sourceDescriptor.RemoveValueChanged(tree, observeTreePublication);
                Assert.False(publishedBlankTree); Assert.False(publishedCollapsedBranch);
                Assert.Equal(project, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                window.UpdateLayout();
                Assert.Equal(topTreePath, Assert.IsType<ExplorerRows>(tree.ItemsSource).Row((int)treeScroll.VerticalOffset).Path);
                tree.Height = double.NaN; window.UpdateLayout();
                window.SetBusy(false);
                Assert.IsType<OpportunityRow>(Assert.Single(grid.Items.Cast<object>())).Selected = true;
                Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);

                SetStage("preserving a collapsed root and prior out-of-date scopes during rechecks");
                var goRoot = window.NavigateToEntryAsync(-1);
                PumpUntil(() => goRoot.IsCompleted, TimeSpan.FromSeconds(10)); goRoot.GetAwaiter().GetResult();
                Assert.IsType<ExplorerRows>(tree.ItemsSource).Collapse(-1);
                var collapsedRecheck = window.RecheckFolderAsync(project);
                PumpUntil(() => collapsedRecheck.IsCompleted, TimeSpan.FromSeconds(10)); collapsedRecheck.GetAwaiter().GetResult();
                Assert.False(Assert.IsType<ExplorerRows>(tree.ItemsSource).IsExpanded(-1));
                Assert.Single(tree.Items.Cast<object>());
                window.MarkScanStale("Prior fixture refresh failed.", [new(project)]);
                var otherRecheck = window.RecheckFolderAsync(window.LogDirectory);
                PumpUntil(() => otherRecheck.IsCompleted, TimeSpan.FromSeconds(10)); otherRecheck.GetAwaiter().GetResult();
                Assert.Contains("OUT OF DATE", ((TextBlock)window.FindName("TreeNote")).Text);
                Assert.Equal("—", ((TextBlock)window.FindName("CandidateText")).Text);
                Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
                var recovery = window.RecheckFolderAsync(project);
                PumpUntil(() => recovery.IsCompleted, TimeSpan.FromSeconds(10)); recovery.GetAwaiter().GetResult();
                Assert.Contains("Partial refresh", ((TextBlock)window.FindName("TreeNote")).Text);
                Assert.Equal("8 KiB", ((TextBlock)window.FindName("CandidateText")).Text);

                SetStage("draining an Explorer-return recheck deferred while busy");
                using (var busy = new CancellationTokenSource())
                {
                    typeof(MainWindow).GetField("_work", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, busy);
                    typeof(MainWindow).GetField("_explorerRecheckPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, project);
                    window.SetBusy(true);
                    typeof(MainWindow).GetMethod("Window_Activated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null, EventArgs.Empty]);
                    Assert.Equal(project, typeof(MainWindow).GetField("_explorerRecheckPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                    typeof(MainWindow).GetField("_work", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
                    window.SetBusy(false);
                    PumpUntil(() => scanButton.IsEnabled, TimeSpan.FromSeconds(10));
                    Assert.Null(typeof(MainWindow).GetField("_explorerRecheckPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                }

                SetStage("checking an eligible native cloud file menu and cancelling its confirmation");
                string cloudRoot = Directory.CreateDirectory(Path.Join(fixture.Path, "synced")).FullName;
                string cloudFile = Path.Join(cloudRoot, "photo.jpg"); File.WriteAllBytes(cloudFile, new byte[4096]);
                OnlineOnlyWindow onlinePreview;
                using (CloudFilesTests.RegisterSyncedFixture(cloudRoot, cloudFile))
                {
                    var importCloud = window.RecheckFolderAsync(fixture.Path);
                    PumpUntil(() => importCloud.IsCompleted, TimeSpan.FromSeconds(10)); importCloud.GetAwaiter().GetResult();
                    var cloudScan = Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan;
                    int cloudLink = cloudScan.FindEntry(cloudFile)!.Value;
                    var goCloud = window.NavigateToEntryAsync(cloudLink);
                    PumpUntil(() => goCloud.IsCompleted, TimeSpan.FromSeconds(10)); goCloud.GetAwaiter().GetResult();
                    RightClick(window, tree, keyboard: true);
                    PumpUntil(() => tree.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Make online-only")), TimeSpan.FromSeconds(10));
                    Assert.True(tree.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Make online-only")).IsEnabled);
                    tree.ContextMenu.IsOpen = false;
                    bool onlineConfirmed = false;
                    Exception? onlineFailure = null;
                    var keepTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
                    {
                        var dialog = app.Windows.OfType<OnlineOnlyWindow>().FirstOrDefault();
                        if (dialog is null) return;
                        try
                        {
                            Assert.Equal(cloudFile, ((TextBox)dialog.FindName("TargetPath")).Text);
                            Assert.True(Descendants(dialog.Content as DependencyObject).OfType<Button>().Single(button => Equals(button.Content, "Keep offline")).IsDefault);
                            onlineConfirmed = true;
                        }
                        catch (Exception ex) { onlineFailure = ex; }
                        finally { dialog.Close(); }
                    }, Dispatcher.CurrentDispatcher);
                    var onlineAction = window.MakeOnlineOnlyAsync(cloudScan, cloudLink);
                    PumpUntil(() => onlineAction.IsCompleted, TimeSpan.FromSeconds(10)); onlineAction.GetAwaiter().GetResult();
                    keepTimer.Stop(); if (onlineFailure is not null) throw onlineFailure;
                    Assert.True(onlineConfirmed);
                    Assert.Contains("No requests were sent", ((TextBlock)window.FindName("StatusText")).Text);
                    Assert.NotEqual(2, CloudFiles.Inspect(cloudFile)!.PinState); // Cancellation sent no unpin request.
                    onlinePreview = new OnlineOnlyWindow(CloudFiles.Prepare(cloudScan, cloudLink, default));
                }

                SetStage("preserving grouped-selection deletion safeguards after an unrelated recheck");
                string groupedRoot = Directory.CreateDirectory(Path.Join(fixture.Path, "many-files")).FullName;
                for (int i = 0; i < 70; i++) File.WriteAllBytes(Path.Join(groupedRoot, $"photo-{i}.jpg"), new byte[512]);
                var importGroup = window.RecheckFolderAsync(fixture.Path);
                PumpUntil(() => importGroup.IsCompleted, TimeSpan.FromSeconds(10)); importGroup.GetAwaiter().GetResult();
                var groupScan = Assert.IsType<ExplorerRows>(tree.ItemsSource).Scan;
                var goGroup = window.NavigateToEntryAsync(groupScan.FindEntry(groupedRoot)!.Value);
                PumpUntil(() => goGroup.IsCompleted, TimeSpan.FromSeconds(10)); goGroup.GetAwaiter().GetResult();
                var realGroup = map.Buttons.Single(button => ((SpaceContribution)button.DataContext).Link == -2);
                realGroup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
                var unrelatedRefresh = window.RecheckFolderAsync(project);
                PumpUntil(() => unrelatedRefresh.IsCompleted, TimeSpan.FromSeconds(10)); unrelatedRefresh.GetAwaiter().GetResult();
                Assert.True((bool)typeof(MainWindow).GetField("_selectionIsGroup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
                Assert.Null(window.DeleteSelectedFolderAsync().GetAwaiter().GetResult());
                Assert.Contains("Grouped entries cannot be deleted", ((TextBlock)window.FindName("StatusText")).Text);
                Assert.True(Directory.Exists(groupedRoot));

                string artifactRoot = FindRepository();
                BeginWorkflow("automatic optimization preview and execution");
                CheckOptimization(app, artifactRoot);
                BeginWorkflow("duplicate discovery and storage preservation");
                CheckDuplicateWorkflow(app, artifactRoot);
                BeginWorkflow("native tool cache maintenance");
                CheckToolCaches(app, artifactRoot);
                BeginWorkflow("managed worktree lifecycle");
                CheckWorktreeLifecycle(app, artifactRoot);
                var recyclePreview = new RecycleFolderWindow(FolderDeletionTarget.FromScan(scan, scan.FindEntry(bin)!.Value));
#pragma warning disable WPF0001 // Exercise the same Fluent theme through its experimental C# API in this smoke test only.
                foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    stage = $"rendering {mode}";
                    app.ThemeMode = mode;
                    PumpUntil(() => true, TimeSpan.FromSeconds(1));
                    string theme = mode == ThemeMode.Light ? "light" : "dark";
                    ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"explorer-{theme}.png"), 1220, 910);
                    // Render the native template states without moving the user's pointer
                    // or bringing this offscreen test window to the foreground.
                    var nameHeader = (GridViewColumnHeader)window.FindName("NameHeader");
                    var allocationHeader = (GridViewColumnHeader)window.FindName("AllocationHeader");
                    var logicalHeader = (GridViewColumnHeader)window.FindName("ModifiedHeader");
                    Assert.True(VisualStateManager.GoToState(nameHeader, "MouseOver", false));
                    Assert.True(VisualStateManager.GoToState(allocationHeader, "Pressed", false));
                    Assert.True(VisualStateManager.GoToState(logicalHeader, "Focused", false));
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"header-states-{theme}.png"), 1220, 910);
                    if (mode == ThemeMode.Dark) Render(window, Path.Join(artifactRoot, "artifacts", "ui", "header-states-compact-dark.png"), 1020, 790);
                    Assert.True(VisualStateManager.GoToState(nameHeader, "Normal", false));
                    Assert.True(VisualStateManager.GoToState(allocationHeader, "Normal", false));
                    Assert.True(VisualStateManager.GoToState(logicalHeader, "Unfocused", false));
                    ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"main-{theme}.png"), 1220, 910);
                    Render(review, Path.Join(artifactRoot, "artifacts", "ui", $"review-{theme}.png"), 860, 700);
                    Render(details, Path.Join(artifactRoot, "artifacts", "ui", $"details-{theme}.png"), 700, 680);
                    Render(recyclePreview, Path.Join(artifactRoot, "artifacts", "ui", $"recycle-{theme}.png"), 520, 300);
                    Render(onlinePreview, Path.Join(artifactRoot, "artifacts", "ui", $"online-only-{theme}.png"), 590, 550);
                    VerifyUpdatesUi(window, theme);
                }
#pragma warning restore WPF0001
                Render(startup, Path.Join(artifactRoot, "artifacts", "ui", "startup.png"), 570, 305);
                ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", "explorer-compact.png"), 1020, 790);
                Assert.InRange(window.ActualWidth, 1019, 1021);
                Assert.True(((FrameworkElement)window.Content).RenderSize.Width <= 1020, "Compact rendering used the previous window width.");
                var detailPanel = (StackPanel)window.FindName("EntryDetail");
                Assert.True(map.TranslatePoint(new Point(0, map.ActualHeight), (UIElement)window.Content).Y <= detailPanel.TranslatePoint(new Point(), (UIElement)window.Content).Y + 1, "The treemap overlaps its selected-entry details.");

                BeginWorkflow("Explorer visualization and virtualization");
                SetStage("checking wide-folder UI virtualization");
                var wideBuilder = new ScanBuilder(trackPreliminary: true);
                wideBuilder.RootIndex = wideBuilder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
                for (int i = 0; i < 50_000; i++)
                {
                    int file = wideBuilder.AddFile(new(new((ulong)i + 2), FileAttributes.Normal, 4096, 4096, 0));
                    wideBuilder.AddLink(file, new(1), $"file-{i}.bin");
                }
                SetStage("showing bounded preliminary results");
                window.RetainCancelledPreliminary(); // Clear the completed fixture before simulating acquisition.
                window.SetBusy(true);
                ((TextBlock)window.FindName("StatusText")).Text = "Reading folders · 50,001 file objects · 1.0s";
                var preliminary = wideBuilder.Preliminary(@"C:\");
                // Add one unresolved path to exercise the visible pending label.
                preliminary = preliminary with { LargestFiles = preliminary.LargestFiles.Select((f, i) => i == 0 ? f with { Path = null } : f).ToArray() };
                window.ShowPreliminary(preliminary);
                var preliminaryPanel = (FrameworkElement)window.FindName("PreliminaryPanel");
                var preliminaryFiles = (ListBox)window.FindName("PreliminaryFiles");
                Assert.Equal(Visibility.Visible, preliminaryPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("ExplorePanel")).Visibility);
                Assert.Equal(100, preliminaryFiles.Items.Count);
                Assert.True(preliminaryFiles.IsEnabled);
                Assert.False(((Border)window.FindName("PathBar")).IsEnabled);
                Assert.False(((Button)window.FindName("ReviewButton")).IsEnabled);
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("CancelButton")).Visibility);
                Assert.Equal("Path pending…", Assert.IsType<PreliminaryRow>(preliminaryFiles.Items[0]).PathText);
                Assert.Contains("Inferred type:", Assert.IsType<PreliminaryRow>(preliminaryFiles.Items[0]).EntryTooltip);
                Assert.Contains("PRELIMINARY", ((TextBlock)window.FindName("ScannedSizeLabel")).Text);
                SetStage("sorting preliminary rows and retaining the choice across progress updates");
                foreach (string header in new[] { "PreliminaryNameHeader", "PreliminaryNameHeader", "PreliminaryLogicalHeader", "PreliminaryAllocationHeader" })
                    ((ButtonBase)window.FindName(header)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)window.FindName("PreliminaryNameHeader")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var names = preliminaryFiles.Items.Cast<PreliminaryRow>().Select(r => r.File.Name).ToArray();
                Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), names);
                window.ShowPreliminary(preliminary);
                Assert.Equal(names, preliminaryFiles.Items.Cast<PreliminaryRow>().Select(r => r.File.Name));
                Assert.Equal("File ↑", ((Button)window.FindName("PreliminaryNameHeader")).Content);
                Assert.Equal(FontWeights.SemiBold, ((Button)window.FindName("PreliminaryNameHeader")).FontWeight);
                Assert.Equal(FontWeights.Normal, ((Button)window.FindName("PreliminaryAllocationHeader")).FontWeight);
#pragma warning disable WPF0001
                foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    app.ThemeMode = mode;
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"preliminary-{(mode == ThemeMode.Light ? "light" : "dark")}.png"), 1220, 910);
                }
#pragma warning restore WPF0001
                SetStage("retaining cancelled partial results and resetting a fallback");
                window.RetainCancelledPreliminary();
                window.SetBusy(false);
                ((TextBlock)window.FindName("StatusText")).Text = "Scan cancelled · Preliminary results retained.";
                Assert.Equal(100, preliminaryFiles.Items.Count);
                Assert.Empty(tree.Items.Cast<object>());
                Assert.Empty(grid.Items.Cast<object>());
                Assert.False(((Button)window.FindName("ReviewButton")).IsEnabled);
                Assert.Contains("cancelled", ((TextBlock)window.FindName("PreliminaryCaption")).Text);
                Assert.Equal("—", ((TextBlock)window.FindName("CandidateText")).Text);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", "preliminary-cancelled-compact.png"), 1020, 790);
                window.ShowPreliminary(new(0, 0, false, Array.Empty<PreliminaryFile>()));
                Assert.Empty(preliminaryFiles.Items.Cast<object>());
                Assert.Equal("0 B", ((TextBlock)window.FindName("ScannedSizeText")).Text);
                Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("PreliminaryHint")).Visibility);
                Assert.Contains("LOGICAL", ((TextBlock)window.FindName("ScannedSizeLabel")).Text);
                Assert.Contains("Hard links may repeat", ((TextBlock)window.FindName("PreliminaryNote")).Text);
                Assert.Contains("Waiting", ((TextBlock)window.FindName("PreliminaryHint")).Text);
                // A backend restart is a fresh acquisition view, not cancelled.
                Assert.DoesNotContain("cancelled", ((TextBlock)window.FindName("PreliminaryCaption")).Text);
                var wideScan = wideBuilder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
                SetStage("binding the wide-folder indexed source");
                var wideLoad = window.InitializeExplorerAsync(wideScan, [], CancellationToken.None);
                PumpUntil(() => wideLoad.IsCompleted, TimeSpan.FromSeconds(10));
                wideLoad.GetAwaiter().GetResult();
                Assert.Equal(Visibility.Collapsed, preliminaryPanel.Visibility);
                Assert.Empty(preliminaryFiles.Items.Cast<object>());
                Assert.Equal(Visibility.Visible, ((Grid)window.FindName("ExplorePanel")).Visibility);
                var wideContent = (FrameworkElement)window.Content;
                SetStage("measuring the wide-folder window");
                window.UpdateLayout();
                Assert.Equal(50_001, tree.Items.Count);
                int realized = Descendants(tree).OfType<ListBoxItem>().Count();
                Assert.True(realized is > 0 and <= 100, $"The wide folder realized {realized:N0} containers.");
                SetStage("blocking deletion of grouped map entries");
                var groupedTile = Assert.IsType<Button>(Assert.Single(map.Children.Cast<object>()));
                Assert.Equal(-2, Assert.IsType<SpaceContribution>(groupedTile.DataContext).Link);
                Assert.Null(groupedTile.ContextMenu);
                groupedTile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
                var groupedDelete = window.DeleteSelectedFolderAsync();
                Assert.Null(groupedDelete.GetAwaiter().GetResult());
                Assert.Contains("Grouped entries cannot be deleted", ((TextBlock)window.FindName("StatusText")).Text);
                var groupedSort = window.SortTreeAsync(ScanSortColumn.Name);
                PumpUntil(() => groupedSort.IsCompleted, TimeSpan.FromSeconds(10));
                groupedSort.GetAwaiter().GetResult();
                Assert.Null(window.DeleteSelectedFolderAsync().GetAwaiter().GetResult());
                Assert.Contains("Grouped entries cannot be deleted", ((TextBlock)window.FindName("StatusText")).Text);
                SetStage("disabling recycling on a drive-root context menu");
                RightClick(window, Assert.IsAssignableFrom<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(0)));
                PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.True(((MenuItem)tree.ContextMenu.Items[0]).IsEnabled);
                Assert.False(((MenuItem)tree.ContextMenu.Items[1]).IsEnabled);
                Assert.Contains("Drive roots", Assert.IsType<string>(((MenuItem)tree.ContextMenu.Items[1]).ToolTip));
                RenderMenu(tree.ContextMenu, Path.Join(artifactRoot, "artifacts", "ui", "folder-menu-dark.png"));
                tree.ContextMenu.IsOpen = false;
                RightClick(window, tree);
                Assert.False(tree.ContextMenu.IsOpen); // Blank tree space does not reuse the selected root's menu.
                SetStage("scrolling to the final wide-folder entry");
                var wideRows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                tree.SelectedIndex = wideRows.Count - 1;
                tree.ScrollIntoView(tree.SelectedItem);
                wideContent.UpdateLayout();
                PumpUntil(() => tree.ItemContainerGenerator.ContainerFromIndex(wideRows.Count - 1) is not null, TimeSpan.FromSeconds(10));
                Assert.True(Descendants(tree).OfType<ListBoxItem>().Count() <= 100, "Scrolling realized the whole wide folder.");
                var finalFile = Assert.IsAssignableFrom<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(wideRows.Count - 1));
                RightClick(window, finalFile);
                PumpUntil(() => tree.ContextMenu.Items.Cast<MenuItem>().All(i => !Equals(i.Header, "Checking cloud availability…")), TimeSpan.FromSeconds(3));
                Assert.False(tree.ContextMenu.IsOpen); // File rows do not expose folder operations.
                Assert.Equal(8192, new FileInfo(Path.Join(bin, "sample.dll")).Length);
                window.SetScanTarget(Path.GetPathRoot(fixture.Path)!);
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("OpenLogButton")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("BackendStatusText")).Visibility);
                Assert.Empty(tree.ContextMenu.Items.Cast<object>());
                int dialogsBefore = app.Windows.OfType<RecycleFolderWindow>().Count();
                capturedRecycle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.True(scanButton.IsEnabled);
                Assert.Equal(dialogsBefore, app.Windows.OfType<RecycleFolderWindow>().Count()); // An older scan's menu cannot delete a current target.

                BeginWorkflow("Photos");
                CheckPhotos(window, app, fixture, artifactRoot, SetStage);

                BeginWorkflow("Apps");
                SetStage("checking Apps extension packaging, sorting, search, cancellation and drive changes");
                CheckApps(window, app, fixture, artifactRoot);
                BeginWorkflow("Assistant");
                SetStage("checking local assistant metadata, review, disable and cancellation");
                CheckAssistant(window, app, artifactRoot);
                BeginWorkflow("Explorer maps and columns");
                SetStage("rendering framed folders at desktop and compact sizes");
                window.SetScanTarget(@"G:\");
                var layoutScan = NestedLayoutFixture();
                var layoutLoad = window.InitializeExplorerAsync(layoutScan, [], default);
                PumpUntil(() => layoutLoad.IsCompleted, TimeSpan.FromSeconds(10));
                layoutLoad.GetAwaiter().GetResult();
                Assert.Contains(map.Buttons, button => ((SpaceContribution)button.DataContext).Name == "Project A");
                Assert.InRange(map.Buttons.Count(), 1, 64);
                ((TextBlock)window.FindName("ScannedSizeLabel")).Text = "Scanned";
                ((TextBlock)window.FindName("ScannedSizeText")).Text = SizeText.Format(layoutScan.AllocatedBytes);
                ((TextBlock)window.FindName("ScannedSizeCaption")).Text = "On disk";
                ((TextBlock)window.FindName("CandidateText")).Text = "—";
                ((TextBlock)window.FindName("CandidateCaption")).Text = "Not analyzed";
                ((TextBlock)window.FindName("StatusText")).Text = "Illustrative inventory · Layout validation";
                PumpUntil(() => ((ProgressBar)window.FindName("DriveCapacityBar")).Value > 0, TimeSpan.FromSeconds(2));
                Assert.InRange(((ProgressBar)window.FindName("DriveCapacityBar")).Value, 3, 4);
#pragma warning disable WPF0001
                foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    app.ThemeMode = mode;
                    string theme = mode == ThemeMode.Light ? "light" : "dark";
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"framed-folders-{theme}.png"), 1220, 910);
                    Assert.InRange(((FrameworkElement)window.FindName("ScanHeader")).ActualHeight, 100, 130);
                    AssertMapFillsPane(window, map);
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"framed-folders-compact-{theme}.png"), 1020, 790);
                    AssertMapFillsPane(window, map);
                    Assert.True(map.TranslatePoint(new Point(0, map.ActualHeight), (UIElement)window.Content).Y
                        <= detailPanel.TranslatePoint(new Point(), (UIElement)window.Content).Y + 1, "The nested map overlaps entry details.");
                    var exploreLayout = (Grid)window.FindName("ExplorePanel");
                    exploreLayout.ColumnDefinitions[0].Width = new(2, GridUnitType.Star);
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"framed-folders-narrow-{theme}.png"), 1020, 790);
                    var parentBand = map.Buttons.Single(button => ((SpaceContribution)button.DataContext).Name == "Github");
                    var title = Assert.IsType<Grid>(parentBand.Content);
                    var name = (TextBlock)title.Children[0]; var facts = (TextBlock)title.Children[1];
                    Assert.Equal(Visibility.Visible, facts.Visibility);
                    Assert.True(name.TranslatePoint(new Point(0, name.ActualHeight), parentBand).Y <= facts.TranslatePoint(new Point(), parentBand).Y,
                        "A compact parent band must keep the name and size readable without overlap.");
                    exploreLayout.ColumnDefinitions[0].Width = new(1, GridUnitType.Star);
                }
#pragma warning restore WPF0001
                SetStage("keeping small overview folders inside their proportional map cells after resizing");
                var crowdedLoad = window.InitializeExplorerAsync(CrowdedMapFixture(), [], default);
                PumpUntil(() => crowdedLoad.IsCompleted, TimeSpan.FromSeconds(10));
                crowdedLoad.GetAwaiter().GetResult();
                foreach (var size in new[] { (1220, 910), (1020, 790), (1220, 910) })
                {
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"framed-folders-crowded-{size.Item1}.png"), size.Item1, size.Item2);
                    AssertTilesContained(map);
                }
                SetStage("revealing the parent of a nested grouped tile beneath a collapsed root");
                var groupedLoad = window.InitializeExplorerAsync(NestedLayoutFixture(smallEntries: true), [], default);
                PumpUntil(() => groupedLoad.IsCompleted, TimeSpan.FromSeconds(10));
                groupedLoad.GetAwaiter().GetResult();
                var groupedRows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                window.UpdateLayout();
                var rootContainer = Assert.IsAssignableFrom<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(0));
                Descendants(rootContainer).OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
                Assert.False(groupedRows.IsExpanded(-1));
                var nestedGroup = map.Buttons.Single(button => ((SpaceContribution)button.DataContext).Link == -2);
                Assert.Null(nestedGroup.ContextMenu);
#pragma warning disable WPF0001
                foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    app.ThemeMode = mode;
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"framed-folders-grouped-{(mode == ThemeMode.Light ? "light" : "dark")}.png"), 1220, 910);
                    var groupFrame = Assert.IsType<System.Windows.Shapes.Rectangle>(nestedGroup.Template.FindName("GroupFrame", nestedGroup));
                    Assert.Equal(Visibility.Visible, groupFrame.Visibility);
                    Assert.NotEmpty(groupFrame.StrokeDashArray);
                }
#pragma warning restore WPF0001
                nestedGroup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
                Assert.True(groupedRows.IsExpanded(-1));
                Assert.Equal(@"G:\Github", Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                Assert.True(Assert.IsType<ExplorerRow>(tree.SelectedItem).Expanded);
                Assert.Null(window.DeleteSelectedFolderAsync().GetAwaiter().GetResult());
                Assert.Contains("Grouped entries cannot be deleted", ((TextBlock)window.FindName("StatusText")).Text);
                SetStage("keeping the initial sort visible after column changes and basic scanning");
                CheckVisibleInitialSort(window);
                BeginWorkflow("Opportunities browsing and selection");
                SetStage("browsing thousands of grouped opportunities and retaining hidden selections");
                CheckOpportunityBrowser(window, app, artifactRoot);
                SetStage("inspecting ecosystem-specific build contents and scanned folder sizes");
                CheckBuildContents(window, app, artifactRoot);
                SetStage("browsing additional cleanup categories by tool and project");
                CheckAdditionalCleanupCategories(window, app, artifactRoot);
                SetStage("browsing agent history, checkpoints and model libraries");
                CheckAiStorage(window, app, artifactRoot);
                BeginWorkflow("Containers and WSL");
                CheckContainers(window, app, artifactRoot);
                CheckModels(window, app, artifactRoot);
                BeginWorkflow("agent transcript and log cleanup");
                CheckAgents(window, app, artifactRoot);
                BeginWorkflow("OpenCode session cleanup");
                CheckOpenCodeSessions(window, app, artifactRoot);
                BeginWorkflow("Cleanup review and execution");
                SetStage("validating stale selections before cleanup review");
                CheckOpportunityValidation(window, app, artifactRoot);
                SetStage("reviewing a thousand cleanup folders with bounded pages and bulk exclusions");
                CheckBulkCleanupReview(app, artifactRoot);
                SetStage("confirming batch cleanup and refreshing its affected folders");
                CheckOpportunityCleanup(window, app, artifactRoot);
                SetStage("keeping overlapping extension groups and selections separate");
                CheckOverlappingOpportunityExtensions(window, app, artifactRoot);
                BeginWorkflow("Recycle Bin and helper lifecycle");
                SetStage("confirming selected-drive Recycle Bin cleanup without deleting real bin contents");
                CheckRecycleBinCleanup(window, app, artifactRoot);
                SetStage("retaining one helper across app scans and closing it with the window");
                var helperStart = HelperLayoutSource.CreateAsync(false);
                PumpUntil(() => helperStart.IsCompleted, TimeSpan.FromSeconds(10));
                var appHelper = helperStart.GetAwaiter().GetResult();
                typeof(MainWindow).GetField("_scanHelper", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, appHelper);
                fastFlag.SetValue(window, true);
                window.SetScanTarget(fixture.Path);
                for (int scanNumber = 0; scanNumber < 2; scanNumber++)
                {
                    // Owned folder scans leave the authorized helper idle; no UAC
                    // or user-volume enumeration is needed to check its lifecycle.
                    var appScan = window.ScanCurrentPathAsync();
                    PumpUntil(() => appScan.IsCompleted, TimeSpan.FromSeconds(10));
                    Assert.True(appScan.GetAwaiter().GetResult());
                    Assert.Same(appHelper, typeof(MainWindow).GetField("_scanHelper", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                    Assert.True(appHelper.IsAvailable); Assert.False(appHelper.Exit.IsCompleted);
                }
                SetStage("closing windows");
                string extensionLog = Assert.Single(Directory.GetFiles(Path.Join(window.LogDirectory, "Extensions"), "*.jsonl"));
                startup.Close(); review.Close(); details.Close(); recyclePreview.Close(); window.Close();
                PumpUntil(() => !app.Windows.Cast<Window>().Contains(window), TimeSpan.FromSeconds(10));
                Assert.True(appHelper.Exit.IsCompletedSuccessfully); Assert.False(appHelper.IsAvailable);
                File.Copy(extensionLog, Path.Join(artifactRoot, "artifacts", "extensions", "extension-smoke.jsonl"), true);
            }
            catch (Exception ex) { failure = new InvalidOperationException($"WPF smoke failed while {stage}.", ex); }
            finally
            {
                // Let cleanup pump the dispatcher even after a workflow timed out.
                _desktopCancellation = default;
                try
                {
                    if (window?.IsVisible == true)
                    { window.Close(); PumpUntil(() => !app!.Windows.Cast<Window>().Contains(window), TimeSpan.FromSeconds(10)); }
                    app?.Shutdown(); fixture?.Dispose();
                }
                catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // One Application owns all workflows, but their cumulative rendering and
        // fixture work must not consume another workflow's deadline. Interaction
        // waits retain their own shorter limits; synchronous stalls remain bounded.
        string? timedOut = null;
        while (!thread.Join(TimeSpan.FromMilliseconds(100)))
        {
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref workflowStarted)) <= TimeSpan.FromSeconds(90)) continue;
            timedOut = $"The WPF {Volatile.Read(ref workflow)} workflow timed out while {stage}.";
            timeout.Cancel();
            break;
        }
        if (timedOut is not null)
        {
            bool stopped = thread.Join(TimeSpan.FromSeconds(15));
            Assert.Fail(timedOut + (stopped ? " The test session was closed." : " The test session did not finish closing."));
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }


}
