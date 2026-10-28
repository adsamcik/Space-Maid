using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceMaid.Installer;
using Xunit;

#pragma warning disable WPF0001

namespace SpaceMaid.Tests;

[Trait("Stage", "System")]
public sealed class InstallerWindowTests
{
    [Fact]
    public void SetupShowsInstallUpdateAndOpenChoicesAndRetainsPortableSelection()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                string repository = FindRepository();
                string setupVersion = new InstallerPayloads(typeof(InstallerWindow).Assembly).Version;
                string folder = @"D:\My applications\Tools and utilities\Space Maid";
                foreach (ThemeMode theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    app.ThemeMode = theme;
                    foreach (string? version in new[] { null, "0.1.0-alpha.2", setupVersion, "99.0.0" })
                    {
                        InstalledApplication? current = version is null ? null : new(folder, Velopack.SemanticVersion.Parse(version));
                        var window = new InstallerWindow(() => current) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                        try
                        {
                            window.Show();
                            var action = (Button)window.FindName("ActionButton");
                            var portable = (RadioButton)window.FindName("PortableMode");
                            var install = (RadioButton)window.FindName("InstallMode");
                            var path = (TextBox)window.FindName("FolderPath");
                            var folders = (FrameworkElement)window.FindName("FolderPanel");
                            string expected = version is null ? "Install" : current!.NeedsUpdate(setupVersion) ? "Update" : "Open Space Maid";
                            Assert.Equal(expected, action.Content);
                            Assert.Equal(((TextBlock)window.FindName("InstallModeTitle")).Text, System.Windows.Automation.AutomationProperties.GetName(install));
                            Assert.Equal("Portable folder", System.Windows.Automation.AutomationProperties.GetName(portable));
                            Assert.Equal(Visibility.Collapsed, folders.Visibility);
                            Assert.Equal(current?.Folder ?? "", ((TextBlock)window.FindName("InstalledFolderLabel")).Text);
                            Capture(window, Path.Join(repository, "artifacts", "installer-update", $"{expected.Split(' ')[0]}-{theme}.png"));
                            portable.IsChecked = true;
                            path.Text = @"D:\Portable apps\Space Maid";
                            Assert.Equal("Create portable app", action.Content);
                            Assert.Equal(Visibility.Visible, folders.Visibility);
                            install.IsChecked = true;
                            Assert.Equal(expected, action.Content);
                            portable.IsChecked = true;
                            Assert.Equal(@"D:\Portable apps\Space Maid", path.Text);
                            Capture(window, Path.Join(repository, "artifacts", "installer-update", $"Portable-{theme}.png"));
                            install.IsChecked = true;
                            // A newly changed installation must be reviewed before any process can launch.
                            current = new(folder, Velopack.SemanticVersion.Parse("100.0.0"));
                            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Assert.Equal("Open Space Maid", action.Content);
                            Assert.Contains("installation changed", ((TextBlock)window.FindName("Status")).Text);
                        }
                        finally { window.Close(); }
                    }
                }
            }
            catch (Exception error) { failure = error; }
            finally { app?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Installer window inspection timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "SpaceMaid.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository not found.");
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
        int height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
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
