using System.Diagnostics;
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
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                using var fixture = new TestDirectory();
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
                var window = new MainWindow();
                window.LogDirectory = Path.Join(fixture.Path, "diagnostics");
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
                var row = Assert.IsType<OpportunityRow>(Assert.Single(grid.Items.Cast<object>()));
                Assert.Equal(bin, row.Path);
                Assert.Equal("8 KiB", row.Size);
                row.Selected = true;
                Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
                Assert.Equal("1 location selected", ((TextBlock)window.FindName("SelectionText")).Text);
                Assert.Equal(row.Item, ((StackPanel)window.FindName("DetailPanel")).DataContext);

                stage = "navigating the real folder tree and treemap";
                var tree = (ListBox)window.FindName("SpaceTree");
                var explorer = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                Assert.True(tree.Items.Count >= 2);
                stage = "opening folder operations through routed right-click events";
                int binLink = explorer.Scan.PrimaryLink(row.Item.DirectoryIndex);
                int projectLink = explorer.Scan.PrimaryLink(explorer.Scan.Entry(binLink).ParentIndex);
                var projectContainer = Assert.IsType<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(explorer.Find(projectLink)));
                string rootMapTotal = ((TextBlock)window.FindName("MapTotal")).Text;
                RightClick(window, projectContainer);
                Assert.Equal(project, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(project, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                Assert.Equal(rootMapTotal, ((TextBlock)window.FindName("MapTotal")).Text);
                Assert.Equal(new[] { "Open in File Explorer", "Move to Recycle Bin" }, tree.ContextMenu.Items.Cast<MenuItem>().Select(i => i.Header));
                Assert.True(tree.ContextMenu.Items.Cast<MenuItem>().All(i => i.IsEnabled));
                var capturedRecycle = (MenuItem)tree.ContextMenu.Items[1];
                tree.ContextMenu.IsOpen = false;
                var rootMap = (TreemapPanel)window.FindName("SpaceMap");
                var projectTile = rootMap.Buttons.Single(b => ((SpaceContribution)b.DataContext).Path == project);
                RightClick(window, projectTile);
                PumpUntil(() => projectTile.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(rootMapTotal, ((TextBlock)window.FindName("MapTotal")).Text);
                Assert.Equal(2, projectTile.ContextMenu.Items.Count);
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
                var tile = Assert.IsType<Button>(Assert.Single(map.Children.Cast<object>()));
                Assert.Equal(8192, Assert.IsType<SpaceContribution>(tile.DataContext).Bytes);
                tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((StackPanel)window.FindName("EntryDetail")).DataContext is ExplorerRow { Entry.IsDirectory: false }, TimeSpan.FromSeconds(10));
                Assert.Equal(Path.Join(bin, "sample.dll"), Assert.IsType<ExplorerRow>(((StackPanel)window.FindName("EntryDetail")).DataContext).Path);

                stage = "constructing cleanup review";
                var scan = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false).GetAwaiter().GetResult();
                var plan = CleanupPlanner.Create(scan, OpportunityDetector.Find(scan));
                var review = new ReviewWindow(plan);
                var clean = Descendants(review.Content as DependencyObject).OfType<Button>()
                    .Single(b => Equals(b.Content, "Clean up"));
                Assert.False(clean.IsEnabled);
                Assert.Equal(bin, Assert.IsType<Opportunity>(Assert.Single(((ListBox)review.FindName("LocationList")).Items.Cast<object>())).Path);
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
                    Assert.Contains("Rescan", ((TextBlock)window.FindName("StatusText")).Text);
                    Assert.Contains("OUT OF DATE", ((TextBlock)window.FindName("TreeNote")).Text);
                    Assert.Equal("—", ((TextBlock)window.FindName("CandidateText")).Text);
                    Assert.False(((Button)window.FindName("ReviewButton")).IsEnabled);
                    var staleBinContainer = Assert.IsType<ListBoxItem>(tree.ItemContainerGenerator.ContainerFromIndex(explorer.Find(binLink)));
                    RightClick(window, staleBinContainer);
                    PumpUntil(() => tree.ContextMenu.IsOpen, TimeSpan.FromSeconds(2));
                    Assert.True(((MenuItem)tree.ContextMenu.Items[0]).IsEnabled);
                    Assert.False(((MenuItem)tree.ContextMenu.Items[1]).IsEnabled);
                    tree.ContextMenu.IsOpen = false;
                    var retry = window.DeleteSelectedFolderAsync();
                    Assert.Null(retry.GetAwaiter().GetResult());
                    Assert.Contains("out of date", ((TextBlock)window.FindName("StatusText")).Text);
                }
                finally { DeletionTests.RestoreFixture(Assert.IsType<string>(recycled.RecycledPath), bin); }
                var refreshedExplorer = window.InitializeExplorerAsync(scan, [row.Item], default);
                PumpUntil(() => refreshedExplorer.IsCompleted, TimeSpan.FromSeconds(10));
                refreshedExplorer.GetAwaiter().GetResult();
                window.SetBusy(false);
                Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);

                string artifactRoot = FindRepository();
                var recyclePreview = new RecycleFolderWindow(FolderDeletionTarget.FromScan(scan, scan.PrimaryLink(row.Item.DirectoryIndex)));
#pragma warning disable WPF0001 // Exercise the same Fluent theme through its experimental C# API in this smoke test only.
                foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    stage = $"rendering {mode}";
                    app.ThemeMode = mode;
                    PumpUntil(() => true, TimeSpan.FromSeconds(1));
                    string theme = mode == ThemeMode.Light ? "light" : "dark";
                    ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"explorer-{theme}.png"), 1220, 910);
                    ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"main-{theme}.png"), 1220, 910);
                    Render(review, Path.Join(artifactRoot, "artifacts", "ui", $"review-{theme}.png"), 860, 700);
                    Render(details, Path.Join(artifactRoot, "artifacts", "ui", $"details-{theme}.png"), 700, 680);
                    Render(recyclePreview, Path.Join(artifactRoot, "artifacts", "ui", $"recycle-{theme}.png"), 590, 460);
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
                PumpUntil(() => true, TimeSpan.FromSeconds(1));
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

                stage = "rendering nested detail at desktop and compact sizes";
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
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"nested-{theme}.png"), 1220, 910);
                    Assert.InRange(((FrameworkElement)window.FindName("ScanHeader")).ActualHeight, 100, 130);
                    AssertMapFillsPane(window, map);
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"nested-compact-{theme}.png"), 1020, 790);
                    AssertMapFillsPane(window, map);
                    Assert.True(map.TranslatePoint(new Point(0, map.ActualHeight), (UIElement)window.Content).Y
                        <= detailPanel.TranslatePoint(new Point(), (UIElement)window.Content).Y + 1, "The nested map overlaps entry details.");
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
                nestedGroup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((Grid)window.FindName("ExplorePanel")).IsEnabled, TimeSpan.FromSeconds(10));
                Assert.True(groupedRows.IsExpanded(-1));
                Assert.Equal(@"G:\Github", Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
                Assert.True(Assert.IsType<ExplorerRow>(tree.SelectedItem).Expanded);
                Assert.Null(window.DeleteSelectedFolderAsync().GetAwaiter().GetResult());
                Assert.Contains("Grouped entries cannot be deleted", ((TextBlock)window.FindName("StatusText")).Text);
                stage = "closing windows";
                startup.Close(); review.Close(); details.Close(); recyclePreview.Close(); window.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { stage = "shutting down WPF"; app?.Shutdown(); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), $"The WPF smoke test timed out while {stage}.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static long GiB(double value) => (long)(value * 1024 * 1024 * 1024);

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
