using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.IO;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckExtensionWorkspace(MainWindow window, Application app, string artifactRoot, DiskAccess expectedFiles)
    {
        using var fixture = new TestDirectory();
        string previousPath = window.CurrentPath;
        var existingWindows = app.Windows.Cast<Window>().ToHashSet();
        try
        {
            var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow).GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.All(runtime.Entries, entry => Assert.Null(entry.Error));
            var agents = Assert.Single(runtime.Entries, entry => entry.Id == "space-maid.agents");
            Assert.True(window.OpenExtension(agents.Id));
            window.UpdateLayout();
            Assert.True(agents.View!.IsVisible);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("ScanHeader")).Visibility);
            var parent = agents.View.Parent;
            Assert.True(window.OpenExtension("space-maid.models"));
            Assert.False(agents.View.IsVisible);
            Assert.True(window.OpenExtension(agents.Id));
            Assert.Same(parent, agents.View.Parent);
            Assert.False(window.OpenExtension("missing.extension"));
            Assert.Single(app.Windows.OfType<MainWindow>());
            Assert.True(existingWindows.SetEquals(app.Windows.Cast<Window>()), "Switching extension workflows must not open additional windows.");
            typeof(MainWindow).GetMethod("BeginPathEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Assert.True(((TextBox)window.FindName("PathEditor")).IsVisible);
            Assert.True(((RadioButton)window.FindName("ExploreTab")).IsChecked);
            typeof(MainWindow).GetMethod("EndPathEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

            ((RadioButton)window.FindName("ExtensionsTab")).IsChecked = true;
            var manager = Assert.IsType<ExtensionsView>(((ContentControl)window.FindName("ExtensionManagerContent")).Content);
            var search = (TextBox)manager.FindName("SearchBox");
            var list = (ListBox)manager.FindName("ExtensionList");
            search.Text = "agent";
            Assert.Equal(agents.Id, Assert.IsType<LoadedExtension>(Assert.Single(list.Items.Cast<object>())).Id);
            var enabled = (CheckBox)manager.FindName("EnabledBox");
            enabled.IsChecked = false; enabled.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(runtime.HasPendingChange(agents));
            Assert.Contains("after restart", ((TextBlock)manager.FindName("StateText")).Text);
            ((Button)manager.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(agents.View.IsVisible); // Current session remains loaded.
            ((RadioButton)window.FindName("ExtensionsTab")).IsChecked = true;
            Assert.Equal("agent", search.Text); Assert.Equal(agents.Id, Assert.IsType<LoadedExtension>(list.SelectedItem).Id);
            enabled.IsChecked = true; enabled.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(runtime.HasPendingChange(agents));
            search.Text = "no-such-extension";
            Assert.Empty(list.Items); Assert.Equal(Visibility.Visible, ((TextBlock)manager.FindName("EmptyText")).Visibility);
            search.Text = "";
            CheckExtensionContextRecovery(window, runtime, manager, fixture.Path);

            string output = Path.Join(artifactRoot, "artifacts", "ui");
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme;
                foreach (var size in new[] { (1220, 940), (1020, 820) })
                {
                    Render(window, Path.Join(output, $"extensions-workspace-{theme}-{size.Item1}.png"), size.Item1, size.Item2);
                    Assert.True(list.ActualHeight > 400);
                    Assert.True(((FrameworkElement)window.FindName("ExtensionManagerContent")).ActualWidth > 750);
                    Assert.True(((Button)manager.FindName("AddButton")).IsVisible);
                }
            }
#pragma warning restore WPF0001
            Assert.True(window.OpenExtension(agents.Id));
            Render(window, Path.Join(output, "extension-agent-workspace.png"), 1220, 940);

            var apps = runtime.Entries.Single(entry => entry.Id == "space-maid.apps").View!;
            var host = Assert.IsAssignableFrom<IExtensionHost>(apps.GetType().GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(apps));
            Assert.Same(expectedFiles, host.Files);
            var navigation = Assert.IsAssignableFrom<IExtensionNavigationHost>(host).Navigation!;
            window.SetScanTarget(@"R:\Context");
            Assert.Equal(@"R:\", apps.GetType().GetField("_drive", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(apps));
            var open = Task.Run(() => navigation.OpenExtensionAsync(agents.Id));
            PumpUntil(() => open.IsCompleted, TimeSpan.FromSeconds(5)); Assert.True(open.GetAwaiter().GetResult());
            string target = Directory.CreateDirectory(Path.Join(fixture.Path, "owned-folder")).FullName;
            File.WriteAllText(Path.Join(target, "sample.txt"), "sample");
            var measured = host.Files.MeasureFolderAsync(target);
            PumpUntil(() => measured.IsCompleted, TimeSpan.FromSeconds(10));
            Assert.Equal(6, measured.GetAwaiter().GetResult().LogicalBytes);
            var explore = Task.Run(() => navigation.ExploreFolderAsync(target));
            PumpUntil(() => explore.IsCompleted, TimeSpan.FromSeconds(15)); Assert.True(explore.GetAwaiter().GetResult());
            Assert.Equal(target, window.CurrentPath); Assert.True(((RadioButton)window.FindName("ExploreTab")).IsChecked);
            Assert.Equal(Path.GetPathRoot(target), apps.GetType().GetField("_drive", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(apps));
        }
        finally
        {
            window.SetScanTarget(previousPath);
            ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;
        }
    }

    private sealed class RecoverableContextView : UserControl, IExtensionContextView
    {
        internal bool Fail = true;
        internal int Calls;
        public void SetContext(ExtensionViewContext context)
        { Calls++; if (Fail) throw new IOException("Owned transient context fixture"); }
    }

    private static void CheckExtensionContextRecovery(MainWindow window, ExtensionRuntime runtime, ExtensionsView manager, string fixture)
    {
        var entries = Assert.IsType<List<LoadedExtension>>(typeof(ExtensionRuntime).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));
        var links = Assert.IsType<Dictionary<string, RadioButton>>(typeof(MainWindow).GetField("_extensionLinks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var healthy = runtime.Entries.Single(entry => entry.Id == "space-maid.agents").View!;
        var healthyParent = healthy.Parent;
        var view = new RecoverableContextView();
        var entry = new LoadedExtension("test.context-recovery", "Recovery fixture", fixture, "Loaded", view);
        string previous = window.CurrentPath;
        entries.Add(entry);
        try
        {
            typeof(MainWindow).GetMethod("InitializeExtensionWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Assert.False(view.IsEnabled);
            int failedCalls = view.Calls;
            window.SetBusy(true); window.SetBusy(false);
            Assert.Equal(failedCalls, view.Calls); // Repeated publication must not loop failed delivery.
            view.Fail = false;
            var retry = Assert.IsAssignableFrom<Task>(typeof(MainWindow).GetMethod("RetryExtensionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null));
            PumpUntil(() => retry.IsCompleted, TimeSpan.FromSeconds(15)); retry.GetAwaiter().GetResult();
            Assert.True(view.IsEnabled);
            Assert.Same(healthy, runtime.Entries.Single(item => item.Id == "space-maid.agents").View);
            Assert.Same(healthyParent, healthy.Parent);
            Assert.Same(manager, ((ContentControl)window.FindName("ExtensionManagerContent")).Content);
            Assert.Single(links.Where(pair => pair.Key == entry.Id));
            view.Fail = true;
            window.SetBusy(true); // The inherited disabled value is not the view's local state.
            window.SetScanTarget(@"R:\OwnedRecoveryFailure");
            window.SetBusy(false);
            Assert.False(view.IsEnabled);
            view.Fail = false;
            window.SetScanTarget(@"R:\OwnedRecoveryChangedContext");
            Assert.True(view.IsEnabled);
        }
        finally
        {
            entries.Remove(entry);
            if (links.Remove(entry.Id, out var link))
            {
                if (link.Tag is FrameworkElement page && page.Parent is Panel pages) pages.Children.Remove(page);
                if (link.Parent is Panel navigation) navigation.Children.Remove(link);
            }
            foreach (string field in new[] { "_publishedContexts", "_failedContexts", "_contextEnabled" })
                ((System.Collections.IDictionary)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Remove(view);
            ((HashSet<FrameworkElement>)typeof(MainWindow).GetField("_contextFailures", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Remove(view);
            manager.ReloadEntries(); window.SetScanTarget(previous);
        }
    }
}
