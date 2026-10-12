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
}
