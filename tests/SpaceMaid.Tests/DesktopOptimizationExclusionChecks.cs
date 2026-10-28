using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckOptimizationExclusions(Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        OptimizationTests.MakePython(fixture.Path, "first"); OptimizationTests.MakePython(fixture.Path, "second");
        OptimizationTests.Age(fixture.Path);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(10));
        var scan = scanning.GetAwaiter().GetResult(); var findings = DeveloperOpportunityFixture.Find(scan);
        var rule = Assert.Single(findings).AutomaticCleanup!;
        string savedPath = Path.Join(fixture.Path, "settings.json"), existing = Path.Join(fixture.Path, "Personal notes");
        string cache = Path.Join(fixture.Path, "__pycache__");
        var policy = new OptimizationPolicy { ScopePath = fixture.Path, ExcludedPaths = [existing],
            EnabledRules = [rule.ConsentKey(DeveloperOpportunityFixture.ProviderId)] };
        var window = new OptimizationWindow(scan, findings, new FileOperations(), new(policy),
            chosen => new OptimizationSettings(chosen).Save(savedPath),
            (_, _, _) => throw new InvalidOperationException("Exclusion editing must not delete files."));
        try
        {
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.Show();
            var list = (ListBox)window.FindName("ExclusionsList"); var tab = (TabItem)window.FindName("ExclusionsTab");
            var files = (DataGrid)window.FindName("FileList"); var run = (Button)window.FindName("RunButton");
            var preview = (Button)window.FindName("PreviewButton"); var save = (Button)window.FindName("SaveButton");
            Assert.Equal(existing, Assert.IsType<OptimizationWindow.ExclusionRow>(Assert.Single(list.Items.Cast<object>())).Path);
            Assert.Throws<ArgumentException>(() => window.AddExclusions([cache, "relative"])); Assert.Single(list.Items.Cast<object>());
            window.AddExclusions([cache, cache.ToUpperInvariant()]); Assert.Equal(2, list.Items.Count);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new[] { existing, cache }, OptimizationSettings.Load(savedPath).Policy.ExcludedPaths);
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10)); Assert.Empty(files.Items.Cast<object>()); Assert.False(run.IsEnabled);
            tab.IsSelected = true;
            window.UpdateLayout();
            Descendants(list).OfType<Button>().Single(button => button.DataContext is OptimizationWindow.ExclusionRow row && row.Path == cache)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(list.Items.Cast<object>()); Assert.False(run.IsEnabled);
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10)); Assert.Equal(2, files.Items.Count); Assert.True(run.IsEnabled);
            files.SelectedIndex = 0;
            ((Button)window.FindName("KeepFileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, list.Items.Count); Assert.False(run.IsEnabled); Assert.Empty(files.Items.Cast<object>());
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10)); Assert.Single(files.Items.Cast<object>());
            files.SelectedIndex = 0;
            ((Button)window.FindName("KeepFolderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(3, list.Items.Count); Assert.False(run.IsEnabled);
            Assert.True(tab.IsSelected); Assert.Equal("Excluded locations (3)", tab.Header);
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme; string name = theme == ThemeMode.Light ? "light" : "dark";
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"optimization-exclusions-{name}.png"), 980, 790);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"optimization-exclusions-compact-{name}.png"), 760, 640);
                Assert.True(list.ActualHeight >= 160, "Exclusions should have a useful working area at compact sizes.");
            }
#pragma warning restore WPF0001
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(3, OptimizationSettings.Load(savedPath).Policy.ExcludedPaths.Length);
            while (list.Items.Count > 0)
            {
                window.UpdateLayout();
                Descendants(list).OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("NoExclusionsText")).Visibility);
            Assert.Equal(Visibility.Collapsed, list.Visibility); Assert.Equal("Excluded locations", tab.Header);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "optimization-exclusions-empty-dark.png"), 760, 640);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Empty(OptimizationSettings.Load(savedPath).Policy.ExcludedPaths);
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => preview.IsEnabled, TimeSpan.FromSeconds(10)); Assert.Equal(2, files.Items.Count);
            Assert.True(((TabItem)window.FindName("GeneratedFilesTab")).IsSelected);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "optimization-preview-compact-dark.png"), 760, 640);
            Assert.True(files.ActualHeight >= 180, "The generated-file preview should retain a useful working area.");
            Assert.Equal(2, Directory.GetFiles(cache).Length);
        }
        finally
        {
            window.Close();
#pragma warning disable WPF0001
            app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        }
    }
}
