using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckCloudDrives(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string root = Directory.CreateDirectory(Path.Join(fixture.Path, "Proton Drive")).FullName;
        string folder = Directory.CreateDirectory(Path.Join(root, "Holiday photos")).FullName;
        File.WriteAllBytes(Path.Join(folder, "pending.jpg"), new byte[1024]);
        string file = Path.Join(root, "Lake panorama — original.jpg");
        File.WriteAllBytes(file, new byte[4096]);
        using var registration = CloudFilesTests.RegisterSyncedFixture(root, file);
        var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow).GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var entry = Assert.Single(runtime.Entries, entry => entry.Id == "space-maid.cloud");
        Assert.Equal("Loaded", entry.Status); Assert.Null(entry.Error);
        var view = Assert.IsAssignableFrom<UserControl>(entry.View);
        Assert.True(window.OpenExtension(entry.Id));
        Task Invoke(string method, params object[] args) => (Task)view.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, args)!;
        void Finish(Task task) { PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10)); task.GetAwaiter().GetResult(); }
        var list = (DataGrid)view.FindName("FileList");
        var free = (Button)view.FindName("FreeButton");
        var search = (TextBox)view.FindName("SearchBox");
        Assert.Empty(list.Items); Assert.False(free.IsEnabled);
        Finish(Invoke("AddRootAsync", "Proton Drive", root));
        Assert.Equal(root, ((TextBox)view.FindName("FolderPath")).Text);
        Assert.Equal(2, list.Items.Count);
        list.SelectedIndex = 1; Assert.True(free.IsEnabled);
        search.Text = "no-such-file"; Assert.Empty(list.Items);
        search.Text = ""; Assert.Equal(2, list.Items.Count); list.SelectedIndex = 1;

#pragma warning disable WPF0001
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            foreach (var size in new[] { (1220, 940), (1020, 820) })
            {
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cloud-drives-{theme}-{size.Item1}.png"), size.Item1, size.Item2);
                Assert.True(list.ActualHeight > 350); Assert.True(list.ActualWidth > 550);
                Assert.True(free.IsVisible);
            }
        }
#pragma warning restore WPF0001
        void AnswerConfirmation(bool confirm)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                if (app.Windows.OfType<OnlineOnlyWindow>().FirstOrDefault(candidate => candidate.IsVisible
                    && ((TextBox)candidate.FindName("TargetPath")).Text == file) is not { } dialog) return;
                timer.Stop();
                Assert.Equal(file, ((TextBox)dialog.FindName("TargetPath")).Text);
                dialog.DialogResult = confirm;
            };
            timer.Start();
            try { Finish(Invoke("FreeSelectedAsync")); }
            finally { timer.Stop(); }
        }
        AnswerConfirmation(false);
        Assert.NotEqual(2, CloudFiles.Inspect(file)!.PinState); Assert.Equal(2, list.Items.Count);
        Assert.Contains("cancelled", ((TextBlock)view.FindName("StatusText")).Text);
        AnswerConfirmation(true);
        Assert.Equal(2, CloudFiles.Inspect(file)!.PinState);
        Assert.Equal(4096, new FileInfo(file).Length); Assert.Equal(4096, CloudFiles.Inspect(file)!.LocalBytes);
        Assert.Empty(list.Items); Assert.False(free.IsEnabled);
        Assert.Contains("requests accepted", ((TextBlock)view.FindName("StatusText")).Text);
        Finish(Invoke("BrowseAsync", root));
        list.SelectedIndex = 0;
        ((Button)view.FindName("OpenFolderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((TextBox)view.FindName("FolderPath")).Text == folder, TimeSpan.FromSeconds(5));
        Assert.Single(list.Items); list.SelectedIndex = 0; Assert.False(free.IsEnabled);
        Assert.True(((Button)view.FindName("ExplorerButton")).IsEnabled);
        ((Button)view.FindName("UpButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((TextBox)view.FindName("FolderPath")).Text == root, TimeSpan.FromSeconds(5));
        Assert.NotNull(list.SelectedItem);
        Assert.Equal(folder, ((CloudDirectoryEntry)list.SelectedItem.GetType().GetProperty("Entry")!.GetValue(list.SelectedItem)!).Path);
        ((Button)view.FindName("OpenFolderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((TextBox)view.FindName("FolderPath")).Text == folder, TimeSpan.FromSeconds(5));
        search.Text = "pending";
        Assert.True(window.OpenExtension("space-maid.apps")); Assert.True(window.OpenExtension(entry.Id));
        Assert.Equal("pending", search.Text); Assert.Equal(folder, ((TextBox)view.FindName("FolderPath")).Text);
        search.Text = "";
        // An unavailable saved root remains a recoverable error rather than an empty success.
        Finish(Invoke("AddRootAsync", "Google Drive", Path.Join(fixture.Path, "Disconnected Google Drive")));
        Assert.Contains("Could not finish", ((TextBlock)view.FindName("StatusText")).Text);
        Assert.False(free.IsEnabled);
        foreach (string provider in new[] { "OneDrive", "Dropbox" })
        {
            string location = Directory.CreateDirectory(Path.Join(fixture.Path, provider)).FullName;
            File.WriteAllText(Path.Join(location, "original.txt"), "keep");
            Finish(Invoke("AddRootAsync", provider, location));
            Assert.Single(list.Items); list.SelectedIndex = 0; Assert.False(free.IsEnabled);
            Assert.Equal("keep", File.ReadAllText(Path.Join(location, "original.txt")));
        }
        // Loading another bounded page retains filtering, selection and old
        // rows if the folder changes before continuation can be validated.
        string pagedRoot = Directory.CreateDirectory(Path.Join(fixture.Path, "Paged cloud")).FullName;
        for (int i = 0; i < 5001; i++) File.WriteAllBytes(Path.Join(pagedRoot, $"file-{i:D4}.bin"), []);
        Finish(Invoke("AddRootAsync", "OneDrive", pagedRoot));
        var more = (Button)view.FindName("LoadMoreButton");
        Assert.Equal(5000, list.Items.Count); Assert.True(more.IsVisible);
        search.Text = "5000.bin";
        Assert.Empty(list.Items);
        Assert.Contains("loaded", ((TextBlock)view.FindName("EmptyText")).Text);
        search.Text = "file-"; list.SelectedIndex = 23;
        var selected = list.SelectedItem;
        Finish(Invoke("LoadMoreAsync"));
        Assert.Equal("file-", search.Text); Assert.Equal(5001, list.Items.Count);
        Assert.Same(selected, list.SelectedItem); Assert.False(more.IsVisible);
        search.Text = "5000.bin"; Assert.Single(list.Items);
        search.Text = ""; Finish(Invoke("BrowseAsync", pagedRoot));
        list.SelectedIndex = 23; selected = list.SelectedItem;
        File.WriteAllText(Path.Join(pagedRoot, "new-file.bin"), "keep");
        Directory.SetLastWriteTimeUtc(pagedRoot, DateTime.UtcNow.AddSeconds(1));
        Finish(Invoke("LoadMoreAsync"));
        Assert.Equal(5000, list.Items.Count); Assert.Same(selected, list.SelectedItem);
        Assert.False(more.IsVisible);
        Assert.Contains("refresh", ((TextBlock)view.FindName("DetailsText")).Text);
        Finish(Invoke("BrowseAsync", pagedRoot));
        Assert.True(more.IsVisible); Finish(Invoke("LoadMoreAsync"));
        Assert.Equal(5002, list.Items.Count);
        Assert.True(window.OpenExtension("space-maid.apps"));
    }
}
