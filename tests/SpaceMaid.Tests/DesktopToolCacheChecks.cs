using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.Core.Optimization;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckToolCaches(Application app, string artifactRoot)
    {
        using var fixture = new ToolCacheFixture();
        var window = new CacheMaintenanceWindow(fixture.Service, null,
            (plan, token) => fixture.Service.ExecuteAsync(plan, token)) { ConfirmMaintenance = _ => false };
        try
        {
            window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000; window.Top = -10000; window.Show();
            var refresh = (Button)window.FindName("RefreshButton");
            PumpUntil(() => refresh.IsEnabled, TimeSpan.FromSeconds(5));
            var list = (DataGrid)window.FindName("CacheList"); Assert.Equal(5, list.Items.Count);
            var search = (TextBox)window.FindName("SearchBox"); var review = (Button)window.FindName("ReviewButton");
            search.Text = "NuGet"; Assert.Single(list.Items);
            Assert.False(review.IsEnabled); Assert.Contains("up-to-date restores", ((TextBlock)window.FindName("DetailText")).Text);
            search.Text = "";
            list.SelectedItem = list.Items.Cast<ToolCacheObservation>().Single(row => row.Kind == ToolCacheKind.Pnpm);
            Assert.True(review.IsEnabled);
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme; string name = theme == ThemeMode.Light ? "light" : "dark";
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", "tool-caches-" + name + ".png"), 960, 730);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", "tool-caches-compact-" + name + ".png"), 760, 640);
                Assert.True(list.ActualHeight >= 140);
                Assert.True(review.IsVisible);
            }
            app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
            review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => review.IsEnabled, TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(fixture.Commands, c => c.Arguments.Contains("prune"));
            Assert.Contains("cancelled before", ((TextBlock)window.FindName("StatusText")).Text);
            window.ConfirmMaintenance = _ => true;
            review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => review.IsEnabled, TimeSpan.FromSeconds(5));
            Assert.Single(fixture.Commands, c => c.Arguments.Contains("prune"));
            Assert.Contains("completed", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.Contains("--store-dir", ((TextBox)window.FindName("CommandText")).Text);
            Assert.Contains("Native fixture report", ((TextBox)window.FindName("ReportText")).Text);
        }
        finally { window.Close(); }
    }
}
