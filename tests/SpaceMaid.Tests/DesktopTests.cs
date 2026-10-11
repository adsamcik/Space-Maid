using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
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
                // A helper is one-shot; its absence after a scan does not disable the user's choice.
                window.SetBusy(true); window.SetBusy(false);
                Assert.Equal(Visibility.Collapsed, enableFast.Visibility);
                // Cancellation/failure resets the choice; retry is offered only when idle.
                fastFlag.SetValue(window, false);
                window.SetBusy(true);
                Assert.Equal(availableVisibility, enableFast.Visibility); Assert.False(enableFast.IsEnabled);
                window.SetBusy(false);
                Assert.Equal(availableVisibility, enableFast.Visibility); Assert.Equal(!Elevation.IsAdministrator, enableFast.IsEnabled);
                var picker = (ComboBox)window.FindName("DrivePicker");
                var fixtureDrive = new DriveInfo(fixture.Path);
                picker.ItemsSource = new[] { new DriveChoice(fixture.Path, "Controlled scan fixture", fixtureDrive.TotalSize, fixtureDrive.AvailableFreeSpace) };
                picker.SelectedIndex = 0;
                var scanButton = (Button)window.FindName("ScanButton");
                stage = "scanning fixture through the UI";
                scanButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("OpenLogButton")).Visibility);
                PumpUntil(() => scanButton.IsEnabled, TimeSpan.FromSeconds(15));
                string logFile = Assert.Single(Directory.GetFiles(window.LogDirectory, "*.jsonl"));
                string events = File.ReadAllText(logFile);
                Assert.Contains("backend-selected", events);
                Assert.Contains("app-scan-complete", events);
                Assert.Contains("Standard Windows scan", ((TextBlock)window.FindName("BackendStatusText")).Text);
                var grid = (ListBox)window.FindName("OpportunityGrid");
                Assert.Equal(0, ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex);
                Assert.Equal(Visibility.Visible, ((Border)window.FindName("CleanupOverview")).Visibility);
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
                var tree = (ListBox)window.FindName("SpaceTree");
                var explorer = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                Assert.True(tree.Items.Count >= 2);
                stage = "opening folder operations through routed right-click events";
                int binLink = explorer.Scan.PrimaryLink(row.Item.DirectoryIndex);
                int projectLink = explorer.Scan.PrimaryLink(explorer.Scan.Entry(binLink).ParentIndex);
                var projectContainer = Assert.IsType<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(explorer.Find(projectLink)));
                stage = "checking expansion hit targets beyond the arrow glyph";
                window.UpdateLayout();
                var projectExpander = Descendants(projectContainer).OfType<Button>().Single();
                Assert.True(projectExpander.ActualWidth >= 40 && projectExpander.ActualHeight >= 34);
                var expansionTargets = Descendants(projectExpander).Append(projectExpander).ToHashSet();
                foreach (var point in new[] { new Point(2, 2), new Point(projectExpander.ActualWidth - 2, 2),
                    new Point(2, projectExpander.ActualHeight - 2), new Point(projectExpander.ActualWidth - 2, projectExpander.ActualHeight - 2) })
                    Assert.Contains(Assert.IsAssignableFrom<DependencyObject>(tree.InputHitTest(projectExpander.TranslatePoint(point, tree))), expansionTargets);
                stage = "opening folder operations through routed right-click events";
                string rootMapTotal = ((TextBlock)window.FindName("MapTotal")).Text;
                RightClick(window, projectContainer);
                Assert.Equal(project, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(project, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                Assert.Equal(rootMapTotal, ((TextBlock)window.FindName("MapTotal")).Text);
                PumpUntil(() => tree.ContextMenu.Items.Cast<MenuItem>().All(i => !Equals(i.Header, "Checking cloud availability…")), TimeSpan.FromSeconds(3));
                Assert.Equal(new[] { "Open in File Explorer", "Move to Recycle Bin", "Recheck this folder" }, tree.ContextMenu.Items.Cast<MenuItem>().Select(i => i.Header));
                Assert.True(tree.ContextMenu.Items.Cast<MenuItem>().All(i => i.IsEnabled));
                var capturedRecycle = (MenuItem)tree.ContextMenu.Items[1];
                tree.ContextMenu.IsOpen = false;
                var rootMap = (TreemapPanel)window.FindName("SpaceMap");
                var projectTile = rootMap.Buttons.Single(b => ((SpaceContribution)b.DataContext).Path == project);
                RightClick(window, projectTile);
                PumpUntil(() => projectTile.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(rootMapTotal, ((TextBlock)window.FindName("MapTotal")).Text);
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
                string projectMapTotal = ((TextBlock)window.FindName("MapTotal")).Text;
                RightClick(window, binTile);
                PumpUntil(() => binTile.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(bin, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                Assert.True(explorer.IsExpanded(projectLink));
                Assert.Same(binTile, rootMap.Buttons.Single(b => ((SpaceContribution)b.DataContext).Path == bin));
                Assert.Equal(projectMapTotal, ((TextBlock)window.FindName("MapTotal")).Text);
                binTile.ContextMenu.IsOpen = false;
                RightClick(window, tree, keyboard: true);
                PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(bin, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                tree.ContextMenu.IsOpen = false;

                stage = "sorting tree columns while preserving expansion, selection and map scope";
                var expandedBefore = tree.Items.Cast<ExplorerRow>().Where(r => r.Expanded).Select(r => r.Entry.LinkIndex).ToArray();
                foreach (string header in new[] { "NameHeader", "NameHeader", "LogicalHeader", "AllocationHeader", "ShareHeader" })
                {
                    ((Button)window.FindName(header)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
                    Assert.Equal(FontWeights.SemiBold, ((Button)window.FindName(header)).FontWeight);
                    Assert.Equal(bin, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                    Assert.All(expandedBefore, link => Assert.True(explorer.IsExpanded(link)));
                    Assert.Equal(projectMapTotal, ((TextBlock)window.FindName("MapTotal")).Text);
                }
                Assert.Equal("% parent ↓", ((Button)window.FindName("ShareHeader")).Content);

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
                var fileContainer = Assert.IsType<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(tree.SelectedIndex));
                Assert.Contains(Descendants(fileContainer).OfType<Grid>(), cell => cell.ToolTip is string tooltip && tooltip.Contains(typeText.Text));
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
                var pathField = Descendants(((StackPanel)window.FindName("EntryDetail"))).OfType<TextBox>().Single();
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
                    Assert.Equal("% parent ↓", ((Button)window.FindName("ShareHeader")).Content);
                    capturedRecycle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.True(scanButton.IsEnabled); // Captured old-scan actions cannot operate on the refreshed inventory.
                }
                finally { DeletionTests.RestoreFixture(Assert.IsType<string>(recycled.RecycledPath), bin); }
                var refreshedExplorer = window.RecheckFolderAsync(project);
                PumpUntil(() => refreshedExplorer.IsCompleted, TimeSpan.FromSeconds(10));
                refreshedExplorer.GetAwaiter().GetResult();
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
                    var nameHeader = (Button)window.FindName("NameHeader");
                    var allocationHeader = (Button)window.FindName("AllocationHeader");
                    var logicalHeader = (Button)window.FindName("LogicalHeader");
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
                    Render(recyclePreview, Path.Join(artifactRoot, "artifacts", "ui", $"recycle-{theme}.png"), 590, 460);
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
                Assert.False(picker.IsEnabled);
                Assert.False(((Button)window.FindName("ReviewButton")).IsEnabled);
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("CancelButton")).Visibility);
                Assert.Equal("Path pending…", Assert.IsType<PreliminaryRow>(preliminaryFiles.Items[0]).PathText);
                Assert.Contains("Inferred type:", Assert.IsType<PreliminaryRow>(preliminaryFiles.Items[0]).EntryTooltip);
                Assert.Contains("PRELIMINARY", ((TextBlock)window.FindName("ScannedSizeLabel")).Text);
                stage = "sorting preliminary rows and retaining the choice across progress updates";
                foreach (string header in new[] { "PreliminaryNameHeader", "PreliminaryNameHeader", "PreliminaryLogicalHeader", "PreliminaryAllocationHeader" })
                    ((Button)window.FindName(header)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
                RightClick(window, Assert.IsType<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(0)));
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
                var finalFile = Assert.IsType<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(wideRows.Count - 1));
                RightClick(window, finalFile);
                PumpUntil(() => tree.ContextMenu.Items.Cast<MenuItem>().All(i => !Equals(i.Header, "Checking cloud availability…")), TimeSpan.FromSeconds(3));
                Assert.False(tree.ContextMenu.IsOpen); // File rows do not expose folder operations.
                Assert.Equal(8192, new FileInfo(Path.Join(bin, "sample.dll")).Length);
                picker.ItemsSource = new[] { new DriveChoice(Path.GetPathRoot(fixture.Path)!, "Another target", fixtureDrive.TotalSize, fixtureDrive.AvailableFreeSpace) };
                picker.SelectedIndex = 0;
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("OpenLogButton")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("BackendStatusText")).Visibility);
                Assert.Empty(tree.ContextMenu.Items.Cast<object>());
                int dialogsBefore = app.Windows.OfType<RecycleFolderWindow>().Count();
                capturedRecycle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.True(scanButton.IsEnabled);
                Assert.Equal(dialogsBefore, app.Windows.OfType<RecycleFolderWindow>().Count()); // An older scan's menu cannot delete a current target.

                stage = "loading the bundled Photos extension into its own assembly context";
                PumpUntil(() => window.ExtensionsReady, TimeSpan.FromSeconds(10));
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
                Assert.Equal(Visibility.Collapsed, ((Grid)photosView.FindName("StatusBand")).Visibility);
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
                Assert.Equal(Visibility.Collapsed, ((Image)photosView.FindName("SelectedPreview")).Visibility);
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
                ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;

                static string PhotoName(object display)
                {
                    var value = display.GetType().GetProperty("Row")!.GetValue(display)!;
                    return (string)value.GetType().GetProperty("Name")!.GetValue(value)!;
                }

                stage = "rendering framed folders at desktop and compact sizes";
                picker.ItemsSource = new[] { new DriveChoice(@"G:\", @"G:\", GiB(465.75), GiB(17.67)) };
                picker.SelectedIndex = 0;
                var layoutScan = NestedLayoutFixture();
                var layoutLoad = window.InitializeExplorerAsync(layoutScan, [], default);
                PumpUntil(() => layoutLoad.IsCompleted, TimeSpan.FromSeconds(10));
                layoutLoad.GetAwaiter().GetResult();
                Assert.Contains(map.Buttons, button => ((SpaceContribution)button.DataContext).Name == "Project A");
                Assert.InRange(map.Buttons.Count(), 1, 64);
                ((TextBlock)window.FindName("ScannedSizeLabel")).Text = "Scanned";
                ((TextBlock)window.FindName("ScannedSizeText")).Text = SizeText.Format(layoutScan.AllocatedBytes);
                ((TextBlock)window.FindName("ScannedSizeCaption")).Text = "Allocated · Shared files counted once";
                ((TextBlock)window.FindName("CandidateText")).Text = "—";
                ((TextBlock)window.FindName("CandidateCaption")).Text = "Not analyzed in this layout fixture";
                ((TextBlock)window.FindName("StatusText")).Text = "Illustrative inventory · Layout validation";
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
                stage = "revealing the parent of a nested grouped tile beneath a collapsed root";
                var groupedLoad = window.InitializeExplorerAsync(NestedLayoutFixture(smallEntries: true), [], default);
                PumpUntil(() => groupedLoad.IsCompleted, TimeSpan.FromSeconds(10));
                groupedLoad.GetAwaiter().GetResult();
                var groupedRows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                window.UpdateLayout();
                var rootContainer = Assert.IsType<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(0));
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
                stage = "browsing thousands of grouped opportunities and retaining hidden selections";
                CheckOpportunityBrowser(window, app, artifactRoot);
                stage = "browsing additional cleanup categories by tool and project";
                CheckAdditionalCleanupCategories(window, app, artifactRoot);
                stage = "validating stale selections before cleanup review";
                CheckOpportunityValidation(window, app, artifactRoot);
                stage = "confirming batch cleanup and refreshing its affected folders";
                CheckOpportunityCleanup(window, app, artifactRoot);
                stage = "closing windows";
                string extensionLog = Assert.Single(Directory.GetFiles(Path.Join(window.LogDirectory, "Extensions"), "*.jsonl"));
                startup.Close(); review.Close(); details.Close(); recyclePreview.Close(); window.Close();
                PumpUntil(() => !app.Windows.Cast<Window>().Contains(window), TimeSpan.FromSeconds(10));
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), $"The WPF smoke test timed out while {stage}.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static long GiB(double value) => (long)(value * 1024 * 1024 * 1024);

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
        ((TextBlock)window.FindName("ScannedSizeCaption")).Text = "Illustrative inventory · Shared files counted once";
        ((TextBlock)window.FindName("CandidateText")).Text = SizeText.Format(CleanupPlanner.Create(scan, locations).EstimatedBytes);
        ((TextBlock)window.FindName("CandidateCaption")).Text = "4 cleanup groups · Illustrative inventory";
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
        var selected = locations.Where(location => location.Path.Contains("Chatty-NPC", StringComparison.Ordinal) || location.Path.Contains("good-shit", StringComparison.Ordinal)).Select(location => location.Path).ToHashSet(StringComparer.Ordinal);
        publish.Invoke(window, [scan, locations, groups, selected, ((OpportunityRow)list.SelectedItem).Path, true]);
        Assert.Equal("Build output", ((TextBlock)window.FindName("OpportunityHeading")).Text);
        Assert.Equal("good-shit", search.Text); Assert.Equal("Build output", filter.SelectedItem);
        Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
        search.Text = "no-such-location";
        PumpUntil(() => list.Items.Count == 0, TimeSpan.FromSeconds(3));
        Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("EmptyState")).Visibility);
        Assert.Contains("40 outside this view", selection.Text);
        // This inventory is illustrative and must never be rechecked on disk.
        var preview = new ReviewWindow(CleanupPlanner.Create(scan, locations.Where(location => selected.Contains(location.Path))));
        Assert.Equal(40, ((ListBox)preview.FindName("LocationList")).Items.Count);
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
        Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("OpportunityFreshnessText")).Visibility);
        Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
        Assert.True(((Button)window.FindName("SelectListedButton")).IsEnabled);
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
            var chosen = csharp.Matches.Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
            publish.Invoke(window, [scan, locations, groups, chosen, ((OpportunityRow)list.SelectedItem).Path, true]);
            Assert.Equal(2, list.Items.Count); Assert.Equal("Chatty-NPC", ((TextBlock)window.FindName("OpportunityHeading")).Text);
            Assert.Equal("Build files › C# / .NET", ((TextBlock)window.FindName("CleanupScopeText")).Text);
            var withoutProject = locations.Where(item => !item.Path.Contains("Chatty-NPC", StringComparison.Ordinal) || !chosen.Contains(item.Path)).ToArray();
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
            var remaining = locations.Where(item => !chosen.Contains(item.Path)).ToArray();
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
            var selectedWorktrees = retained.Keys.ToHashSet(StringComparer.Ordinal);
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
        ((TextBlock)window.FindName("ScannedSizeCaption")).Text = "Illustrative inventory · Shared files counted once";
        ((TextBlock)window.FindName("CandidateText")).Text = SizeText.Format(CleanupPlanner.Create(scan, locations).EstimatedBytes);
        ((TextBlock)window.FindName("CandidateCaption")).Text = "6 cleanup groups · Review required";
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
            var selected = child.Matches.Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
            string projectPath = ((OpportunityGroupRow)navigation.SelectedItem).Path;
            var remaining = locations.Where(item => !selected.Contains(item.Path) || !item.Path.StartsWith(projectPath + "\\", StringComparison.Ordinal)).ToArray();
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
        var scanTask = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanTask.IsCompleted, TimeSpan.FromSeconds(5));
        var scan = scanTask.GetAwaiter().GetResult();
        Publish(scan);
        var list = (ListBox)window.FindName("OpportunityGrid");
        Assert.Equal(2, list.Items.Count);
        foreach (var row in list.Items.Cast<OpportunityRow>()) row.Selected = true;
        var search = (TextBox)window.FindName("OpportunitySearch");
        search.Text = "bin";
        PumpUntil(() => list.Items.Count == 1, TimeSpan.FromSeconds(3));
        File.WriteAllBytes(output, new byte[16384]);
        File.Delete(removed); Directory.Delete(obj);
        File.WriteAllBytes(Path.Join(other, "old.dat"), new byte[20000]);
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
        Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("OpportunityFreshnessText")).Visibility);
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
        window.MarkScanStale("Logical-only fixture data is out of date.");
        ReviewAndAssert(32768, "Other folders retain", "review-validated-logical.png", logical: true);
        Assert.Same(basic, Assert.IsType<ExplorerRows>(((ListBox)window.FindName("SpaceTree")).ItemsSource).Scan);
        Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("OpportunityFreshnessText")).Visibility);
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
        Assert.Equal("Size not checked", ((TextBlock)window.FindName("MapTotal")).Text);
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
                    Assert.Equal(logical, ((TextBlock)dialog.FindName("EstimateCaption")).Text.Contains("physical saving unknown", StringComparison.Ordinal));
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
                    Assert.Contains("Affected folders refreshed", ((TextBlock)dialog.FindName("ResultText")).Text);
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

    private static void AssertMapFillsPane(MainWindow window, TreemapPanel map)
    {
        var layout = (Grid)window.FindName("MapLayout");
        var legend = layout.Children.OfType<WrapPanel>().Single(panel => Grid.GetRow(panel) == 2);
        double gap = legend.TranslatePoint(new Point(), layout).Y - map.TranslatePoint(new Point(0, map.ActualHeight), layout).Y;
        Assert.InRange(gap, 0, 12); // A small legend gutter, with no unused map space.
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

    private static void RenderMenu(ContextMenu menu, string path)
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
