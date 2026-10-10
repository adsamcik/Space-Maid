using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    public void FluentDesktopLoadsScansAndPreviewsWithCleanupDisabled()
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
                PumpUntil(() => scanButton.IsEnabled, TimeSpan.FromSeconds(15));
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
                var navigation = window.NavigateToEntryAsync(explorer.Scan.PrimaryLink(row.Item.DirectoryIndex));
                PumpUntil(() => navigation.IsCompleted, TimeSpan.FromSeconds(10));
                navigation.GetAwaiter().GetResult();
                Assert.Equal(bin, Assert.IsType<ExplorerRow>(tree.SelectedItem).Path);
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

                string artifactRoot = FindRepository();
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
                var wideBuilder = new ScanBuilder();
                wideBuilder.RootIndex = wideBuilder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
                for (int i = 0; i < 50_000; i++)
                {
                    int file = wideBuilder.AddFile(new(new((ulong)i + 2), FileAttributes.Normal, 4096, 4096, 0));
                    wideBuilder.AddLink(file, new(1), $"file-{i}.bin");
                }
                var wideScan = wideBuilder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
                stage = "binding the wide-folder indexed source";
                var wideLoad = window.InitializeExplorerAsync(wideScan, [], CancellationToken.None);
                PumpUntil(() => wideLoad.IsCompleted, TimeSpan.FromSeconds(10));
                wideLoad.GetAwaiter().GetResult();
                var wideContent = (FrameworkElement)window.Content;
                stage = "measuring the wide-folder window";
                window.UpdateLayout();
                Assert.Equal(50_001, tree.Items.Count);
                int realized = Descendants(tree).OfType<ListBoxItem>().Count();
                Assert.True(realized is > 0 and <= 100, $"The wide folder realized {realized:N0} containers.");
                stage = "scrolling to the final wide-folder entry";
                var wideRows = Assert.IsType<ExplorerRows>(tree.ItemsSource);
                tree.SelectedIndex = wideRows.Count - 1;
                tree.ScrollIntoView(tree.SelectedItem);
                wideContent.UpdateLayout();
                PumpUntil(() => tree.ItemContainerGenerator.ContainerFromIndex(wideRows.Count - 1) is not null, TimeSpan.FromSeconds(10));
                Assert.True(Descendants(tree).OfType<ListBoxItem>().Count() <= 100, "Scrolling realized the whole wide folder.");
                Assert.Equal(8192, new FileInfo(Path.Join(bin, "sample.dll")).Length);
                stage = "closing windows";
                startup.Close(); review.Close(); details.Close(); window.Close();
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
