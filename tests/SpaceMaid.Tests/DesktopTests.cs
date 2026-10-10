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

                stage = "constructing cleanup review";
                var scan = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false).GetAwaiter().GetResult();
                var plan = CleanupPlanner.Create(scan, OpportunityDetector.Find(scan));
                var review = new ReviewWindow(plan);
                var clean = Descendants(review.Content as DependencyObject).OfType<Button>()
                    .Single(b => Equals(b.Content, "Clean up"));
                Assert.False(clean.IsEnabled);
                Assert.Equal(bin, Assert.IsType<Opportunity>(Assert.Single(((ListBox)review.FindName("LocationList")).Items.Cast<object>())).Path);
                var startup = new StartupWindow();
                Assert.Contains(Descendants(startup.Content as DependencyObject).OfType<Button>(), b => Equals(b.Content, "Continue without admin"));

                string artifactRoot = FindRepository();
#pragma warning disable WPF0001 // Exercise the same Fluent theme through its experimental C# API in this smoke test only.
                foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    stage = $"rendering {mode}";
                    app.ThemeMode = mode;
                    PumpUntil(() => true, TimeSpan.FromSeconds(1));
                    string theme = mode == ThemeMode.Light ? "light" : "dark";
                    Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"main-{theme}.png"), 1220, 790);
                    Render(review, Path.Join(artifactRoot, "artifacts", "ui", $"review-{theme}.png"), 860, 700);
                }
#pragma warning restore WPF0001
                Render(startup, Path.Join(artifactRoot, "artifacts", "ui", "startup.png"), 570, 305);
                Assert.Equal(8192, new FileInfo(Path.Join(bin, "sample.dll")).Length);
                stage = "closing windows";
                startup.Close(); review.Close(); window.Close();
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
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
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
