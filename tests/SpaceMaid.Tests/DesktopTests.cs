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

public sealed class DesktopTests
{
    [Fact]
    public void FluentDesktopLoadsScansPreviewsAndConfirmedFolderRecycling()
    {
        Exception? failure = null;
        string stage = "starting";
        var thread = new Thread(() =>
        {
            Application? app = null;
            TestDirectory? fixture = null;
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                fixture = new TestDirectory();
                string project = Directory.CreateDirectory(Path.Join(fixture.Path, "sample")).FullName;
                File.WriteAllText(Path.Join(project, "sample.csproj"), "<Project />");
                string bin = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
                File.WriteAllBytes(Path.Join(bin, "sample.dll"), new byte[8192]);
                BuildActivityFixture.AgeProject(project);
                stage = "loading application resources";
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001
                app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SpaceMaid.App;component/Styles.xaml", UriKind.Relative) });
                stage = "constructing main window";
                window = new MainWindow();
                window.LogDirectory = Path.Join(fixture.Path, "diagnostics");
                window.ExtensionDataRoot = Path.Join(fixture.Path, "extensions");
                window.DriveHistoryPath = Path.Join(fixture.Path, "drive-history.json");
                window.ColumnLayoutPath = Path.Join(fixture.Path, "columns.json");
                window.CleanupSettingsPath = Path.Join(fixture.Path, "cleanup-settings.json");
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
                stage = "keeping the build retention preference bounded and persistent";
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
                stage = "choosing a drive without starting a scan";
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
                stage = "keeping the fast-scan enable button in sync with session state";
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
                stage = "returning to the drive root without starting a scan";
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
                stage = "scanning fixture through the UI";
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
                stage = "showing historical drive suggestions and recent folders";
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
                stage = "changing drives while editing a full path";
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
                stage = "opening a drive recommendation from the matching inventory";
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
                stage = "scanning before opening a recommendation when inventory is absent";
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

                stage = "navigating the real folder tree and treemap";
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
                stage = "opening folder operations through routed right-click events";
                int binLink = explorer.Scan.PrimaryLink(row.Item.DirectoryIndex);
                int projectLink = explorer.Scan.PrimaryLink(explorer.Scan.Entry(binLink).ParentIndex);
                var projectContainer = Assert.IsAssignableFrom<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(explorer.Find(projectLink)));
                stage = "checking expansion hit targets beyond the arrow glyph";
                window.UpdateLayout();
                var projectExpander = Descendants(projectContainer).OfType<Button>().Single();
                Assert.True(projectExpander.ActualWidth >= 40 && projectExpander.ActualHeight >= 34);
                var expansionTargets = Descendants(projectExpander).Append(projectExpander).ToHashSet();
                foreach (var point in new[] { new Point(2, 2), new Point(projectExpander.ActualWidth - 2, 2),
                    new Point(2, projectExpander.ActualHeight - 2), new Point(projectExpander.ActualWidth - 2, projectExpander.ActualHeight - 2) })
                    Assert.Contains(Assert.IsAssignableFrom<DependencyObject>(tree.InputHitTest(projectExpander.TranslatePoint(point, tree))), expansionTargets);
                stage = "opening folder operations through routed right-click events";
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

                stage = "revealing a map folder beneath a collapsed tree branch";
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

                stage = "sorting tree columns while preserving expansion, selection and map scope";
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
                stage = "keeping a folder menu action bound to its original target";
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

                stage = "constructing cleanup review";
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

                stage = "checking folder Delete confirmation and its safe default";
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

                stage = "recycling a fixture through the actual UI and marking its scan stale";
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
                    Assert.Contains("Rechecked", ((TextBlock)window.FindName("TreeNote")).Text);
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

                stage = "preserving a collapsed root and prior out-of-date scopes during rechecks";
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
                Assert.Contains("Rechecked", ((TextBlock)window.FindName("TreeNote")).Text);
                Assert.Equal("8 KiB", ((TextBlock)window.FindName("CandidateText")).Text);

                stage = "draining an Explorer-return recheck deferred while busy";
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

                stage = "checking an eligible native cloud file menu and cancelling its confirmation";
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

                stage = "preserving grouped-selection deletion safeguards after an unrelated recheck";
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
                }
#pragma warning restore WPF0001
                Render(startup, Path.Join(artifactRoot, "artifacts", "ui", "startup.png"), 570, 305);
                ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", "explorer-compact.png"), 1020, 790);
                Assert.InRange(window.ActualWidth, 1019, 1021);
                Assert.True(((FrameworkElement)window.Content).RenderSize.Width <= 1020, "Compact rendering used the previous window width.");
                var detailPanel = (StackPanel)window.FindName("EntryDetail");
                Assert.True(map.TranslatePoint(new Point(0, map.ActualHeight), (UIElement)window.Content).Y <= detailPanel.TranslatePoint(new Point(), (UIElement)window.Content).Y + 1, "The treemap overlaps its selected-entry details.");

                stage = "checking wide-folder UI virtualization";
                var wideBuilder = new ScanBuilder(trackPreliminary: true);
                wideBuilder.RootIndex = wideBuilder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
                for (int i = 0; i < 50_000; i++)
                {
                    int file = wideBuilder.AddFile(new(new((ulong)i + 2), FileAttributes.Normal, 4096, 4096, 0));
                    wideBuilder.AddLink(file, new(1), $"file-{i}.bin");
                }
                stage = "showing bounded preliminary results";
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
                stage = "sorting preliminary rows and retaining the choice across progress updates";
                foreach (string header in new[] { "PreliminaryNameHeader", "PreliminaryNameHeader", "PreliminaryLogicalHeader", "PreliminaryAllocationHeader" })
                    ((ButtonBase)window.FindName(header)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)window.FindName("PreliminaryNameHeader")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var names = preliminaryFiles.Items.Cast<PreliminaryRow>().Select(r => r.File.Name).ToArray();
                Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), names);
                window.ShowPreliminary(preliminary);
                Assert.Equal(names, preliminaryFiles.Items.Cast<PreliminaryRow>().Select(r => r.File.Name));
                Assert.Equal("File / exact path ↑", ((Button)window.FindName("PreliminaryNameHeader")).Content);
                Assert.Equal(FontWeights.SemiBold, ((Button)window.FindName("PreliminaryNameHeader")).FontWeight);
                Assert.Equal(FontWeights.Normal, ((Button)window.FindName("PreliminaryAllocationHeader")).FontWeight);
#pragma warning disable WPF0001
                foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    app.ThemeMode = mode;
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"preliminary-{(mode == ThemeMode.Light ? "light" : "dark")}.png"), 1220, 910);
                }
#pragma warning restore WPF0001
                stage = "retaining cancelled partial results and resetting a fallback";
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
                stage = "binding the wide-folder indexed source";
                var wideLoad = window.InitializeExplorerAsync(wideScan, [], CancellationToken.None);
                PumpUntil(() => wideLoad.IsCompleted, TimeSpan.FromSeconds(10));
                wideLoad.GetAwaiter().GetResult();
                Assert.Equal(Visibility.Collapsed, preliminaryPanel.Visibility);
                Assert.Empty(preliminaryFiles.Items.Cast<object>());
                Assert.Equal(Visibility.Visible, ((Grid)window.FindName("ExplorePanel")).Visibility);
                var wideContent = (FrameworkElement)window.Content;
                stage = "measuring the wide-folder window";
                window.UpdateLayout();
                Assert.Equal(50_001, tree.Items.Count);
                int realized = Descendants(tree).OfType<ListBoxItem>().Count();
                Assert.True(realized is > 0 and <= 100, $"The wide folder realized {realized:N0} containers.");
                stage = "blocking deletion of grouped map entries";
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
                stage = "disabling recycling on a drive-root context menu";
                RightClick(window, Assert.IsAssignableFrom<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(0)));
                PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.True(((MenuItem)tree.ContextMenu.Items[0]).IsEnabled);
                Assert.False(((MenuItem)tree.ContextMenu.Items[1]).IsEnabled);
                Assert.Contains("Drive roots", Assert.IsType<string>(((MenuItem)tree.ContextMenu.Items[1]).ToolTip));
                RenderMenu(tree.ContextMenu, Path.Join(artifactRoot, "artifacts", "ui", "folder-menu-dark.png"));
                tree.ContextMenu.IsOpen = false;
                RightClick(window, tree);
                Assert.False(tree.ContextMenu.IsOpen); // Blank tree space does not reuse the selected root's menu.
                stage = "scrolling to the final wide-folder entry";
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

                stage = "loading the bundled Photos extension into its own assembly context";
                PumpUntil(() => window.ExtensionsReady, TimeSpan.FromSeconds(10));
                var extensionRuntime = Assert.IsType<SpaceMaid.App.Extensions.ExtensionRuntime>(typeof(MainWindow)
                    .GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                var developerExtension = Assert.Single(extensionRuntime.Entries, value => value.Id == "space-maid.developer");
                Assert.Equal("Loaded", developerExtension.Status); Assert.Null(developerExtension.View);
                var photosView = Assert.IsAssignableFrom<FrameworkElement>(((ContentControl)window.FindName("PhotosContent")).Content);
                Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default,
                    System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(photosView.GetType().Assembly));
                ((RadioButton)window.FindName("PhotosTab")).IsChecked = true;
                Assert.Equal(Visibility.Visible, ((Grid)window.FindName("PhotosPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("OpportunitiesPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("ExplorePanel")).Visibility);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", "photos-empty.png"), 1220, 940);
                Assert.IsType<ComboBox>(photosView.FindName("CollectionList"));
                Assert.Equal(Visibility.Collapsed, ((Border)photosView.FindName("SelectionPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Grid)photosView.FindName("CollectionHeader")).Visibility);
                Assert.Equal("Add folder", ((Button)photosView.FindName("EmptyActionButton")).Content);
                Assert.NotNull(((Image)photosView.FindName("LibraryIllustration")).Source);
                stage = "discovering and analyzing owned photos through the loaded extension and real tool broker";
                PumpUntil(() => !string.IsNullOrEmpty(((TextBlock)photosView.FindName("ToolText")).Text), TimeSpan.FromSeconds(10));
                string photoFolder = Directory.CreateDirectory(Path.Join(fixture.Path, "photo-review")).FullName;
                string originalPhoto = Path.Join(photoFolder, "original.png"), copyPhoto = Path.Join(photoFolder, "copy.png");
                var sample = BitmapSource.Create(200, 100, 96, 96, PixelFormats.Bgra32, null,
                    Enumerable.Repeat((byte)120, 200 * 100 * 4).ToArray(), 200 * 4);
                var photoEncoder = new PngBitmapEncoder(); photoEncoder.Frames.Add(BitmapFrame.Create(sample));
                using (var output = File.Create(originalPhoto)) photoEncoder.Save(output);
                File.Copy(originalPhoto, copyPhoto);
                byte[] originalBytes = File.ReadAllBytes(originalPhoto);
                var addPhotos = (Task)photosView.GetType().GetMethod("AddFolderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(photosView, [photoFolder])!;
                PumpUntil(() => addPhotos.IsCompleted, TimeSpan.FromSeconds(10)); addPhotos.GetAwaiter().GetResult();
                var photoFiles = (DataGrid)photosView.FindName("FileList");
                Assert.Equal(2, photoFiles.Items.Count);
                var photoGallery = (ListBox)photosView.FindName("PhotoGallery");
                Assert.Same(photoFiles.ItemsSource, photoGallery.ItemsSource);
                Assert.Equal(Visibility.Visible, photoGallery.Visibility);
                Assert.Equal(Visibility.Collapsed, photoFiles.Visibility);
                photoGallery.SelectedIndex = 0;
                Assert.Same(photoGallery.SelectedItem, photoFiles.SelectedItem);
                Assert.All(Descendants(photoGallery).OfType<Image>(), image => Assert.Null(image.Source));
                ((Button)photosView.FindName("CloseSelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, ((Grid)photosView.FindName("CollectionHeader")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)photosView.FindName("PageButtons")).Visibility);
                Assert.False(((Expander)photosView.FindName("CollectionDetails")).IsExpanded);
                Assert.Equal(Visibility.Collapsed, photoFiles.Columns.Single(column => column.SortMemberPath == "Status").Visibility);
                Assert.Equal(Visibility.Collapsed, photoFiles.Columns.Single(column => column.SortMemberPath == "Type").Visibility);
                var statusColumnChoice = (MenuItem)photosView.FindName("ShowStatusColumn");
                statusColumnChoice.IsChecked = true; statusColumnChoice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(Visibility.Visible, photoFiles.Columns.Single(column => column.SortMemberPath == "Status").Visibility);
                statusColumnChoice.IsChecked = false; statusColumnChoice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                ((RadioButton)photosView.FindName("FamiliesTab")).IsChecked = true;
                PumpUntil(() => photoFiles.Items.Count == 0 && ((TextBlock)photosView.FindName("EmptyTitle")).Text == "No related files found", TimeSpan.FromSeconds(3));
                Assert.Equal(Visibility.Collapsed, photoFiles.Visibility);
                Assert.Equal("Browse photos", ((Button)photosView.FindName("EmptyActionButton")).Content);
                photosView.GetType().GetMethod("ShowFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(photosView, [new IOException("Owned fixture retry")]);
                Assert.Equal(Visibility.Visible, ((Grid)photosView.FindName("StatusBand")).Visibility);
                Assert.Equal("Try again", ((Button)photosView.FindName("EmptyActionButton")).Content);
                ((Button)photosView.FindName("EmptyActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((TextBlock)photosView.FindName("EmptyTitle")).Text == "No related files found", TimeSpan.FromSeconds(3));
                Assert.Equal(Visibility.Hidden, ((Grid)photosView.FindName("StatusBand")).Visibility);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", "photos-related-empty.png"), 1220, 940);
                ((Button)photosView.FindName("EmptyActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => photoFiles.Items.Count == 2, TimeSpan.FromSeconds(3));
                // The template-based Name column still routes header sorting to
                // catalog paging, with a visible arrow and actual row reordering.
                var nameColumn = photoFiles.Columns.Single(column => column.SortMemberPath == "Name");
                var onSorting = typeof(DataGrid).GetMethod("OnSorting", BindingFlags.Instance | BindingFlags.NonPublic)!;
                onSorting.Invoke(photoFiles, [new DataGridSortingEventArgs(nameColumn)]);
                PumpUntil(() => photoFiles.Items.Count == 2 && PhotoName(photoFiles.Items[0]) == "original.png", TimeSpan.FromSeconds(3));
                Assert.Equal(ListSortDirection.Descending, nameColumn.SortDirection);
                onSorting.Invoke(photoFiles, [new DataGridSortingEventArgs(nameColumn)]);
                PumpUntil(() => photoFiles.Items.Count == 2 && PhotoName(photoFiles.Items[0]) == "copy.png", TimeSpan.FromSeconds(3));
#pragma warning disable WPF0001
                foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    app.ThemeMode = theme;
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"photos-browse-{(theme == ThemeMode.Light ? "light" : "dark")}.png"), 1220, 940);
                }
#pragma warning restore WPF0001
                photoFiles.SelectedIndex = 0; window.UpdateLayout();
                Assert.Equal(Visibility.Visible, ((Border)photosView.FindName("SelectionPanel")).Visibility);
                Assert.Null(((Image)photosView.FindName("SelectedPreview")).Source);
                Assert.Equal(Visibility.Hidden, ((Image)photosView.FindName("SelectedPreview")).Visibility);
                Assert.Contains("Analyze", ((TextBlock)photosView.FindName("PreviewStatusText")).Text);
                ((Button)photosView.FindName("CloseSelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Null(photoFiles.SelectedItem); Assert.Equal(Visibility.Collapsed, ((Border)photosView.FindName("SelectionPanel")).Visibility);
                ((RadioButton)photosView.FindName("DuplicatesTab")).IsChecked = true;
                PumpUntil(() => photoFiles.Items.Count == 0 && Equals(((Button)photosView.FindName("EmptyActionButton")).Content, "Analyze photos"), TimeSpan.FromSeconds(3));
                var analyzePhotos = (Button)photosView.FindName("AnalyzeButton");
                ((Button)photosView.FindName("EmptyActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => analyzePhotos.IsEnabled && ((TextBlock)photosView.FindName("StatusText")).Text.Contains("Analysis complete", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
                ((RadioButton)photosView.FindName("DuplicatesTab")).IsChecked = true;
                PumpUntil(() => photoFiles.Items.Count == 2 && ((TextBlock)photosView.FindName("CoverageText")).Text.Contains("Default-stream equality", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
                photoFiles.SelectedIndex = 0;
                PumpUntil(() => ((Image)photosView.FindName("SelectedPreview")).Source is not null, TimeSpan.FromSeconds(10));
                var photoPreview = Assert.IsAssignableFrom<BitmapSource>(((Image)photosView.FindName("SelectedPreview")).Source);
                Assert.InRange(photoPreview.PixelWidth, 1, 160); Assert.InRange(photoPreview.PixelHeight, 1, 160);
                Assert.Contains("PNG", ((TextBox)photosView.FindName("SelectionText")).Text);
                stage = "sorting the gallery and switching views without losing selection or previews";
                var gallerySelection = photoGallery.SelectedItem;
                ((Button)photosView.FindName("SortDirectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => PhotoName(photoGallery.Items[0]) == "original.png", TimeSpan.FromSeconds(3));
                Assert.Same(gallerySelection, photoGallery.SelectedItem);
                ((Button)photosView.FindName("SortDirectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => PhotoName(photoGallery.Items[0]) == "copy.png", TimeSpan.FromSeconds(3));
                ((RadioButton)photosView.FindName("ListButton")).IsChecked = true; window.UpdateLayout();
                Assert.Equal(Visibility.Visible, photoFiles.Visibility); Assert.Equal(Visibility.Collapsed, photoGallery.Visibility);
                Assert.Same(gallerySelection, photoFiles.SelectedItem);
                ((RadioButton)photosView.FindName("GalleryButton")).IsChecked = true; window.UpdateLayout();
                Assert.Same(gallerySelection, photoGallery.SelectedItem);
                Assert.Same(photoPreview, ((Image)photosView.FindName("SelectedPreview")).Source);
                stage = "refreshing the saved photo page without rebuilding selection or previews";
                var photoPicker = (ComboBox)photosView.FindName("CollectionList");
                var pickerSource = photoPicker.ItemsSource; var pickerSelection = photoPicker.SelectedItem;
                var photoSource = photoFiles.ItemsSource; var photoSelection = photoFiles.SelectedItem;
                var rowContainer = photoFiles.ItemContainerGenerator.ContainerFromItem(photoSelection);
                var galleryContainer = photoGallery.ItemContainerGenerator.ContainerFromItem(photoSelection);
                var changedSelection = 0; photoFiles.SelectionChanged += (_, _) => changedSelection++;
                var collectionChanges = 0;
                ((System.Collections.Specialized.INotifyCollectionChanged)photoSource).CollectionChanged += (_, _) => collectionChanges++;
                var reloadPhotos = photosView.GetType().GetMethod("LoadCollectionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var reload = (Task)reloadPhotos.Invoke(photosView, [null])!;
                PumpUntil(() => reload.IsCompleted, TimeSpan.FromSeconds(3)); reload.GetAwaiter().GetResult(); window.UpdateLayout();
                Assert.Same(pickerSource, photoPicker.ItemsSource); Assert.Same(pickerSelection, photoPicker.SelectedItem);
                Assert.Same(photoSource, photoFiles.ItemsSource); Assert.Same(photoSelection, photoFiles.SelectedItem);
                Assert.Same(rowContainer, photoFiles.ItemContainerGenerator.ContainerFromItem(photoSelection));
                Assert.NotNull(galleryContainer); Assert.Same(galleryContainer, photoGallery.ItemContainerGenerator.ContainerFromItem(photoSelection));
                Assert.Same(photoPreview, ((Image)photosView.FindName("SelectedPreview")).Source);
                Assert.Equal(0, changedSelection); Assert.Equal(0, collectionChanges);
                onSorting.Invoke(photoFiles, [new DataGridSortingEventArgs(nameColumn)]);
                PumpUntil(() => PhotoName(photoFiles.Items[0]) == "original.png", TimeSpan.FromSeconds(3));
                Assert.Same(photoSelection, photoFiles.SelectedItem);
                Assert.Same(photoPreview, ((Image)photosView.FindName("SelectedPreview")).Source);
                onSorting.Invoke(photoFiles, [new DataGridSortingEventArgs(nameColumn)]);
                PumpUntil(() => PhotoName(photoFiles.Items[0]) == "copy.png", TimeSpan.FromSeconds(3));
                // Simulate a page query beating the discovery worker's catalog
                // revocation. Discovery must never republish old analysis.
                ((RadioButton)photosView.FindName("BrowseTab")).IsChecked = true;
                PumpUntil(() => photoFiles.Columns[0].Visibility == Visibility.Collapsed && photoFiles.Items.Count == 2, TimeSpan.FromSeconds(3));
                var discoveringField = photosView.GetType().GetField("_discovering", BindingFlags.Instance | BindingFlags.NonPublic)!;
                discoveringField.SetValue(photosView, true);
                try
                {
                    reload = (Task)reloadPhotos.Invoke(photosView, [null])!;
                    PumpUntil(() => reload.IsCompleted, TimeSpan.FromSeconds(3)); reload.GetAwaiter().GetResult();
                    Assert.Same(photoSelection, photoFiles.SelectedItem);
                    Assert.Null(((Image)photosView.FindName("SelectedPreview")).Source);
                    Assert.DoesNotContain("SHA-256", ((TextBox)photosView.FindName("SelectionText")).Text);
                    Assert.DoesNotContain("Default stream verified", ((TextBox)photosView.FindName("SelectionText")).Text);
                    Assert.Contains("Preliminary", ((TextBlock)photosView.FindName("FreshnessText")).Text);
                }
                finally { discoveringField.SetValue(photosView, false); }
                ((RadioButton)photosView.FindName("DuplicatesTab")).IsChecked = true;
                PumpUntil(() => photoFiles.Items.Count == 2 && ((Image)photosView.FindName("SelectedPreview")).Source is not null, TimeSpan.FromSeconds(3));
                ((Button)photosView.FindName("RetainButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("Review preference", ((TextBox)photosView.FindName("SelectionText")).Text);
                Assert.True(((Expander)photosView.FindName("FileDetails")).IsExpanded);
                Assert.Equal(originalBytes, File.ReadAllBytes(originalPhoto)); Assert.Equal(originalBytes, File.ReadAllBytes(copyPhoto));
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", "photos-duplicates.png"), 1220, 940);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", "photos-duplicates-compact.png"), 1020, 820);
                File.Delete(copyPhoto); // Only the owned fixture; refresh must invalidate earlier evidence.
                ((Button)photosView.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((Button)photosView.FindName("RefreshButton")).IsEnabled && photoFiles.Items.Count == 0, TimeSpan.FromSeconds(10));
                ((RadioButton)photosView.FindName("BrowseTab")).IsChecked = true;
                PumpUntil(() => photoFiles.Items.Count == 1, TimeSpan.FromSeconds(10));
                photoFiles.SelectedIndex = 0;
                Assert.Null(((Image)photosView.FindName("SelectedPreview")).Source);
                Assert.Equal(originalBytes, File.ReadAllBytes(originalPhoto));

                stage = "preserving the photo viewport across metadata refresh and a removed earlier row";
                // Metadata-only fixtures expand one bounded page without adding
                // image decoding or ExifTool work to the existing Application.
                for (var i = 0; i < 104; i++) File.WriteAllBytes(Path.Join(photoFolder, $"z-{i:D3}.jpg"), [1]);
                var photosRefresh = (Button)photosView.FindName("RefreshButton");
                photosRefresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => photosRefresh.IsEnabled && photoGallery.Items.Count == 100, TimeSpan.FromSeconds(3)); window.UpdateLayout();
                var galleryScroll = Descendants(photoGallery).OfType<ScrollViewer>().First();
                galleryScroll.ScrollToVerticalOffset(250); window.UpdateLayout();
                PumpUntil(() => galleryScroll.VerticalOffset > 0, TimeSpan.FromSeconds(2));
                var galleryOffset = galleryScroll.VerticalOffset;
                var galleryReload = (Task)reloadPhotos.Invoke(photosView, [null])!;
                PumpUntil(() => galleryReload.IsCompleted, TimeSpan.FromSeconds(3)); galleryReload.GetAwaiter().GetResult(); window.UpdateLayout();
                Assert.Equal(galleryOffset, galleryScroll.VerticalOffset, 2);
#pragma warning disable WPF0001
                foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    app.ThemeMode = theme;
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"photos-gallery-page-{(theme == ThemeMode.Light ? "light" : "dark")}.png"), 1220, 940);
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"photos-gallery-page-compact-{(theme == ThemeMode.Light ? "light" : "dark")}.png"), 1020, 820);
                }
#pragma warning restore WPF0001
                ((RadioButton)photosView.FindName("ListButton")).IsChecked = true; window.UpdateLayout();
                var listTop = photoFiles.TranslatePoint(new Point(), photosView).Y;
                photosRefresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                Assert.Equal(listTop, photoFiles.TranslatePoint(new Point(), photosView).Y, 2);
                PumpUntil(() => photosRefresh.IsEnabled && photoFiles.Items.Count == 100, TimeSpan.FromSeconds(3)); window.UpdateLayout();
                Assert.Same(photoSource, photoFiles.ItemsSource); Assert.Same(pickerSelection, photoPicker.SelectedItem);
                Assert.Equal(listTop, photoFiles.TranslatePoint(new Point(), photosView).Y, 2);
                photoFiles.SelectedIndex = 70; photoSelection = photoFiles.SelectedItem;
                photoFiles.Columns.Single(column => column.SortMemberPath == "Status").Visibility = Visibility.Visible;
                photoFiles.Columns.Single(column => column.SortMemberPath == "Type").Visibility = Visibility.Visible;
                nameColumn.Width = 400; // Ensure a real horizontal viewport to preserve.
                photoFiles.ScrollIntoView(photoSelection); window.UpdateLayout();
                var photoScroll = Descendants(photoFiles).OfType<ScrollViewer>().First();
                photoScroll.ScrollToHorizontalOffset(60); window.UpdateLayout();
                PumpUntil(() => photoScroll.VerticalOffset > 0 && photoScroll.HorizontalOffset > 0, TimeSpan.FromSeconds(2));
                var topName = PhotoName(photoFiles.Items[(int)photoScroll.VerticalOffset]);
                var vertical = photoScroll.VerticalOffset; var horizontal = photoScroll.HorizontalOffset;
                reload = (Task)reloadPhotos.Invoke(photosView, [null])!;
                PumpUntil(() => reload.IsCompleted, TimeSpan.FromSeconds(3)); reload.GetAwaiter().GetResult(); window.UpdateLayout();
                Assert.Same(photoSelection, photoFiles.SelectedItem);
                Assert.Equal(vertical, photoScroll.VerticalOffset, 2); Assert.Equal(horizontal, photoScroll.HorizontalOffset, 2);
                File.Delete(Path.Join(photoFolder, "z-000.jpg"));
                photosRefresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => photosRefresh.IsEnabled, TimeSpan.FromSeconds(3)); window.UpdateLayout();
                PumpUntil(() => PhotoName(photoFiles.Items[(int)photoScroll.VerticalOffset]) == topName, TimeSpan.FromSeconds(2));
                Assert.Same(photoSelection, photoFiles.SelectedItem);
                Assert.Same(photoSource, photoFiles.ItemsSource); Assert.Equal(horizontal, photoScroll.HorizontalOffset, 2);
                Assert.Equal(ListSortDirection.Ascending, nameColumn.SortDirection);
                Assert.Equal(listTop, photoFiles.TranslatePoint(new Point(), photosView).Y, 2);
                ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;

                static string PhotoName(object display)
                {
                    var value = display.GetType().GetProperty("Row")!.GetValue(display)!;
                    return (string)value.GetType().GetProperty("Name")!.GetValue(value)!;
                }

                stage = "checking Apps extension packaging, sorting, search, cancellation and drive changes";
                CheckApps(window, app, fixture, artifactRoot);
                stage = "rendering framed folders at desktop and compact sizes";
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
                stage = "keeping small overview folders inside their proportional map cells after resizing";
                var crowdedLoad = window.InitializeExplorerAsync(CrowdedMapFixture(), [], default);
                PumpUntil(() => crowdedLoad.IsCompleted, TimeSpan.FromSeconds(10));
                crowdedLoad.GetAwaiter().GetResult();
                foreach (var size in new[] { (1220, 910), (1020, 790), (1220, 910) })
                {
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"framed-folders-crowded-{size.Item1}.png"), size.Item1, size.Item2);
                    AssertTilesContained(map);
                }
                stage = "revealing the parent of a nested grouped tile beneath a collapsed root";
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
                stage = "keeping the initial sort visible after column changes and basic scanning";
                CheckVisibleInitialSort(window);
                stage = "browsing thousands of grouped opportunities and retaining hidden selections";
                CheckOpportunityBrowser(window, app, artifactRoot);
                stage = "browsing additional cleanup categories by tool and project";
                CheckAdditionalCleanupCategories(window, app, artifactRoot);
                stage = "validating stale selections before cleanup review";
                CheckOpportunityValidation(window, app, artifactRoot);
                stage = "reviewing a thousand cleanup folders with bounded pages and bulk exclusions";
                CheckBulkCleanupReview(app, artifactRoot);
                stage = "confirming batch cleanup and refreshing its affected folders";
                CheckOpportunityCleanup(window, app, artifactRoot);
                stage = "keeping overlapping extension groups and selections separate";
                CheckOverlappingOpportunityExtensions(window, app, artifactRoot);
                stage = "retaining one helper across app scans and closing it with the window";
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
                stage = "closing windows";
                string extensionLog = Assert.Single(Directory.GetFiles(Path.Join(window.LogDirectory, "Extensions"), "*.jsonl"));
                startup.Close(); review.Close(); details.Close(); recyclePreview.Close(); window.Close();
                PumpUntil(() => !app.Windows.Cast<Window>().Contains(window), TimeSpan.FromSeconds(10));
                Assert.True(appHelper.Exit.IsCompletedSuccessfully); Assert.False(appHelper.IsAvailable);
                File.Copy(extensionLog, Path.Join(artifactRoot, "artifacts", "extensions", "extension-smoke.jsonl"), true);
            }
            catch (Exception ex) { failure = new InvalidOperationException($"WPF smoke failed while {stage}.", ex); }
            finally
            {
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
        // The bounded smoke now renders both photo views, a full gallery page,
        // two themes and compact layouts alongside the existing workflows.
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), $"The WPF smoke test timed out while {stage}.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void CheckApps(MainWindow window, Application app, TestDirectory fixture, string artifactRoot)
    {
        var content = (ContentControl)window.FindName("AppsContent");
        var packaged = Assert.IsAssignableFrom<FrameworkElement>(content.Content);
        Assert.Equal("SpaceMaid.Extensions.Apps", packaged.GetType().Assembly.GetName().Name);
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(packaged.GetType().Assembly));
        var host = new AppsUiHost(fixture.Path);
        // Exercise the packaged WPF view itself. Creating a second view from the
        // default load context conflicts with WPF's assembly-name resource cache.
        var view = packaged;
        var type = view.GetType(); var assembly = type.Assembly;
        type.GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, host);
        var discoveryProperty = type.GetProperty("Discover", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var appType = assembly.GetType("SpaceMaid.Extensions.Apps.DetectedApp")!;
        var resultType = assembly.GetType("SpaceMaid.Extensions.Apps.AppDiscoveryResult")!;
        object Discover(string drive)
        {
            var apps = Array.CreateInstance(appType, drive == @"R:\" ? 1 : 48);
            for (int i = 0; i < apps.Length; i++)
            {
                string name = drive == @"R:\" ? "Other drive" : i == 0 ? "Baldur's Gate 3" : i == 1 ? "Android Studio" : i == 2 ? "Visual Studio" : $"Application {i:00}";
                apps.SetValue(Activator.CreateInstance(appType, [name, i == 0 ? "Larian Studios" : "Example publisher", "1.0",
                    drive == @"R:\" ? @"R:\Apps\Other" : i == 0 ? fixture.Path : $@"G:\Apps\Application {i:00}",
                    i == 4 ? (long?)4096 : null, "Windows installation record", new[] { name }, false]), i);
            }
            return Activator.CreateInstance(resultType, [apps, Array.Empty<string>()])!;
        }
        var parameters = discoveryProperty.PropertyType.GetMethod("Invoke")!.GetParameters()
            .Select(parameter => System.Linq.Expressions.Expression.Parameter(parameter.ParameterType)).ToArray();
        var invoke = System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant((Func<string, object>)Discover), parameters[0]);
        discoveryProperty.SetValue(view, System.Linq.Expressions.Expression.Lambda(discoveryProperty.PropertyType,
            System.Linq.Expressions.Expression.Convert(invoke, resultType), parameters).Compile());
        Task Refresh() => (Task)type.GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [true])!;
        static string RowText(object row, string property) => (string)row.GetType().GetProperty(property)!.GetValue(row)!;
        window.SetScanTarget(@"G:\");
        ((RadioButton)window.FindName("AppsTab")).IsChecked = true;
        Assert.Equal(Visibility.Visible, ((Grid)window.FindName("AppsPanel")).Visibility);
        var load = Refresh();
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(3)); load.GetAwaiter().GetResult();
        var list = (DataGrid)view.FindName("AppList");
        var search = (TextBox)view.FindName("SearchBox");
        Assert.Equal(48, list.Items.Count);
        Assert.True(host.FreshMeasurements >= 48, "Explicit Refresh must measure current folders instead of reusing an earlier drive scan.");
        Assert.Equal("Baldur's Gate 3", RowText(list.Items[0], "Name"));
        Assert.Equal("On disk", RowText(list.Items[0], "Basis"));
        Assert.Contains(list.Items.Cast<object>(), row => RowText(row, "Basis") == "File size · partial");
        Assert.Contains(list.Items.Cast<object>(), row => RowText(row, "Basis") == "Unavailable");
        list.SelectedIndex = 0;
        ((Button)view.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(fixture.Path, host.Revealed);
#pragma warning disable WPF0001
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            string name = theme == ThemeMode.Light ? "light" : "dark";
            if (theme == ThemeMode.Light) Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"apps-{name}.png"), 1220, 910);
            else Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"apps-compact-{name}.png"), 1020, 790);
            Assert.True(list.ActualHeight > 320, $"Apps table was crowded to {list.ActualHeight:0} pixels.");
            Assert.True(((FrameworkElement)view.FindName("AddButton")).TranslatePoint(new Point(), view).X >= 0);
        }
#pragma warning restore WPF0001
        // Sort through the actual DataGrid header, then keep this user sort on refresh.
        var header = Descendants(list).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>()
            .Single(item => Equals(item.Content, "App"));
        typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(header, null);
        Assert.Equal(ListSortDirection.Ascending, list.Columns[0].SortDirection);
        search.Text = "Android"; Assert.Single(list.Items.Cast<object>());
        list.SelectedIndex = 0; var selection = list.SelectedItem;
        ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        ((RadioButton)window.FindName("AppsTab")).IsChecked = true;
        Assert.Equal("Android", search.Text); Assert.Same(selection, list.SelectedItem);
        search.Text = "no such app"; Assert.Empty(list.Items.Cast<object>());
        Assert.Equal(Visibility.Visible, ((StackPanel)view.FindName("EmptyPanel")).Visibility);
        search.Text = "";
        host.Block = true;
        load = Refresh();
        PumpUntil(() => host.Waiting, TimeSpan.FromSeconds(2));
        ((Button)view.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(2)); load.GetAwaiter().GetResult();
        Assert.Equal(48, list.Items.Count);
        Assert.Contains("Cancelled", ((TextBlock)view.FindName("StatusText")).Text);
        Assert.True(((Button)view.FindName("RefreshButton")).IsEnabled);
        // Cancel an old drive's in-flight measurement; it cannot populate the next drive.
        host.Waiting = false; load = Refresh();
        PumpUntil(() => host.Waiting, TimeSpan.FromSeconds(2));
        host.Block = false; ((IDriveExtensionView)view).SetDrive(@"R:\");
        var other = Refresh();
        PumpUntil(() => other.IsCompleted, TimeSpan.FromSeconds(2)); other.GetAwaiter().GetResult();
        Assert.Equal("Other drive", RowText(Assert.Single(list.Items.Cast<object>()), "Name"));
        Assert.Equal(ListSortDirection.Ascending, list.Columns[0].SortDirection);
        var stop = (Task)type.GetMethod("ShutdownAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        PumpUntil(() => stop.IsCompleted, TimeSpan.FromSeconds(2)); stop.GetAwaiter().GetResult();
        ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        content.Content = packaged;
    }

    private sealed class AppsUiHost(string path) : IExtensionHost, IInventoryReader, IInventoryFolderSizes
    {
        public string DataDirectory => path;
        public string CacheDirectory => path;
        public IInventoryReader Inventory => this;
        public IExtensionJobs Jobs => new PhotoTestJobs();
        public IToolBroker Tools => new PhotoTestTools();
        public string? Revealed { get; private set; }
        public bool Block { get; set; }
        public bool Waiting { get; set; }
        public int FreshMeasurements { get; private set; }
        public void RevealLocation(string location) => Revealed = location;
        public void Log(string eventName, object data) { }
        public async Task<InventoryFolderSize?> ReadFolderSizeAsync(string location, bool refresh, CancellationToken token)
        {
            await Task.Yield(); token.ThrowIfCancellationRequested();
            if (refresh) FreshMeasurements++;
            if (Block) { Waiting = true; await Task.Delay(Timeout.Infinite, token); }
            if (location.EndsWith("03", StringComparison.Ordinal)) throw new IOException("Owned unavailable-folder fixture.");
            long size = location == path ? GiB(146) : location.EndsWith("01", StringComparison.Ordinal) ? GiB(8.4) : GiB(1.2);
            return new(size, location.EndsWith("02", StringComparison.Ordinal) ? null : size,
                !location.EndsWith("02", StringComparison.Ordinal), DateTime.UtcNow, "Drive scan", []);
        }
        public IAsyncEnumerable<InventoryBatch> ReadAsync(string root, CancellationToken token, bool preferExistingInventory = false) => throw new NotSupportedException();
        public string? ResolveLocation(InventoryReference reference) => null;
    }

    private static long GiB(double value) => (long)(value * 1024 * 1024 * 1024);

    private static void CheckOverlappingOpportunityExtensions(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        var runtime = Assert.IsType<SpaceMaid.App.Extensions.ExtensionRuntime>(typeof(MainWindow)
            .GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var provider = new OverlappingOpportunityProvider(fixture.Path);
        runtime.Opportunities.Register("test.previews", provider);
        string project = Directory.CreateDirectory(Path.Join(fixture.Path, "project")).FullName;
        string bin = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
        File.WriteAllText(Path.Join(project, "sample.csproj"), "<Project />");
        string file = Path.Join(bin, "sample.dll"); File.WriteAllBytes(file, new byte[5000]);
        BuildActivityFixture.AgeProject(project);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(5)); var scan = scanning.GetAwaiter().GetResult();
        var analysis = runtime.Opportunities.Analyze(scan, CancellationToken.None);
        Assert.Equal(2, analysis.Findings.Count);
        Assert.Single(analysis.Findings.Select(item => item.Path).Distinct());
        ((TextBox)window.FindName("OpportunitySearch")).Text = "";
        ((ComboBox)window.FindName("OpportunityTypeFilter")).SelectedIndex = 0;
        var load = window.InitializeExplorerAsync(scan, analysis.Findings, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, analysis.Findings, analysis.Groups, null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        var grouping = (ComboBox)window.FindName("OpportunityGroupingPicker");
        grouping.SelectedIndex = 0;
        var cards = (ItemsControl)window.FindName("CleanupGroupCards");
        var list = (ListBox)window.FindName("OpportunityGrid");
        var groups = cards.Items.Cast<OpportunityGroupRow>().ToArray();
        Assert.Equal(3, groups.Length);
        var developer = Assert.Single(groups, group => group.Group.ProviderId == DeveloperOpportunityFixture.ProviderId);
        var previewGroups = groups.Where(group => group.Group.ProviderId == "test.previews").ToArray();
        Assert.Equal(2, previewGroups.Length);
        Assert.All(groups, group => Assert.Equal(group.Group.ProviderId, Assert.Single(group.Matches).Item.ProviderId));
        Assert.Same(previewGroups[0].Matches[0], previewGroups[1].Matches[0]);
        Assert.NotSame(developer.Matches[0], previewGroups[0].Matches[0]);
        window.UpdateLayout();
        var previewContainer = cards.ItemContainerGenerator.ContainerFromItem(previewGroups[0]);
        Descendants(previewContainer).OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, previewGroups[0]))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(previewGroups[0].Checked); Assert.True(previewGroups[1].Checked); Assert.False(developer.Checked);
        Assert.False(developer.Matches[0].Selected);
        var refresh = window.RecheckFolderAsync(bin);
        PumpUntil(() => refresh.IsCompleted, TimeSpan.FromSeconds(5)); refresh.GetAwaiter().GetResult();
        groups = cards.Items.Cast<OpportunityGroupRow>().ToArray();
        developer = Assert.Single(groups, group => group.Group.ProviderId == DeveloperOpportunityFixture.ProviderId);
        previewGroups = groups.Where(group => group.Group.ProviderId == "test.previews").ToArray();
        Assert.Equal(2, previewGroups.Length);
        Assert.True(previewGroups[0].Checked); Assert.True(previewGroups[1].Checked); Assert.False(developer.Checked);
        Assert.False(developer.Matches[0].Selected); // Same path never transfers consent between providers.
        window.UpdateLayout();
        previewContainer = cards.ItemContainerGenerator.ContainerFromItem(previewGroups[0]);
        Descendants(previewContainer).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, previewGroups[0]))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("test.previews", Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>())).Item.ProviderId);
        ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        grouping.SelectedIndex = 1;
        var navigation = (ListBox)window.FindName("OpportunityGroupList");
        Assert.Equal(3, navigation.Items.Count); // One developer project plus two overlapping extension project groups.
        foreach (var group in navigation.Items.Cast<OpportunityGroupRow>().ToArray())
        {
            navigation.SelectedItem = group;
            var row = Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>()));
            Assert.Equal(group.Group.ProviderId, row.Item.ProviderId);
            Assert.Same(row, Assert.Single(group.Matches));
            Assert.False(row.Selected);
        }
        grouping.SelectedIndex = 0;
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-extension-overlap.png"), 1220, 910);
        grouping.SelectedIndex = 1;
        ((Button)window.FindName("AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, list.Items.Count);
        foreach (var row in list.Items.Cast<OpportunityRow>()) row.Selected = true;
        provider.Enabled = false; // Only this provider's rule disappears during fresh review.
        Exception? failure = null; bool reviewed = false;
        var closeReview = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            var dialog = app.Windows.OfType<ReviewWindow>().FirstOrDefault(candidate => candidate.IsVisible);
            if (dialog is null) return;
            try
            {
                reviewed = true;
                var reviewedLocation = Assert.IsType<CleanupReviewRow>(Assert.Single(((ListBox)dialog.FindName("LocationList")).Items.Cast<object>()));
                Assert.Equal(DeveloperOpportunityFixture.ProviderId, reviewedLocation.Location.ProviderId);
                Assert.Equal(bin, reviewedLocation.Path);
            }
            catch (Exception error) { failure = error; }
            finally { dialog.Close(); }
        }, Dispatcher.CurrentDispatcher);
        try
        {
            var review = window.ReviewSelectionAsync();
            PumpUntil(() => review.IsCompleted, TimeSpan.FromSeconds(10)); review.GetAwaiter().GetResult();
            if (failure is not null) throw failure;
            Assert.True(reviewed);
            var retained = Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>()));
            Assert.Equal(DeveloperOpportunityFixture.ProviderId, retained.Item.ProviderId);
            Assert.True(retained.Selected); // Excluding another provider at this path must retain this valid selection.
            Assert.Equal(5000, new FileInfo(file).Length);
        }
        finally { closeReview.Stop(); }
    }

    private sealed class OverlappingOpportunityProvider(string rootPath) : IOpportunityExtension
    {
        internal bool Enabled { get; set; } = true;
        public OpportunityAnalysis Analyze(IOpportunityInventory inventory, CancellationToken token)
        {
            if (!Enabled || inventory.RootPath != rootPath) return new([], new([], []));
            int directory = Enumerable.Range(0, inventory.FileCount).Single(index => inventory.IsDirectory(index)
                && inventory.PrimaryLink(index) is >= 0 and var link && inventory.Name(link).SequenceEqual("bin"));
            var finding = new Opportunity(directory, "Generated previews", "Preview cache", inventory.PathForFile(directory)!,
                inventory.LogicalSize(directory), inventory.AllocatedSize(directory), "Generated fixture previews", "Review custom files", "Recycle", true)
            { RuleId = "previews" };
            OpportunityGroup Group(string key, string name) => new(key, name, inventory.RootPath, "Review", [finding], finding.AllocatedBytes);
            return new([finding], new([Group("project:first", "Preview project"), Group("project:second", "Media project")], [],
                Cleanup: [Group("cleanup:previews", "Preview cache"), Group("cleanup:review", "Previews to review")]));
        }
    }

    private static void CheckOpportunityBrowser(MainWindow window, Application app, string artifactRoot)
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        ulong id = 2;
        var folders = new List<(int Index, string Title, string Type)>();
        for (int project = 0; project < 200; project++)
        {
            var repo = Folder(1, project == 0 ? "Chatty-NPC" : project == 1 ? "good-shit" : $"Project-{project:D3}");
            Folder(repo.Identity, ".git");
            for (int item = 0; item < 20; item++)
            {
                var module = Folder(repo.Identity, $"module-{item:D2}");
                string name = item == 8 ? "dist" : item == 12 ? "__pycache__" : (item % 4) switch { 0 => "obj", 1 => "node_modules", 2 => ".venv", _ => "checkout" };
                if (item % 4 == 0 && item != 12)
                {
                    int manifest = builder.AddFile(new(new(id++), FileAttributes.Normal, 0, 0, 0));
                    builder.AddLink(manifest, new(module.Identity), item == 8 ? "package.json" : item == 16 ? "app.fsproj" : "app.csproj");
                }
                var folder = Folder(module.Identity, name);
                int file = builder.AddFile(new(new(id++), FileAttributes.Normal, (200 - project) * 256L * 1024, (200 - project) * 256L * 1024, 0));
                builder.AddLink(file, new(folder.Identity), item == 12 ? "data.pyc" : "data.bin");
                if (item % 4 == 3)
                { int marker = builder.AddFile(new(new(id++), FileAttributes.Normal, 0, 0, 0)); builder.AddLink(marker, new(folder.Identity), ".git"); }
                folders.Add((folder.Index, item == 8 ? "Possible Node build output" : item == 12 ? "Python bytecode cache" : (item % 4) switch { 0 => ".NET build output", 1 => "Node dependencies", 2 => "Python virtual environment", _ => "Linked Git checkout" },
                    (item % 4) switch { 0 => "Build output", 1 => "Dependencies", 2 => "Python environments", _ => "Git worktrees" }));
            }
        }
        var scan = builder.Build(@"G:\Github", "Illustrative inventory", TimeSpan.Zero, 0, default);
        var locations = folders.Select(folder => OpportunityGroupingTests.Finding(scan, folder.Index, folder.Type) with { Title = folder.Title }).ToArray();
        var groups = OpportunityGrouping.Build(scan, locations);
        var load = window.InitializeExplorerAsync(scan, locations, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(10)); load.GetAwaiter().GetResult();
        var publish = typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!;
        publish.Invoke(window, [scan, locations, groups, null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        var list = (ListBox)window.FindName("OpportunityGrid");
        var navigation = (ListBox)window.FindName("OpportunityGroupList");
        var search = (TextBox)window.FindName("OpportunitySearch");
        var grouping = (ComboBox)window.FindName("OpportunityGroupingPicker");
        var filter = (ComboBox)window.FindName("OpportunityTypeFilter");
        var selection = (TextBlock)window.FindName("SelectionText");
        grouping.SelectedIndex = 0;
        Assert.Equal(Visibility.Visible, ((Border)window.FindName("CleanupOverview")).Visibility);
        var cards = (ItemsControl)window.FindName("CleanupGroupCards");
        Assert.Equal(4, cards.Items.Count); Assert.Empty(list.Items);
        Assert.Equal(100, cards.Items.Cast<OpportunityGroupRow>().Max(group => group.RelativeSize));
        Assert.All(cards.Items.Cast<OpportunityGroupRow>(), group => Assert.InRange(group.RelativeSize, 0, 100));
        Assert.False(cards.Items.Cast<OpportunityGroupRow>().Single(group => group.Name == "Other worktrees").CanSelectGroup);
        Assert.Equal(1000, cards.Items.Cast<OpportunityGroupRow>().Single(group => group.Name == "Build files").Matches.Count);
        window.UpdateLayout();
        var firstCard = cards.ItemContainerGenerator.ContainerFromIndex(0);
        var groupCheck = Descendants(firstCard).OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, cards.Items[0]));
        groupCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("1 group selected · 1,000 folders", selection.Text);
        Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
        Descendants(firstCard).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, cards.Items[0])).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1000, list.Items.Count); Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
        Assert.IsType<OpportunityRow>(list.Items[0]).Selected = false;
        Assert.Equal(Visibility.Visible, ((Button)window.FindName("BackToCleanupButton")).Visibility);
        ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(list.Items); Assert.Equal("1 group selected · 999 folders", selection.Text);
        Assert.Null(cards.Items.Cast<OpportunityGroupRow>().First().Checked);
        window.UpdateLayout();
        Descendants(cards.ItemContainerGenerator.ContainerFromIndex(0)).OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, cards.Items[0])).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("1 group selected · 1,000 folders", selection.Text);
        ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((TextBlock)window.FindName("ScannedSizeText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("ScannedSizeCaption")).Text = "On disk";
        ((TextBlock)window.FindName("CandidateText")).Text = SizeText.Format(CleanupPlanner.Create(scan, locations).EstimatedBytes);
        ((TextBlock)window.FindName("CandidateCaption")).Text = "Estimate · 4 groups";
        ((TextBlock)window.FindName("StatusText")).Text = "Illustrative inventory · 200 projects, 4,000 locations";
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            string theme = mode == ThemeMode.Light ? "light" : "dark";
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-groups-{theme}.png"), 1220, 910);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-groups-compact-{theme}.png"), 1020, 790);
        }
#pragma warning restore WPF0001
        CheckBuildClassification();
        CheckWorktreeFilters();
        grouping.SelectedIndex = 1;
        navigation.SelectedIndex = 0;
        Assert.Equal(200, navigation.Items.Count); Assert.Equal(20, list.Items.Count);
        Assert.Equal("Chatty-NPC", ((TextBlock)window.FindName("OpportunityHeading")).Text);
        Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.DoesNotContain(@"G:\", row.DisplayPath));
        ((Button)window.FindName("SelectListedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("20 locations selected", selection.Text);
        search.Text = "good-shit";
        PumpUntil(() => list.Items.Count == 20 && ((OpportunityRow)list.Items[0]).GroupName == "good-shit", TimeSpan.FromSeconds(3));
        Assert.Single(navigation.Items.Cast<object>()); Assert.Contains("20 outside this view", selection.Text);
        navigation.SelectedIndex = 0;
        ((Button)window.FindName("SelectListedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        grouping.SelectedIndex = 2; filter.SelectedItem = "Build output"; navigation.SelectedIndex = 0;
        Assert.Equal(5, list.Items.Count); Assert.Contains("35 outside this view", selection.Text);
        var selected = locations.Where(location => location.Path.Contains("Chatty-NPC", StringComparison.Ordinal) || location.Path.Contains("good-shit", StringComparison.Ordinal))
            .Select(location => (location.ProviderId, location.Path, location.RuleId)).ToHashSet();
        publish.Invoke(window, [scan, locations, groups, selected, ((OpportunityRow)list.SelectedItem).Path, true]);
        Assert.Equal("Build output", ((TextBlock)window.FindName("OpportunityHeading")).Text);
        Assert.Equal("good-shit", search.Text); Assert.Equal("Build output", filter.SelectedItem);
        Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
        search.Text = "no-such-location";
        PumpUntil(() => list.Items.Count == 0, TimeSpan.FromSeconds(3));
        Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("EmptyState")).Visibility);
        Assert.Contains("40 outside this view", selection.Text);
        // This inventory is illustrative and must never be rechecked on disk.
        var preview = new ReviewWindow(CleanupPlanner.Create(scan, locations.Where(location => selected.Contains((location.ProviderId, location.Path, location.RuleId)))));
        Assert.Equal(40, ((ListBox)preview.FindName("GroupList")).Items.Cast<CleanupReviewGroup>().Single(group => group.Key == "all").Rows.Count);
        Assert.InRange(((ListBox)preview.FindName("LocationList")).Items.Count, 0, 40);
        Assert.False(Descendants(preview.Content as DependencyObject).OfType<Button>().Single(button => Equals(button.Content, "Move to Recycle Bin")).IsEnabled);
        preview.Close();
        search.Text = ""; filter.SelectedIndex = 0; grouping.SelectedIndex = 1;
        ((Button)window.FindName("AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(4000, list.Items.Count);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "opportunity-groups-all.png"), 1220, 910);
        Assert.InRange(Descendants(list).OfType<ListBoxItem>().Count(), 1, 80);
        Assert.InRange(Descendants(navigation).OfType<ListBoxItem>().Count(), 1, 40);
        navigation.SelectedItem = navigation.Items.Cast<OpportunityGroupRow>().Single(group => group.Name == "Chatty-NPC");
        ((TextBlock)window.FindName("ScannedSizeText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("CandidateText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("StatusText")).Text = "Illustrative inventory · 200 projects, 4,000 locations";
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            string theme = mode == ThemeMode.Light ? "light" : "dark";
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"opportunity-groups-{theme}.png"), 1220, 910);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"opportunity-groups-compact-{theme}.png"), 1020, 790);
        }
#pragma warning restore WPF0001
        window.MarkScanStale("Illustrative out-of-date results; recheck or rescan.");
        Assert.Equal("Chatty-NPC", ((TextBlock)window.FindName("OpportunityHeading")).Text);
        Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("CleanupFreshnessText")).Visibility);
        Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
        Assert.True(((Button)window.FindName("SelectListedButton")).IsEnabled);
        window.UpdateLayout();
        double listHeight = list.ActualHeight;
        string fullWarning = "Could not recycle the folder: The recheck was incomplete. "
            + string.Join(" ", Enumerable.Repeat(@"Junction target not followed: G:\$RECYCLE.BIN\fixture\.cache.", 25))
            + " Results may be out of date.";
        window.MarkScanStale(fullWarning); window.UpdateLayout();
        Assert.Equal(listHeight, list.ActualHeight, 2); // Full diagnostics cannot squeeze the default list.
        Assert.InRange(((TextBlock)window.FindName("StatusText")).ActualHeight, 1, 20);
        var diagnosticsToggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("DiagnosticsToggle");
        Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("DiagnosticsPanel")).Visibility);
        diagnosticsToggle.IsChecked = true; window.UpdateLayout();
        var fullMessage = (TextBox)window.FindName("DiagnosticMessage");
        Assert.True(fullMessage.IsVisible); Assert.True(fullMessage.IsReadOnly); Assert.Equal(fullWarning, fullMessage.Text);
        Assert.InRange(fullMessage.ActualHeight, 1, 160);
        diagnosticsToggle.IsChecked = false; window.UpdateLayout();
        Assert.Equal(listHeight, list.ActualHeight, 2);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "opportunity-groups-stale-dark.png"), 1220, 910);
        ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.DoesNotContain("selected", selection.Text);

        void CheckBuildClassification()
        {
            var build = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == "Build files");
            Assert.Equal(4, build.Children.Count);
            var csharp = Assert.Single(build.Children, row => row.Name == "C# / .NET");
            Assert.Equal(400, csharp.Matches.Count); Assert.Equal("200 projects", csharp.ProjectCount);
            Assert.Equal(1000, build.Children.Sum(child => child.Matches.Count));
            window.UpdateLayout();
            var container = cards.ItemContainerGenerator.ContainerFromItem(build);
            Assert.Single(Descendants(container).OfType<Expander>()).IsExpanded = true; window.UpdateLayout();
            var checkbox = Descendants(container).OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, csharp));
            checkbox.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(csharp.Checked); Assert.Null(build.Checked);
            Assert.Equal("1 group selected · 400 folders", selection.Text);
            Descendants(container).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, csharp)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Build files › C# / .NET", ((TextBlock)window.FindName("CleanupScopeText")).Text);
            Assert.Equal(200, navigation.Items.Count); Assert.Equal(2, list.Items.Count);
            Assert.Equal("Chatty-NPC", ((TextBlock)window.FindName("OpportunityHeading")).Text);
            ((Button)window.FindName("AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(400, list.Items.Count);
            Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.Equal(".NET build output", row.Title));
            navigation.SelectedItem = navigation.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == "Chatty-NPC");
            Assert.Equal(2, list.Items.Count); Assert.Contains("398 outside this view", selection.Text);
            Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
            // Retain language/project scope and existing selections through refreshed metadata publication.
            var chosen = csharp.Matches.Select(row => row.SelectionIdentity).ToHashSet();
            publish.Invoke(window, [scan, locations, groups, chosen, ((OpportunityRow)list.SelectedItem).Path, true]);
            Assert.Equal(2, list.Items.Count); Assert.Equal("Chatty-NPC", ((TextBlock)window.FindName("OpportunityHeading")).Text);
            Assert.Equal("Build files › C# / .NET", ((TextBlock)window.FindName("CleanupScopeText")).Text);
            var withoutProject = locations.Where(item => !item.Path.Contains("Chatty-NPC", StringComparison.Ordinal) || !chosen.Contains((item.ProviderId, item.Path, item.RuleId))).ToArray();
            publish.Invoke(window, [scan, withoutProject, OpportunityGrouping.Build(scan, withoutProject), chosen, null, true]);
            Assert.Empty(list.Items); Assert.Equal(199, navigation.Items.Count);
            Assert.Equal("Chatty-NPC", ((TextBlock)window.FindName("OpportunityHeading")).Text);
            Assert.Contains("No matching folders remain", ((TextBlock)window.FindName("OpportunityResultSummary")).Text);
            Assert.False(((Button)window.FindName("SelectListedButton")).IsEnabled);
            publish.Invoke(window, [scan, locations, groups, chosen, null, true]);
            Assert.Equal(2, list.Items.Count); // Restoring the project keeps the same scope.
            search.Text = "good-shit";
            PumpUntil(() => list.Items.Count == 2 && ((OpportunityRow)list.Items[0]).GroupName == "good-shit", TimeSpan.FromSeconds(3));
            Assert.Single(navigation.Items.Cast<object>());
            navigation.SelectedIndex = 0;
#pragma warning disable WPF0001
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = mode;
                string theme = mode == ThemeMode.Light ? "light" : "dark";
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"build-projects-{theme}.png"), 1220, 910);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"build-projects-compact-{theme}.png"), 1020, 790);
            }
#pragma warning restore WPF0001
            search.Text = "node_modules";
            PumpUntil(() => list.Items.Count == 0, TimeSpan.FromSeconds(3)); // Never escape the selected build-language scope.
            ((Button)window.FindName("SelectListedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("400 outside this view", selection.Text);
            var remaining = locations.Where(item => !chosen.Contains((item.ProviderId, item.Path, item.RuleId))).ToArray();
            publish.Invoke(window, [scan, remaining, OpportunityGrouping.Build(scan, remaining), chosen, null, true]);
            Assert.Empty(navigation.Items); Assert.Empty(list.Items);
            Assert.Equal("Build files › C# / .NET", ((TextBlock)window.FindName("CleanupScopeText")).Text);
            ((Button)window.FindName("AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(list.Items); // A disappeared classification does not expose unrelated findings.
            publish.Invoke(window, [scan, locations, groups, chosen, null, true]);
            ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("CleanupScopeText")).Visibility);
            build = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == "Build files");
            Assert.Null(build.Checked); Assert.True(build.Children.Single(row => row.Name == "C# / .NET").Checked);
            ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.All(build.Children, child => Assert.False(child.Checked));
        }

        void CheckWorktreeFilters()
        {
            // Cached facts exercise rule changes without running Git against this illustrative inventory.
            var facts = (Dictionary<string, GitWorktreeAssessment>)typeof(MainWindow)
                .GetField("_worktreeAssessments", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var worktrees = locations.Where(item => item.Category == "Git worktrees").Take(4).ToArray();
            var now = DateTime.UtcNow;
            AddFact(0, 75, integrated: true); AddFact(1, 45, integrated: false);
            AddFact(2, 10, integrated: true); AddFact(3, 100, integrated: true, clean: false);
            var rebuild = typeof(MainWindow).GetMethod("RebuildWorktreeGroups", BindingFlags.Instance | BindingFlags.NonPublic)!;
            rebuild.Invoke(window, null);
            typeof(MainWindow).GetMethod("ApplyOpportunityFilters", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null]);
            var worktreeOptions = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("WorktreeOptionsToggle");
            var viewOptions = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("ViewOptionsToggle");
            Assert.True(worktreeOptions.IsVisible);
            viewOptions.IsChecked = true; viewOptions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            worktreeOptions.IsChecked = true; worktreeOptions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Assert.Equal(Visibility.Visible, ((Border)window.FindName("WorktreeCleanupControls")).Visibility);
            Assert.False(viewOptions.IsChecked);
            Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("ViewOptionsPanel")).Visibility);
            var rule = (ComboBox)window.FindName("WorktreeRulePicker");
            var slider = (Slider)window.FindName("WorktreeAgeSlider");
            var threshold = (TextBlock)window.FindName("WorktreeAgeText");
            var summary = (TextBlock)window.FindName("WorktreeCheckSummary");
            var selectMatches = (Button)window.FindName("SelectWorktreeMatchesButton");
            Assert.Contains("1 worktree matches", summary.Text);
            Assert.Single(MatchingGroup().Matches);
            Assert.True(selectMatches.IsEnabled);
            selectMatches.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var capture = typeof(MainWindow).GetMethod("SelectedCheckedWorktrees", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var firstCapture = Capture([worktrees[0]]);
            Assert.Equal(WorktreeCleanupMode.OldAndIntegrated, firstCapture[worktrees[0].Path].Mode);

            rule.SelectedIndex = 1; slider.Value = 35;
            Assert.Equal("35 days", threshold.Text); Assert.True(slider.IsEnabled);
            Assert.Equal(2, MatchingGroup().Matches.Count);
            Assert.Single(MatchingGroup().Matches, row => row.Selected); // Never select new matches automatically.
            Assert.Equal(WorktreeCleanupMode.Old, Capture([worktrees[0]])[worktrees[0].Path].Mode);
            Assert.Equal(WorktreeCleanupMode.OldAndIntegrated, firstCapture[worktrees[0].Path].Mode); // Immutable review capture.
            slider.Value = 90;
            Assert.DoesNotContain(cards.Items.Cast<OpportunityGroupRow>(), row => row.Group.Key == "cleanup:old-worktrees");
            Assert.False(selectMatches.IsEnabled);
            Assert.DoesNotContain("selected", selection.Text);
            rule.SelectedIndex = 2;
            Assert.False(slider.IsEnabled); Assert.Equal("Any age", threshold.Text);
            Assert.Equal(2, MatchingGroup().Matches.Count);
            Assert.All(MatchingGroup().Matches, row => Assert.False(row.Selected));
            Assert.Contains("2 worktrees match", summary.Text);
            Assert.DoesNotContain(MatchingGroup().Matches, row => row.Path == worktrees[3].Path); // Local changes block every rule.
            selectMatches.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.All(MatchingGroup().Matches, row => Assert.True(row.Selected));
            var retained = Capture([worktrees[0], worktrees[2]]);
            var selectedWorktrees = locations.Where(item => retained.ContainsKey(item.Path))
                .Select(item => (item.ProviderId, item.Path, item.RuleId)).ToHashSet();
            publish.Invoke(window, [scan, locations, groups, selectedWorktrees, worktrees[0].Path, true]);
            Assert.Empty(facts); // Refresh invalidates displayed checks, while selected paths retain their guard policy.
            Assert.Equal(2, Capture([worktrees[0], worktrees[2]]).Count);
            Assert.All(Capture([worktrees[0], worktrees[2]]).Values, policy => Assert.Equal(WorktreeCleanupMode.Integrated, policy.Mode));
            Assert.All(retained.Values, policy => Assert.Equal(WorktreeCleanupMode.Integrated, policy.Mode));

            foreach (var index in new[] { 0, 1, 2, 3 })
                AddFact(index, index switch { 0 => 75, 1 => 45, 2 => 10, _ => 100 }, index != 1, index != 3);
            rule.SelectedIndex = 1; slider.Value = 35;
            Assert.DoesNotContain(MatchingGroup().Matches, row => row.Selected);
            selectMatches.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("1 group selected · 2 folders", selection.Text);
            worktreeOptions.IsChecked = false;
            Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
            ((TextBlock)window.FindName("StatusText")).Text = "Illustrative worktree checks · No Git commands or cleanup run";
#pragma warning disable WPF0001
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = mode;
                string theme = mode == ThemeMode.Light ? "light" : "dark";
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"worktree-cleanup-{theme}.png"), 1220, 910);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"worktree-cleanup-compact-{theme}.png"), 1020, 790);
            }
#pragma warning restore WPF0001
            ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            rule.SelectedIndex = 0; slider.Value = 30;
            publish.Invoke(window, [scan, locations, groups, null, null, false]);
            Assert.Equal(4, cards.Items.Count);

            OpportunityGroupRow MatchingGroup() => cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Group.Key == "cleanup:old-worktrees");
            IReadOnlyDictionary<string, WorktreeCleanupPolicy> Capture(Opportunity[] chosen) =>
                (IReadOnlyDictionary<string, WorktreeCleanupPolicy>)capture.Invoke(window, [chosen])!;
            void AddFact(int index, int days, bool integrated, bool clean = true)
            {
                var activity = now.AddDays(-days);
                facts[worktrees[index].Path] = new(worktrees[index].Path, clean && integrated && days >= 30, "Fixture", "Illustrative facts",
                    LastObservedModificationUtc: activity, LastReflogActivityUtc: activity,
                    VerifiedClean: clean, Integrated: integrated, HasActivityEvidence: true);
            }
        }

        (int Index, ulong Identity) Folder(ulong parent, string name)
        { ulong identity = id++; int index = builder.AddFile(new(new(identity), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(index, new(parent), name); return (index, identity); }
    }

    private static void CheckAdditionalCleanupCategories(MainWindow window, Application app, string artifactRoot)
    {
        var builder = new ScanBuilder(); ulong next = 2;
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        var items = new List<(int Index, string Title, string Category)>();
        for (int project = 0; project < 2; project++)
        {
            ulong repo = next++; Folder(repo, 1, project == 0 ? "photo-tools" : "web-client"); Folder(next++, repo, ".git");
            Add(repo, ".pytest_cache", "pytest cache", "Tool caches", 1.4);
            Add(repo, ".ruff_cache", "Ruff cache", "Tool caches", 0.8);
            Add(repo, "test-results", "Playwright test results", "Test artifacts", 0.9);
            Add(repo, "playwright-report", "Playwright HTML report", "Test artifacts", 0.4);
            if (project != 0) continue;
            foreach (string title in new[] { "Cargo", "Maven", "Gradle", "CMake", "SvelteKit", "Nuxt" })
                Add(repo, title + "-output", title + " build output", "Build output", 0.6);
            Add(repo, "TestResults", ".NET test results", "Test artifacts", 0.2);
            Add(repo, ".vs", "Visual Studio workspace data", "Editor workspace data", 0.5);
            Add(repo, "dists", "Gradle wrapper downloads", "Downloaded tooling", 0.8);
            Add(repo, "uv-cache", "uv cache", "Package caches", 0.3);
        }
        var scan = builder.Build(@"G:\Github", "Illustrative cleanup categories", TimeSpan.Zero, 0, default);
        var locations = items.Select(item => OpportunityGroupingTests.Finding(scan, item.Index, item.Category) with { Title = item.Title }).ToArray();
        var groups = OpportunityGrouping.Build(scan, locations);
        var load = window.InitializeExplorerAsync(scan, locations, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        var publish = typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex = 0;
        publish.Invoke(window, [scan, locations, groups, null, null, false]);
        ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var cards = (ItemsControl)window.FindName("CleanupGroupCards");
        var list = (ListBox)window.FindName("OpportunityGrid");
        var navigation = (ListBox)window.FindName("OpportunityGroupList");
        var scope = (TextBlock)window.FindName("CleanupScopeText");
        Assert.Equal(6, cards.Items.Count); Assert.Empty(list.Items);
        Assert.Contains(cards.Items.Cast<OpportunityGroupRow>(), row => row.Name == "Downloaded tooling");
        Assert.Contains(cards.Items.Cast<OpportunityGroupRow>(), row => row.Name == "Editor workspace data");
        window.UpdateLayout();
        foreach (string name in new[] { "Tool caches", "Test results & reports" })
        {
            var root = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == name);
            Assert.Equal(2, root.Children.Count); Assert.Equal("By tool and project", root.BreakdownHeading);
            var container = cards.ItemContainerGenerator.ContainerFromItem(root);
            Assert.False(Assert.Single(Descendants(container).OfType<Expander>()).IsExpanded);
        }
        ((TextBlock)window.FindName("StatusText")).Text = "Illustrative inventory · Additional cleanup categories";
        ((TextBlock)window.FindName("ScannedSizeText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("ScannedSizeCaption")).Text = "On disk";
        ((TextBlock)window.FindName("CandidateText")).Text = SizeText.Format(CleanupPlanner.Create(scan, locations).EstimatedBytes);
        ((TextBlock)window.FindName("CandidateCaption")).Text = "Estimate · 6 groups";
        // Every breakdown starts collapsed so the top-level checklist remains short.
        var build = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == "Build files");
        Assert.Equal(5, build.Children.Count);
        Assert.False(Assert.Single(Descendants(cards.ItemContainerGenerator.ContainerFromItem(build)).OfType<Expander>()).IsExpanded);
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-categories-{(mode == ThemeMode.Light ? "light" : "dark")}.png"), 1220, 910);
        }
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-categories-compact-dark.png"), 1020, 790);
#pragma warning restore WPF0001
        foreach (var (name, tool) in new[] { ("Tool caches", "pytest"), ("Test results & reports", "Playwright") })
        {
            window.UpdateLayout();
            var root = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == name);
            var child = root.Children.Single(row => row.Name == tool);
            var container = cards.ItemContainerGenerator.ContainerFromItem(root);
            Assert.Single(Descendants(container).OfType<Expander>()).IsExpanded = true; window.UpdateLayout();
            Descendants(container).OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, child)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(child.Checked); Assert.Null(root.Checked);
            Descendants(container).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, child)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(name + " › " + tool, scope.Text); Assert.Equal(2, navigation.Items.Count);
            int perProject = tool == "Playwright" ? 2 : 1;
            Assert.Equal(perProject, list.Items.Count); Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
            var selected = child.Matches.Select(row => row.SelectionIdentity).ToHashSet();
            string projectPath = ((OpportunityGroupRow)navigation.SelectedItem).Path;
            var remaining = locations.Where(item => !selected.Contains((item.ProviderId, item.Path, item.RuleId)) || !item.Path.StartsWith(projectPath + "\\", StringComparison.Ordinal)).ToArray();
            publish.Invoke(window, [scan, remaining, OpportunityGrouping.Build(scan, remaining), selected, null, true]);
            Assert.Empty(list.Items); Assert.Single(navigation.Items.Cast<object>());
            Assert.Contains("No matching folders remain", ((TextBlock)window.FindName("OpportunityResultSummary")).Text);
            publish.Invoke(window, [scan, locations, groups, selected, null, true]);
            Assert.Equal(perProject, list.Items.Count);
            ((Button)window.FindName("AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(perProject * 2, list.Items.Count);
            if (tool == "Playwright") Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-test-projects-dark.png"), 1220, 910);
            ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex = 1;

        int Folder(ulong id, ulong parent, string name)
        { int file = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(file, new(parent), name); return file; }
        void Add(ulong parent, string name, string title, string category, double gib)
        {
            ulong id = next++; int folder = Folder(id, parent, name);
            int data = builder.AddFile(new(new(next++), FileAttributes.Normal, GiB(gib), GiB(gib), 0)); builder.AddLink(data, new(id), "data");
            items.Add((folder, title, category));
        }
    }

    private static void CheckOpportunityValidation(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "sample.csproj"), "<Project />");
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "bin")).FullName;
        string obj = Directory.CreateDirectory(Path.Join(fixture.Path, "obj")).FullName;
        string other = Directory.CreateDirectory(Path.Join(fixture.Path, "other")).FullName;
        string output = Path.Join(bin, "sample.dll"), removed = Path.Join(obj, "sample.o");
        File.WriteAllBytes(output, new byte[4096]); File.WriteAllBytes(removed, new byte[8192]);
        File.WriteAllBytes(Path.Join(other, "old.dat"), new byte[512]);
        BuildActivityFixture.AgeProject(fixture.Path);
        var scanTask = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanTask.IsCompleted, TimeSpan.FromSeconds(5));
        var scan = scanTask.GetAwaiter().GetResult();
        Publish(scan);
        var list = (ListBox)window.FindName("OpportunityGrid");
        Assert.Equal(2, list.Items.Count);
        foreach (var row in list.Items.Cast<OpportunityRow>()) row.Selected = true;
        File.WriteAllText(window.CleanupSettingsPath, "{invalid");
        window.LoadBuildCleanupSettings();
        var blockedReview = window.ReviewSelectionAsync();
        Assert.True(blockedReview.IsCompletedSuccessfully);
        Assert.DoesNotContain(app.Windows.OfType<ReviewWindow>(), review => review.IsVisible);
        Assert.Equal("{invalid", File.ReadAllText(window.CleanupSettingsPath));
        ((TextBox)window.FindName("BuildKeepRecentDaysBox")).Text = "3";
        Assert.True(window.SaveBuildRetention());
        ((ToggleButton)window.FindName("ViewOptionsToggle")).IsChecked = false;
        var search = (TextBox)window.FindName("OpportunitySearch");
        search.Text = "bin";
        PumpUntil(() => list.Items.Count == 1, TimeSpan.FromSeconds(3));
        File.WriteAllBytes(output, new byte[16384]);
        File.Delete(removed); Directory.Delete(obj);
        File.WriteAllBytes(Path.Join(other, "old.dat"), new byte[20000]);
        BuildActivityFixture.AgeProject(fixture.Path);
        window.MarkScanStale("Unrelated fixture data is out of date.", [new(other)]);
        Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
        Assert.True(((Button)window.FindName("SelectListedButton")).IsEnabled);
        ReviewAndAssert(16384, "1 location was excluded", "review-validated-stale.png", logical: false);
        Assert.Equal("bin", search.Text);
        Assert.Equal("1 location selected", ((TextBlock)window.FindName("SelectionText")).Text);
        Assert.Equal("16 KiB", Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>())).Size);
        var refreshed = Assert.IsType<ExplorerRows>(((ListBox)window.FindName("SpaceTree")).ItemsSource).Scan;
        Assert.Null(refreshed.FindEntry(obj));
        Assert.Equal(512, refreshed.Entry(refreshed.FindEntry(other)!.Value).LogicalBytes);
        Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("CleanupFreshnessText")).Visibility);
        Assert.Contains("review-validation-complete", File.ReadAllText(Assert.IsType<string>(((Button)window.FindName("OpenLogButton")).ToolTip)));

        // Logical-only scans also get fresh review estimates, without replacing
        // the global inventory with a sparse subset or claiming native identity.
        var basicBuilder = new ScanBuilder { AllocationData = false };
        foreach (var record in refreshed.Records) basicBuilder.AddFile(record with { AllocatedBytes = 0 });
        basicBuilder.RootIndex = refreshed.RootIndex;
        for (int i = 0; i < refreshed.Links.Length; i++)
            basicBuilder.AddLink(refreshed.Links[i].FileIndex, refreshed.Links[i].ParentIdentity, refreshed.Name(i));
        var basic = basicBuilder.Build(refreshed.RootPath, "Logical-only fixture", TimeSpan.Zero, 0, default);
        Publish(basic);
        Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>())).Selected = true;
        File.WriteAllBytes(output, new byte[32768]);
        BuildActivityFixture.AgeProject(fixture.Path);
        window.MarkScanStale("Logical-only fixture data is out of date.");
        ReviewAndAssert(32768, "Other folders retain", "review-validated-logical.png", logical: true);
        Assert.Same(basic, Assert.IsType<ExplorerRows>(((ListBox)window.FindName("SpaceTree")).ItemsSource).Scan);
        Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("CleanupFreshnessText")).Visibility);
        Assert.Equal(32768, new FileInfo(output).Length);
        Assert.True(File.Exists(Path.Join(fixture.Path, "sample.csproj")));

        Directory.CreateDirectory(obj); File.WriteAllBytes(removed, new byte[8192]);
        var lateScanTask = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => lateScanTask.IsCompleted, TimeSpan.FromSeconds(5));
        var lateScan = lateScanTask.GetAwaiter().GetResult(); Publish(lateScan);
        search.Text = ""; PumpUntil(() => list.Items.Count == 2, TimeSpan.FromSeconds(3));
        foreach (var row in list.Items.Cast<OpportunityRow>()) row.Selected = true;
        string retained = Path.Join(fixture.Path, "retained-bin"); Directory.Move(bin, retained);
        Directory.CreateDirectory(bin); File.WriteAllBytes(output, new byte[16384]);
        bool cancelledAtPublication = false, unexpectedPreview = false;
        var itemsSource = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListBox))!;
        EventHandler cancelAtPublication = (_, _) =>
        {
            var current = ((ListBox)window.FindName("SpaceTree")).ItemsSource as ExplorerRows;
            if (cancelledAtPublication || current is null || ReferenceEquals(current.Scan, lateScan)) return;
            cancelledAtPublication = true;
            ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        var closeUnexpected = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            var dialog = app.Windows.OfType<ReviewWindow>().FirstOrDefault(candidate => candidate.IsVisible);
            if (dialog is null) return;
            unexpectedPreview = true; dialog.Close();
        }, Dispatcher.CurrentDispatcher);
        itemsSource.AddValueChanged(list, cancelAtPublication);
        try
        {
            var cancelledReview = window.ReviewSelectionAsync();
            PumpUntil(() => cancelledReview.IsCompleted, TimeSpan.FromSeconds(10)); cancelledReview.GetAwaiter().GetResult();
            Assert.True(cancelledAtPublication); Assert.False(unexpectedPreview);
            Assert.Contains("Review cancelled", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.False(list.Items.Cast<OpportunityRow>().Single(row => row.Path == bin).Selected);
            Assert.True(list.Items.Cast<OpportunityRow>().Single(row => row.Path == obj).Selected);
        }
        finally { itemsSource.RemoveValueChanged(list, cancelAtPublication); closeUnexpected.Stop(); }
        var lateUpdated = Assert.IsType<ExplorerRows>(((ListBox)window.FindName("SpaceTree")).ItemsSource).Scan;
        int retainedLink = lateUpdated.FindEntry(retained)!.Value;
        var retainedRow = new ExplorerRow(lateUpdated, lateUpdated.Entry(retainedLink), 1, false);
        Assert.Equal("Not checked", retainedRow.Allocated); Assert.Equal("Not checked", retainedRow.Logical);
        Assert.False(retainedRow.CanExpand); Assert.True(retainedRow.Entry.IsIncomplete);
        var showRetained = window.NavigateToEntryAsync(retainedLink);
        PumpUntil(() => showRetained.IsCompleted, TimeSpan.FromSeconds(5)); showRetained.GetAwaiter().GetResult();
        Assert.Contains("Folder contents were not checked", ((TextBlock)window.FindName("MapHint")).Text);
        Assert.Contains("Folder contents were not checked", ((TextBlock)window.FindName("MapHint")).Text);
        Assert.Equal(32768, new FileInfo(Path.Join(retained, "sample.dll")).Length);

        void Publish(ScanSnapshot snapshot)
        {
            var findings = OpportunityDetector.Find(snapshot);
            var load = window.InitializeExplorerAsync(snapshot, findings, default);
            PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
            typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [snapshot, findings, OpportunityGrouping.Build(snapshot, findings), null, null, false]);
            ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        }
        void ReviewAndAssert(long expectedBytes, string note, string render, bool logical)
        {
            Exception? failure = null;
            bool reviewed = false;
            var closeReview = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
            {
                var dialog = app.Windows.OfType<ReviewWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (dialog is null) return;
                try
                {
                    reviewed = true;
                    var location = Assert.IsType<CleanupReviewRow>(Assert.Single(((ListBox)dialog.FindName("LocationList")).Items.Cast<object>()));
                    Assert.Equal(bin, location.Path); Assert.Equal(expectedBytes, location.Location.LogicalBytes);
                    Assert.Contains(note, ((TextBlock)dialog.FindName("ValidationText")).Text);
                    Assert.Equal(logical, ((TextBlock)dialog.FindName("EstimateCaption")).Text.Contains("Logical size", StringComparison.Ordinal));
                    Assert.True(((Button)dialog.FindName("CleanupButton")).IsEnabled);
                    Assert.True(((Button)dialog.FindName("CloseButton")).IsDefault);
                    Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", render), 860, 740);
                }
                catch (Exception ex) { failure = ex; }
                finally { dialog.Close(); }
            }, Dispatcher.CurrentDispatcher);
            try
            {
                var review = window.ReviewSelectionAsync();
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("CancelButton")).Visibility);
                PumpUntil(() => review.IsCompleted, TimeSpan.FromSeconds(10)); review.GetAwaiter().GetResult();
                if (failure is not null) throw failure;
                Assert.True(reviewed, ((TextBlock)window.FindName("StatusText")).Text);
                Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
            }
            finally { closeReview.Stop(); }
        }
    }

    private static void CheckOpportunityCleanup(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string source = Path.Join(fixture.Path, "sample.csproj"); File.WriteAllText(source, "<Project />");
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "bin")).FullName;
        string obj = Directory.CreateDirectory(Path.Join(fixture.Path, "obj")).FullName;
        File.WriteAllText(Path.Join(bin, "sample.dll"), "rebuildable fixture");
        File.WriteAllText(Path.Join(obj, "sample.o"), "rebuildable fixture");
        BuildActivityFixture.AgeProject(fixture.Path);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(5)); var scan = scanning.GetAwaiter().GetResult();
        var findings = OpportunityDetector.Find(scan);
        var load = window.InitializeExplorerAsync(scan, findings, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, findings, OpportunityGrouping.Build(scan, findings), null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        var list = (ListBox)window.FindName("OpportunityGrid");
        foreach (var row in list.Items.Cast<OpportunityRow>()) row.Selected = true;
        Exception? failure = null; CleanupBatchResult? result = null;
        bool kept = false, confirmed = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            var dialog = app.Windows.OfType<ReviewWindow>().FirstOrDefault(candidate => candidate.IsVisible);
            if (dialog is null) return;
            try
            {
                if (!kept)
                {
                    kept = true;
                    Assert.True(((Button)dialog.FindName("CloseButton")).IsDefault);
                    Assert.True(((Button)dialog.FindName("CleanupButton")).IsEnabled);
                    Assert.Equal(new[] { bin, obj }.Order(), ((ListBox)dialog.FindName("LocationList")).Items.Cast<CleanupReviewRow>().Select(row => row.Path).Order());
                    ((Button)dialog.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (!confirmed)
                {
                    confirmed = true;
                    Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-confirmation.png"), 860, 740);
                    ((Button)dialog.FindName("CleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(((Button)dialog.FindName("CleanupButton")).IsEnabled);
                    Assert.Equal("Cancel cleanup", ((Button)dialog.FindName("CloseButton")).Content);
                }
                else if (dialog.Result is { } completed)
                {
                    result = completed;
                    Assert.Equal(2, result.RecycledCount); Assert.False(result.Cancelled);
                    Assert.All(result.Locations, item => Assert.Equal(CleanupOutcome.Recycled, item.Outcome));
                    Assert.Contains("Affected folders refreshed", ((TextBlock)dialog.FindName("ResultDetailText")).Text);
                    Assert.Contains("Space stays occupied", ((TextBlock)dialog.FindName("ResultText")).Text);
                    Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-results.png"), 860, 740);
                    dialog.Close();
                }
            }
            catch (Exception error) { failure = error; result ??= dialog.Result; dialog.Close(); }
        };
        timer.Start();
        try
        {
            var keep = window.ReviewSelectionAsync(); PumpUntil(() => keep.IsCompleted, TimeSpan.FromSeconds(10)); keep.GetAwaiter().GetResult();
            if (failure is not null) throw failure;
            Assert.True(kept); Assert.True(Directory.Exists(bin)); Assert.True(Directory.Exists(obj));
            var clean = window.ReviewSelectionAsync(); PumpUntil(() => clean.IsCompleted, TimeSpan.FromSeconds(15)); clean.GetAwaiter().GetResult();
            if (failure is not null) throw failure;
            Assert.True(confirmed); Assert.NotNull(result);
            Assert.False(Directory.Exists(bin)); Assert.False(Directory.Exists(obj)); Assert.Equal("<Project />", File.ReadAllText(source));
            var updated = Assert.IsType<ExplorerRows>(((ListBox)window.FindName("SpaceTree")).ItemsSource).Scan;
            Assert.Null(updated.FindEntry(bin)); Assert.Null(updated.FindEntry(obj)); Assert.NotNull(updated.FindEntry(source));
            Assert.Empty(list.Items); Assert.Equal("0 B", ((TextBlock)window.FindName("CandidateText")).Text);
            string log = File.ReadAllText(Assert.IsType<string>(((Button)window.FindName("OpenLogButton")).ToolTip));
            Assert.Contains("cleanup-confirmed", log); Assert.Contains("cleanup-location-result", log); Assert.Contains("cleanup-complete", log);
        }
        finally
        {
            timer.Stop();
            if (result is not null)
                foreach (var item in result.Locations)
                    if (item.Recycle?.RecycledPath is { } destination) DeletionTests.RestoreFixture(destination, item.Location.Path);
        }
    }

    private static void CheckBulkCleanupReview(Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        var recentFiles = new List<string>();
        for (int index = 0; index < 1000; index++)
        {
            string project = Directory.CreateDirectory(Path.Join(fixture.Path, $"project-{index:0000}")).FullName;
            bool cargo = index is >= 700 and < 800, generic = index is >= 800 and < 900;
            File.WriteAllText(Path.Join(project, cargo ? "Cargo.toml" : generic ? "package.json" : "app.csproj"), "owned fixture");
            string output = Directory.CreateDirectory(Path.Join(project, cargo ? "target" : generic ? "dist" : "bin")).FullName;
            string file = Path.Join(output, cargo ? "result.rlib" : generic ? "result.js" : "result.dll");
            File.WriteAllBytes(file, new byte[1024]);
            if (cargo) File.WriteAllText(Path.Join(output, ".rustc_info.json"), "owned fixture");
            if (index >= 900) recentFiles.Add(file);
        }
        BuildActivityFixture.AgeProject(fixture.Path);
        foreach (string file in recentFiles) File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(10)); var scan = scanning.GetAwaiter().GetResult();
        var findings = DeveloperOpportunityFixture.Find(scan);
        Assert.Equal(1000, findings.Count);
        var plan = CleanupPlanner.Prepare(scan, findings, discover: DeveloperOpportunityFixture.Find);
        Assert.Equal(900, plan.Targets.Count); Assert.Equal(100, plan.BlockedLocations.Count);
        Assert.Equal(800, plan.BuildAssessments.Values.Count(item => item.State == CleanupReadiness.Ready));
        Assert.Equal(100, plan.BuildAssessments.Values.Count(item => item.State == CleanupReadiness.NeedsReview));
        var groups = findings.GroupBy(item => item.Title).Select(group => new OpportunityGroup(group.Key, group.Key,
            fixture.Path, "Build files", group.ToArray(), group.Sum(item => item.AllocatedBytes))).ToArray();
        CleanupPlan? confirmed = null;
        var dialog = new ReviewWindow(plan, "Owned illustrative inventory · No files will move.", (chosen, _, _) =>
        {
            confirmed = chosen;
            return Task.FromResult(new CleanupBatchResult(chosen.Preview.Locations.Select(item =>
                new CleanupLocationResult(item, CleanupOutcome.NotProcessed, "Fixture preview only")).ToArray(), null));
        }, groups);
        dialog.ShowActivated = false; dialog.ShowInTaskbar = false; dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000; dialog.Top = -10000; dialog.Show();
        try
        {
            var locations = (ListBox)dialog.FindName("LocationList");
            var navigation = (ListBox)dialog.FindName("GroupList");
            var search = (TextBox)dialog.FindName("SearchBox");
            var cleanup = (Button)dialog.FindName("CleanupButton");
            var estimate = (TextBlock)dialog.FindName("EstimateCaption");
            var all = navigation.Items.Cast<CleanupReviewGroup>().Single(group => group.Key == "all");
            var rows = all.Rows;
            Assert.Equal(1000, rows.Count); Assert.Equal(800, rows.Count(row => row.Selected));
            Assert.All(rows.Where(row => row.Assessment.State == CleanupReadiness.NeedsReview), row => Assert.False(row.Selected));
            Assert.All(rows.Where(row => row.Assessment.State == CleanupReadiness.Protected), row => Assert.False(row.CanSelect));
            Assert.Equal(100, locations.Items.Count);
            Assert.Contains("of 800", ((TextBlock)dialog.FindName("PageText")).Text);
            ((Button)dialog.FindName("NextPage")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.StartsWith("101–200", ((TextBlock)dialog.FindName("PageText")).Text);
            Assert.Equal(100, locations.Items.Count);
            search.Text = "project-0012";
            var excluded = Assert.IsType<CleanupReviewRow>(Assert.Single(locations.Items.Cast<object>()));
            excluded.Selected = false;
            PumpUntil(() => cleanup.IsEnabled && estimate.Text.Contains("799 folders", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            search.Text = "";
            Assert.False(rows.Single(row => row.Path.Contains("project-0012", StringComparison.Ordinal)).Selected);
            var cargo = navigation.Items.Cast<CleanupReviewGroup>().Single(group => group.Name == "Cargo build output");
            navigation.SelectedItem = cargo; cargo.Checked = false;
            PumpUntil(() => cleanup.IsEnabled && estimate.Text.Contains("699 folders", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            ((RadioButton)dialog.FindName("ManualFilter")).IsChecked = true;
            var manualGroup = navigation.Items.Cast<CleanupReviewGroup>().Single(group => group.Name == "Possible Node build output");
            navigation.SelectedItem = manualGroup; Assert.Equal(100, locations.Items.Count);
            Assert.All(locations.Items.Cast<CleanupReviewRow>(), row => Assert.False(row.Selected));
            manualGroup.Checked = true;
            PumpUntil(() => cleanup.IsEnabled && estimate.Text.Contains("799 folders", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            ((RadioButton)dialog.FindName("KeptFilter")).IsChecked = true;
            Assert.Equal(100, locations.Items.Count);
            Assert.All(locations.Items.Cast<CleanupReviewRow>(), row => { row.Selected = true; Assert.False(row.Selected); Assert.False(row.CanSelect); });
            ((RadioButton)dialog.FindName("ReadyFilter")).IsChecked = true;
            navigation.SelectedItem = navigation.Items.Cast<CleanupReviewGroup>().Single(group => group.Key == "all");
            locations.SelectedIndex = 0;
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme;
                string suffix = theme == ThemeMode.Light ? "light" : "dark";
                Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", $"bulk-cleanup-review-{suffix}.png"), 980, 720);
                Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", $"bulk-cleanup-review-compact-{suffix}.png"), 760, 520);
            }
#pragma warning restore WPF0001
            Assert.InRange(Descendants(locations).OfType<ListBoxItem>().Count(), 1, 30);
            cleanup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => dialog.Result is not null, TimeSpan.FromSeconds(5));
            Assert.NotNull(confirmed); Assert.Equal(799, confirmed.Preview.Locations.Count);
            Assert.Equal(plan.BuildPolicy, confirmed.BuildPolicy);
            Assert.DoesNotContain(confirmed.Preview.Locations, item => item.Path.Contains("project-0012", StringComparison.Ordinal));
            Assert.DoesNotContain(confirmed.Preview.Locations, item => item.Title == "Cargo build output");
            Assert.DoesNotContain(confirmed.Preview.Locations, item => recentFiles.Any(file => file.StartsWith(item.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
            Assert.All(confirmed.Targets, target => Assert.Same(plan.Targets.Single(item => item.Path == target.Path), target));
            Assert.All(findings, finding => Assert.True(Directory.Exists(finding.Path)));
        }
        finally { dialog.Close(); }
    }

    private static void AssertMapFillsPane(MainWindow window, TreemapPanel map)
    {
        var layout = (Grid)window.FindName("MapLayout");
        var legend = layout.Children.OfType<WrapPanel>().Single();
        double gap = legend.TranslatePoint(new Point(), layout).Y - map.TranslatePoint(new Point(0, map.ActualHeight), layout).Y;
        Assert.InRange(gap, 0, 12); // A small legend gutter, with no unused map space.
    }

    private static void AssertTilesContained(TreemapPanel map)
    {
        var cells = TreemapLayout.Arrange(map.Weights, map.RenderSize);
        for (int i = 0; i < map.Children.Count; i++)
        {
            var tile = (FrameworkElement)map.Children[i];
            var bounds = tile.TransformToAncestor(map).TransformBounds(new Rect(tile.RenderSize));
            var cell = cells[i];
            Assert.True(bounds.Left >= -.5 && bounds.Top >= -.5
                && bounds.Right <= map.ActualWidth + .5 && bounds.Bottom <= map.ActualHeight + .5,
                $"A map tile extends outside its pane: {bounds}, pane {map.RenderSize}.");
            Assert.True(bounds.Left >= cell.Left - .5 && bounds.Top >= cell.Top - .5
                && bounds.Right <= cell.Right + .5 && bounds.Bottom <= cell.Bottom + .5,
                $"A map tile overlaps a sibling cell: {bounds}, cell {cell}.");
            if (tile is Button button)
            {
                var surface = (Border)button.Template.FindName("TileSurface", button);
                Assert.True(surface.ActualWidth <= button.ActualWidth + .5 && surface.ActualHeight <= button.ActualHeight + .5,
                    "A tile surface is cropped instead of fitting its proportional cell.");
            }
            if (tile is Border { Child: Grid grid, Tag: TreemapPanel nested } && nested.IsVisible)
            {
                var nestedBounds = nested.TransformToAncestor(tile).TransformBounds(new Rect(nested.RenderSize));
                Assert.True(nestedBounds.Left >= -.5 && nestedBounds.Top >= -.5
                    && nestedBounds.Right <= tile.ActualWidth + .5 && nestedBounds.Bottom <= tile.ActualHeight + .5,
                    "A child map extends outside its folder frame.");
                var header = (Button)grid.Children[0];
                Assert.True(header.TranslatePoint(new Point(0, header.ActualHeight), tile).Y <= nestedBounds.Top + .5,
                    "A folder header overlaps its child map.");
                AssertTilesContained(nested);
            }
        }
    }

    private static ScanSnapshot CrowdedMapFixture()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        Folder(2, "Games"); Folder(3, "Cloud"); Folder(4, "Tools");
        File(20, 2, "Call of Duty- Black Ops Cold War", 220.99);
        File(21, 2, "World of Warcraft", 125.56);
        File(22, 2, "Indiana Jones and the Great Circle", 86.7);
        File(23, 2, "The Alters", 63.84); File(24, 2, "Warcraft III", 32.92);
        File(25, 2, "Voidtrain", 18); File(26, 2, "More games", 10.4);
        File(30, 3, "Proton", 230.76); File(31, 3, "OneDrive", 81.64);
        File(40, 4, "tools.bin", 5); File(50, 1, "Other entries", 1.62);
        return builder.Build(@"G:\", "Layout fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);

        void Folder(ulong id, string name)
        {
            int index = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(index, new(1), name);
        }
        void File(ulong id, ulong parent, string name, double size)
        {
            int index = builder.AddFile(new(new(id), FileAttributes.Normal, GiB(size), GiB(size), 0));
            builder.AddLink(index, new(parent), name);
        }
    }

    private static ScanSnapshot NestedLayoutFixture(bool smallEntries = false)
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        Folder(2, 1, "Github"); Folder(3, 1, "Software"); Folder(4, 1, "Other data");
        Folder(20, 2, "Project A"); Folder(21, 2, "Project B"); Folder(22, 2, "Project C");
        File(200, 20, "data.bin", smallEntries ? 495.2 : 500.2, smallEntries ? 220.4 : 225.4);
        File(210, 21, "data.bin", 253.2, 103.9);
        File(220, 22, "data.bin", 84.2, 43.34);
        if (smallEntries)
            for (ulong id = 500; id < 550; id++) File(id, 2, $"small-{id}.bin", .1, .1);
        Folder(30, 3, "SDKs"); Folder(31, 3, "Applications");
        File(300, 30, "data.bin", 45.2, 45.2);
        File(310, 31, "data.bin", 22.5, 22.5);
        File(400, 4, "other.bin", 9.09, 6.93);
        return builder.Build(@"G:\", "Layout fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);

        void Folder(ulong id, ulong parent, string name)
        {
            int index = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(index, new(parent), name);
        }
        void File(ulong id, ulong parent, string name, double logical, double allocated)
        {
            int index = builder.AddFile(new(new(id), FileAttributes.Normal, GiB(logical), GiB(allocated), 0));
            builder.AddLink(index, new(parent), name);
        }
    }

    private static void CheckTreeColumns(MainWindow window, ListBox tree)
    {
        var columns = (GridView)window.FindName("TreeColumns");
        Assert.Equal(new[] { "Name", "Allocated", "Modified" }, columns.Columns.Select(column => ((GridViewColumnHeader)column.Header).Tag));
        var rows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
        var selected = tree.SelectedItem;
        var expansion = rows.ExpandedPaths();
        var button = (Button)window.FindName("ColumnsButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var menu = button.ContextMenu;
        Assert.True(menu.IsOpen);
        Assert.False(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, ScanSortColumn.Name)).IsEnabled);
        foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.Tag is ScanSortColumn && !item.IsChecked))
        { item.IsChecked = true; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        Assert.Equal(9, columns.Columns.Count);
        Assert.Same(selected, tree.SelectedItem); Assert.Equal(expansion, rows.ExpandedPaths());
        RenderMenu(menu, Path.Join(FindRepository(), "artifacts", "ui", "tree-column-menu.png"));
        menu.IsOpen = false;
        window.UpdateLayout();
        var dateColumn = (GridViewColumn)window.FindName("ModifiedColumn");
        var header = (GridViewColumnHeader)dateColumn.Header;
        Assert.True(header.Focusable); Assert.True(header.IsTabStop);
        foreach (var key in new[] { Key.Left, Key.Right })
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(header), 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = header };
            tree.RaiseEvent(input);
            Assert.False(input.Handled); Assert.Equal(expansion, rows.ExpandedPaths());
        }
        var thumb = Assert.Single(Descendants(header).OfType<Thumb>());
        // Header and gripper double-clicks never toggle the selected folder.
        foreach (var target in new UIElement[] { header, thumb })
        {
            tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Control.MouseDoubleClickEvent, Source = target });
            Assert.Equal(expansion, rows.ExpandedPaths()); Assert.Same(selected, tree.SelectedItem);
        }
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(27, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(27, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert.Equal(177, dateColumn.Width);
        columns.Columns.Move(columns.Columns.IndexOf(dateColumn), 1);
        Render(window, Path.Join(FindRepository(), "artifacts", "ui", "tree-columns-all.png"), 1220, 910);
        var viewer = Descendants(tree).OfType<ScrollViewer>().First();
        viewer.ScrollToHorizontalOffset(190); window.UpdateLayout();
        PumpUntil(() => true, TimeSpan.FromSeconds(1));
        Assert.True(viewer.HorizontalOffset > 0, $"Horizontal scrolling: extent {viewer.ExtentWidth}, viewport {viewer.ViewportWidth}, scrollable {viewer.ScrollableWidth}, actual {viewer.ActualWidth}, bar {viewer.HorizontalScrollBarVisibility}, columns {columns.Columns.Sum(column => column.ActualWidth)}");
        Assert.True(Descendants(tree).OfType<ListBoxItem>().Count() <= 100);
        window.SaveColumnLayout();
        var saved = TreeColumnLayout.Load(window.ColumnLayoutPath);
        Assert.Equal(ScanSortColumn.Modified, saved.Columns[1].Id); Assert.Equal(177, saved.Columns[1].Width);
        Assert.All(saved.Columns, state => Assert.True(state.Visible));
        ResetTreeColumns(window);
        PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        typeof(MainWindow).GetMethod("LoadColumnLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Assert.Equal(9, columns.Columns.Count); Assert.Same(dateColumn, columns.Columns[1]); Assert.Equal(177, dateColumn.Width);
        viewer.ScrollToHorizontalOffset(0); window.UpdateLayout();
        var nameHeader = (GridViewColumnHeader)window.FindName("NameHeader");
        RightClick(window, nameHeader);
        Assert.False(tree.ContextMenu.IsOpen);
        Assert.True(nameHeader.ContextMenu.IsOpen); nameHeader.ContextMenu.IsOpen = false;
    }

    private static void CheckVisibleInitialSort(MainWindow window)
    {
        ResetTreeColumns(window);
        PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        Load(true);
        ClickSort("AllocationHeader");
        var button = (Button)window.FindName("ColumnsButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var allocation = button.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, ScanSortColumn.Allocated));
        allocation.IsChecked = false; allocation.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(button.ContextMenu.IsEnabled); // Another toggle cannot hide the pending sort column.
        button.ContextMenu.IsOpen = false;
        PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        Assert.True(button.ContextMenu.IsEnabled);
        Load(true); Assert.Equal("Name ↑", ((GridViewColumnHeader)window.FindName("NameHeader")).Content);
        ResetTreeColumns(window);
        ClickSort("AllocationHeader");
        Load(false); Assert.Equal("Name ↑", ((GridViewColumnHeader)window.FindName("NameHeader")).Content);
        var columns = (GridView)window.FindName("TreeColumns");
        Assert.Contains(columns.Columns, column => ReferenceEquals(column.Header, window.FindName("NameHeader")));

        void Load(bool allocationData)
        {
            var builder = new ScanBuilder { AllocationData = allocationData };
            builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
            for (ulong i = 2; i <= 3; i++)
            {
                int file = builder.AddFile(new(new(i), FileAttributes.Normal, (long)i, allocationData ? (long)i : 0, 0));
                builder.AddLink(file, new(1), $"file-{i}.bin");
            }
            var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
            var task = window.InitializeExplorerAsync(scan, [], default);
            PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10)); task.GetAwaiter().GetResult();
        }
        void ClickSort(string name)
        {
            ((GridViewColumnHeader)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
        }
    }

    private static void ResetTreeColumns(MainWindow window)
    {
        var button = (Button)window.FindName("ColumnsButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var reset = button.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Reset columns"));
        reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        button.ContextMenu.IsOpen = false;
    }

    private static void PumpUntil(Func<bool> completed, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var timer = Stopwatch.StartNew();
        var pulse = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            if (completed() || timer.Elapsed > timeout) frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame);
        pulse.Stop();
        Assert.True(completed(), "The desktop operation did not complete.");
    }

    // Offscreen HWNDs do not receive normal mouse hit testing from SendMessage.
    // Route mouse/menu events through the real controls, then show the real WPF
    // popup. WPF's menu-event constructors are internal, so only the test uses reflection.
    private static void RightClick(Window window, FrameworkElement target, bool keyboard = false)
    {
        window.UpdateLayout();
        if (!keyboard)
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
        var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [target, true, keyboard ? -1d : 10d, keyboard ? -1d : 10d], null)!;
        target.RaiseEvent(args);
        var menu = target.ContextMenu ?? (ItemsControl.ItemsControlFromItemContainer(target) as FrameworkElement)?.ContextMenu;
        if (!args.Handled && menu is not null)
        {
            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }
    }

    private static void RenderMenu(FrameworkElement menu, string path)
    {
        menu.UpdateLayout();
        Assert.True(menu.ActualWidth > 0 && menu.ActualHeight > 0);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(menu) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, menu.ActualWidth, menu.ActualHeight));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject? parent)
    {
        if (parent is null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "SpaceMaid.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository could not be located.");
    }

    private static void Render(Window window, string path, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        if (window.IsVisible)
        {
            window.Width = width;
            window.Height = Math.Max(window.MinHeight, height + 30);
            window.UpdateLayout();
        }
        else
        {
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
        }
        PumpUntil(() => true, TimeSpan.FromSeconds(1));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var background = window.TryFindResource("WindowBackground") as Brush ?? Brushes.White;
            drawing.DrawRectangle(background, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(content.Margin.Left, content.Margin.Top, width - content.Margin.Left - content.Margin.Right, height - content.Margin.Top - content.Margin.Bottom));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
