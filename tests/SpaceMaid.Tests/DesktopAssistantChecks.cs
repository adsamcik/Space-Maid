using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.AI;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Extensions.Assistant;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckAssistant(MainWindow owner, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string content = Directory.CreateDirectory(Path.Join(fixture.Path, "data")).FullName;
        foreach (string name in new[] { "Generated website previews", "Older build output", "Recorded test videos", "Personal photos" })
        {
            string folder = Directory.CreateDirectory(Path.Join(content, name)).FullName;
            File.WriteAllBytes(Path.Join(folder, "sample.txt"), new byte[2048]);
        }
        Directory.CreateDirectory(Path.Join(fixture.Path, "settings"));
        File.WriteAllText(Path.Join(fixture.Path, "settings", "assistant.json"), JsonSerializer.Serialize(new { Enabled = true, ModelPath = Path.Join(fixture.Path, "fixture.gguf") }));
        var scanTask = new VolumeScanner().ScanAsync(content, preferFast: false);
        PumpUntil(() => scanTask.IsCompleted, TimeSpan.FromSeconds(10));
        var host = new AssistantUiHost(fixture.Path, scanTask.GetAwaiter().GetResult());
        ScriptedChatClient? client = null;
        bool waitForCancellation = false;
        var view = new AssistantView(host, default, (_, _) => client = new ScriptedChatClient(async (messages, _, token) =>
        {
            if (waitForCancellation) await Task.Delay(Timeout.Infinite, token);
            using var facts = JsonDocument.Parse(messages[1].Text);
            var items = facts.RootElement.GetProperty("firstChildren").GetProperty("items");
            var candidates = items.EnumerateArray().Where(item => item.GetProperty("name").GetString() != "Personal photos")
                .Select(item => new { id = item.GetProperty("id").GetString(), action = "investigate", explanation = "Check whether this output can be recreated. Preserve custom files and anything still in use." }).ToArray();
            return JsonSerializer.Serialize(new { kind = "propose", proposal = new { schemaVersion = 1, items = candidates } });
        }));
        var dialog = new Window { Title = "Local assistant", Owner = owner, Content = view, Width = 980, Height = 760,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        dialog.Show();
        try
        {
            typeof(AssistantView).GetField("_root", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, content);
            ((TextBlock)view.FindName("FolderText")).Text = content;
            var refresh = view.RefreshSummaryAsync(default); PumpUntil(() => refresh.IsCompleted, TimeSpan.FromSeconds(10)); refresh.GetAwaiter().GetResult();
            Assert.True(((Button)view.FindName("RunButton")).IsEnabled);
            Assert.Contains("8192 bytes", ((TextBlock)view.FindName("EvidenceText")).Text);
            ((Button)view.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var list = (DataGrid)view.FindName("SuggestionList");
            PumpUntil(() => list.Items.Count == 3 && ((Button)view.FindName("RunButton")).IsEnabled, TimeSpan.FromSeconds(10));
            Assert.True(client!.Disposed); Assert.Equal(0, host.Reviews);
            list.SelectedItem = list.Items[0];
            Assert.True(((Button)view.FindName("ReviewButton")).IsEnabled);
#pragma warning disable WPF0001
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = mode;
                Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", $"assistant-{(mode == ThemeMode.Light ? "light" : "compact-dark")}.png"), mode == ThemeMode.Light ? 980 : 740, mode == ThemeMode.Light ? 730 : 600);
                Assert.True(list.ActualHeight > 200, $"Assistant results crowded to {list.ActualHeight:0} pixels.");
                Assert.Equal(3, list.Items.Count); Assert.NotNull(list.SelectedItem);
                Assert.True(list.Columns.Sum(column => column.ActualWidth) < list.ActualWidth, "Host policy must remain visible in the compact table.");
            }
#pragma warning restore WPF0001
            ((Button)view.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => host.Revealed is not null, TimeSpan.FromSeconds(2));
            Assert.StartsWith(content, host.Revealed);
            ((Button)view.FindName("ReviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => host.Reviews == 1 && !((Button)view.FindName("CancelButton")).IsVisible, TimeSpan.FromSeconds(3));
            Assert.Empty(list.Items); Assert.False(((Button)view.FindName("ReviewButton")).IsEnabled);
            Assert.Equal(4, Directory.GetFiles(content, "*", SearchOption.AllDirectories).Length);
            refresh = view.RefreshSummaryAsync(default); PumpUntil(() => refresh.IsCompleted, TimeSpan.FromSeconds(3)); refresh.GetAwaiter().GetResult();
            ((TextBox)view.FindName("ExclusionsBox")).Text = Path.Join(content, "Personal photos");
            Assert.False(((Button)view.FindName("RunButton")).IsEnabled); Assert.Empty(((TextBlock)view.FindName("EvidenceText")).Text);
            refresh = view.RefreshSummaryAsync(default); PumpUntil(() => refresh.IsCompleted, TimeSpan.FromSeconds(3)); refresh.GetAwaiter().GetResult();
            waitForCancellation = true;
            ((Button)view.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => client!.Calls.Count > 0 && ((Button)view.FindName("CancelButton")).IsVisible, TimeSpan.FromSeconds(3));
            ((CheckBox)view.FindName("EnabledBox")).IsChecked = false;
            PumpUntil(() => client!.Disposed && !((Button)view.FindName("CancelButton")).IsVisible, TimeSpan.FromSeconds(3));
            Assert.Empty(list.Items); Assert.False(((Button)view.FindName("RunButton")).IsEnabled);
            Assert.Equal(1, host.Reviews); Assert.Equal(4, Directory.GetFiles(content, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            var shutdown = view.ShutdownAsync(); PumpUntil(() => shutdown.IsCompleted, TimeSpan.FromSeconds(3)); shutdown.GetAwaiter().GetResult();
            dialog.Close(); host.Dispose();
        }
        CheckAssistantHostReview(owner, app);
    }

    private static void CheckAssistantHostReview(MainWindow window, Application app)
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "sample.csproj"), "<Project />");
        string bin = Directory.CreateDirectory(Path.Join(fixture.Path, "bin")).FullName;
        string obj = Directory.CreateDirectory(Path.Join(fixture.Path, "obj")).FullName;
        File.WriteAllText(Path.Join(bin, "custom.txt"), "keep this custom fixture");
        File.WriteAllText(Path.Join(obj, "output.o"), "keep this generated fixture");
        BuildActivityFixture.AgeProject(fixture.Path);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(5)); var scan = scanning.GetAwaiter().GetResult();
        var findings = OpportunityDetector.Find(scan);
        var load = window.InitializeExplorerAsync(scan, findings, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, findings, OpportunityGrouping.Build(scan, findings), null, null, false]);
        var rows = (IEnumerable<OpportunityRow>)typeof(MainWindow).GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Assert.Equal(2, rows.Count()); foreach (var row in rows) row.Selected = row.Item.Path == obj;
        var runtime = (ExtensionRuntime)typeof(MainWindow).GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var opening = runtime.Evidence.OpenEvidenceSessionAsync(fixture.Path, [], default);
        PumpUntil(() => opening.IsCompleted, TimeSpan.FromSeconds(3)); using var session = opening.GetAwaiter().GetResult();
        var unmatched = runtime.ReviewEvidenceAsync(session, [session.Scope.Root.Id], default);
        PumpUntil(() => unmatched.IsCompleted, TimeSpan.FromSeconds(3)); Assert.Equal(0, unmatched.GetAwaiter().GetResult().MatchedLocations);
        var children = session.ChildrenAsync(session.Scope.Root.Id, 20, null, default);
        PumpUntil(() => children.IsCompleted, TimeSpan.FromSeconds(3)); var target = Assert.Single(children.GetAwaiter().GetResult().Items, item => item.Name == "bin");
        bool opened = false; Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            var review = app.Windows.OfType<ReviewWindow>().FirstOrDefault(candidate => candidate.IsVisible);
            if (review is null) return;
            try
            {
                opened = true;
                Assert.True(((Button)review.FindName("CloseButton")).IsDefault);
                Assert.Equal(bin, Assert.Single(((ListBox)review.FindName("LocationList")).Items.Cast<CleanupReviewRow>()).Path);
                ((Button)review.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error) { failure = error; review.Close(); }
        };
        timer.Start();
        try
        {
            var review = runtime.ReviewEvidenceAsync(session, [target.Id], default);
            PumpUntil(() => review.IsCompleted, TimeSpan.FromSeconds(10)); Assert.Equal(1, review.GetAwaiter().GetResult().MatchedLocations);
            if (failure is not null) throw failure;
            Assert.True(opened); Assert.Equal("keep this custom fixture", File.ReadAllText(Path.Join(bin, "custom.txt")));
            Assert.Equal("keep this generated fixture", File.ReadAllText(Path.Join(obj, "output.o")));
            var after = (IEnumerable<OpportunityRow>)typeof(MainWindow).GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.Equal(obj, Assert.Single(after, row => row.Selected).Item.Path);
        }
        finally { timer.Stop(); }
    }

    private sealed class AssistantUiHost : IExtensionHost, IExtensionEvidenceReview, IDisposable
    {
        private readonly ExtensionJobs _jobs = new((_, _) => { });
        internal AssistantUiHost(string root, ScanSnapshot scan)
        { DataDirectory = Path.Join(root, "settings"); Inventory = new ExtensionInventory(() => scan, () => false, _jobs.BeginDiscovery); }
        public string DataDirectory { get; }
        public string CacheDirectory => DataDirectory;
        public IInventoryReader Inventory { get; }
        public IExtensionJobs Jobs => new PhotoTestJobs();
        public IToolBroker Tools => new PhotoTestTools();
        internal int Reviews { get; private set; }
        internal string? Revealed { get; private set; }
        public void RevealLocation(string path) => Revealed = path;
        public void Log(string eventName, object data) { }
        public Task<EvidenceCleanupReviewResult> ReviewEvidenceAsync(IInventoryEvidenceSession session, IReadOnlyList<EvidenceId> selectedItems, CancellationToken token)
        { Assert.Single(selectedItems); Reviews++; return Task.FromResult(new EvidenceCleanupReviewResult(1, [], "Kept the folders.")); }
        public void Dispose() => _jobs.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
