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
    private static long GiB(double value) => (long)(value * 1024 * 1024 * 1024);

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
