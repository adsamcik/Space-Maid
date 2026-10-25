using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.Core.Deduplication;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckDuplicateWorkflow(Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        byte[] payload = Enumerable.Repeat((byte)71, 128 * 1024).ToArray();
        string first = Path.Join(fixture.Path, "Debug-library.pdb"), second = Path.Join(fixture.Path, "Release-library.pdb");
        string unique = Path.Join(fixture.Path, "standalone-library.pdb");
        File.WriteAllBytes(first, payload); File.WriteAllBytes(second, payload);
        File.WriteAllBytes(unique, Enumerable.Repeat((byte)42, payload.Length).ToArray()); OptimizationTests.Age(fixture.Path);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(10)); var scan = scanning.GetAwaiter().GetResult();
        var changed = new List<string>();
        var window = new DuplicateWindow(scan, [], paths => changed.AddRange(paths), _ => { }, default, new FileOperations(), (_, _) => { });
        try
        {
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.Show();
            var find = (Button)window.FindName("FindButton");
            find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => find.IsEnabled, TimeSpan.FromSeconds(10));
            var groups = (DataGrid)window.FindName("GroupList"); var files = (DataGrid)window.FindName("FileList");
            Assert.Single(groups.Items.Cast<object>()); Assert.Equal(2, files.Items.Count);
            var compress = (Button)window.FindName("CompressButton");
            if (StoragePreservation.Capabilities(first).Compression) PumpUntil(() => compress.IsEnabled, TimeSpan.FromSeconds(5));
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme; string appearance = theme == ThemeMode.Light ? "light" : "dark";
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"duplicates-{appearance}.png"), 1060, 760);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"duplicates-compact-{appearance}.png"), 760, 640);
                Assert.True(files.ActualHeight >= 230); Assert.True(find.IsVisible);
            }
            app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
            if (compress.IsEnabled)
            {
                compress.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => find.IsEnabled, TimeSpan.FromSeconds(10));
                Assert.Single(changed); Assert.Contains("Compressed", ((TextBlock)window.FindName("StatusText")).Text);
                Assert.Equal(payload, File.ReadAllBytes(first)); Assert.Equal(payload, File.ReadAllBytes(second));
                Assert.False(compress.IsEnabled);
            }
            var inspect = window.InspectFileAsync(unique);
            PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(10)); inspect.GetAwaiter().GetResult();
            Assert.Single(files.Items.Cast<object>());
            Assert.Contains("Inspected build resource", ((TextBlock)window.FindName("SummaryText")).Text);
            var stopping = window.StopAsync(); PumpUntil(() => stopping.IsCompleted, TimeSpan.FromSeconds(5)); stopping.GetAwaiter().GetResult();
        }
        finally { window.Close(); }
    }
}
