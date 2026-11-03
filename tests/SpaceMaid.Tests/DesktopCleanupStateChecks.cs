using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckCleanupWorkspaceState(MainWindow window, Application app, string artifactRoot)
    {
        Assert.Same(app, Application.Current);
        CheckCleanupDamagedDrafts(window);
        var host = (ContentControl)window.FindName("CleanupWorkspaceContent");
        var browser = (ContentControl)window.FindName("OpportunitiesContent");
        object original = host.Content; Visibility hostVisibility = host.Visibility, browserVisibility = browser.Visibility;
        try
        {
            ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
            browser.Visibility = Visibility.Collapsed; host.Visibility = Visibility.Visible;
            CheckCleanupBackgroundSelection(window, host, artifactRoot);
            CheckCleanupDraftNavigation(window, host, artifactRoot);
        }
        finally { host.Content = original; host.Visibility = hostVisibility; browser.Visibility = browserVisibility; }
    }

    private static void CheckCleanupDamagedDrafts(MainWindow window)
    {
        using var fixture = new TestDirectory();
        _ = CleanupRulePlanTests.Project(fixture.Path, "project", 60);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        AwaitCleanupState(scanning); var scan = scanning.GetAwaiter().GetResult();
        var findings = DeveloperOpportunityFixture.Find(scan);
        string valid = JsonSerializer.Serialize(new
        {
            Definition = new CleanupDefinition { DiscoveryRootPath = fixture.Path, ScopePaths = [fixture.Path] },
            Name = "Owned draft", Days = "30", WorktreeDays = "30", Exclusions = "", Time = "09:00", LimitGiB = "10", LimitTargets = "500",
            Build = true, Cache = false, Worktrees = false, Action = 0, WorktreeRule = 0, Frequency = 0, Day = DayOfWeek.Monday, Automatic = false
        });
        string Damaged(string field)
        {
            var document = JsonNode.Parse(valid)!.AsObject();
            var definition = document["Definition"]!.AsObject();
            if (field == "Definition") document["Definition"] = null;
            else if (field == "ManualRuleKeys") definition[field] = new JsonArray((JsonNode?)null);
            else definition[field] = null;
            return document.ToJsonString();
        }
        var cases = new[] { ("empty", "{}"), ("null", "null"), ("definition", Damaged("Definition")),
            ("root", Damaged("DiscoveryRootPath")), ("scopes", Damaged("ScopePaths")),
            ("worktree", Damaged("WorktreePolicy")), ("limits", Damaged("Limits")), ("keys", Damaged("ManualRuleKeys")) };
        foreach (var (name, damaged) in cases)
        {
            string directory = Directory.CreateDirectory(Path.Join(fixture.Path, "draft-" + name)).FullName;
            string path = Path.Join(directory, "workspace-draft.json"); File.WriteAllText(path, damaged);
            var workspace = new CleanupWorkspace(window.Operations, new(window.Operations.Files, directory), DeveloperOpportunityFixture.Find, directory);
            try
            {
                var notice = (TextBlock)workspace.FindName("NoticeText");
                Assert.Equal(Visibility.Visible, notice.Visibility); Assert.Contains("Saved draft", notice.Text);
                Assert.Equal(damaged, File.ReadAllText(path));
                workspace.SetSource(scan, findings, 3); workspace.ReviewSelection(scan, findings, []); WaitForWorkspacePlan(workspace, 3);
                Assert.True(((Button)workspace.FindName("CleanupButton")).IsEnabled);
                Assert.True(((TextBox)workspace.FindName("NameBox")).IsEnabled);
            }
            finally { AwaitCleanupState(workspace.StopAsync()); }
            Assert.Equal(damaged, File.ReadAllText(path));
        }
        string largeDirectory = Directory.CreateDirectory(Path.Join(fixture.Path, "large-draft")).FullName;
        string largePath = Path.Join(largeDirectory, "workspace-draft.json");
        string[] scopes = Enumerable.Range(0, 600).Select(index => Path.Join(fixture.Path, new string('p', 180), "project-" + index, "bin")).ToArray();
        var large = JsonNode.Parse(valid)!.AsObject();
        large["Definition"]!["ScopePaths"] = JsonSerializer.SerializeToNode(scopes);
        File.WriteAllText(largePath, large.ToJsonString()); Assert.True(new FileInfo(largePath).Length > 128 * 1024);
        var largeWorkspace = new CleanupWorkspace(window.Operations, new(window.Operations.Files, largeDirectory), DeveloperOpportunityFixture.Find, largeDirectory);
        try
        {
            Assert.Equal(string.Join(Environment.NewLine, scopes), ((ComboBox)largeWorkspace.FindName("ScopeBox")).ToolTip);
            ((TextBox)largeWorkspace.FindName("NameBox")).Text = "Large saved selection";
        }
        finally { AwaitCleanupState(largeWorkspace.StopAsync()); }
        var savedLarge = JsonNode.Parse(File.ReadAllText(largePath))!;
        Assert.Equal("Large saved selection", savedLarge["Name"]!.GetValue<string>());
        Assert.Equal(scopes, savedLarge["Definition"]!["ScopePaths"]!.Deserialize<string[]>());
    }

    private static void CheckCleanupBackgroundSelection(MainWindow window, ContentControl host, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string data = Directory.CreateDirectory(Path.Join(fixture.Path, "profile")).FullName;
        string activeScope = Directory.CreateDirectory(Path.Join(fixture.Path, "active-scope")).FullName;
        string historyScope = Directory.CreateDirectory(Path.Join(fixture.Path, "history-scope")).FullName;
        var store = new CleanupRunStore(window.Operations.Files, data);
        var activeDefinition = new CleanupDefinition { Name = "Background cleanup A", DiscoveryRootPath = fixture.Path, ScopePaths = [activeScope] };
        var historicalDefinition = new CleanupDefinition { Name = "Historical cleanup B", DiscoveryRootPath = fixture.Path, ScopePaths = [historyScope] };
        var active = new CleanupRunRecord { Definition = activeDefinition, ScanRootPath = fixture.Path, State = CleanupRunState.Running,
            Source = "Scheduled", ApprovalClaimId = Guid.NewGuid(), ApprovedUtc = DateTimeOffset.UtcNow };
        var location = new Opportunity(0, "Owned old build", "Build output", Path.Join(historyScope, "bin"), 123, 123, "Frozen fixture", "", "", false);
        var history = new CleanupRunRecord { Definition = historicalDefinition, ScanRootPath = fixture.Path, State = CleanupRunState.Completed,
            Targets = [new(location, null, "Kept", "Kept by this historical rule.")], Message = "Historical fixture receipt." };
        store.SaveRun(active); store.SaveRun(history);
        var workspace = new CleanupWorkspace(window.Operations, store, DeveloperOpportunityFixture.Find, data); host.Content = workspace; window.UpdateLayout();
        try
        {
            ClickCleanupState(workspace, "HistoryButton");
            var panel = (StackPanel)workspace.FindName("LibraryPanel");
            PumpUntil(() => CleanupStateButton(panel, "View run: Historical cleanup B") is not null, TimeSpan.FromSeconds(5));
            CleanupStateButton(panel, "View run: Historical cleanup B")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => ((StackPanel)workspace.FindName("ResultPanel")).Visibility == Visibility.Visible
                && ((TextBlock)workspace.FindName("ResultPolicy")).Text.Contains(historyScope, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            ClickCleanupState(workspace, "InspectResultsButton");
            var inspector = Assert.IsType<CleanupInspector>(((Border)workspace.FindName("InspectorHost")).Child);
            Assert.Equal(history.Id, inspector.CapturePresentation().ContextId);
            AwaitCleanupState(workspace.RefreshStatusAsync());
            PumpUntil(() => ((TextBlock)workspace.FindName("RunningText")).Text.Contains(activeDefinition.Name, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            Assert.Contains(historyScope, ((TextBlock)workspace.FindName("ResultPolicy")).Text);
            Assert.Equal(history.Id, inspector.CapturePresentation().ContextId);
            ClickCleanupState(workspace, "StopButton");
            Assert.True(store.IsStopRequested(active.Id)); Assert.False(store.IsStopRequested(history.Id));
            Assert.Equal(CleanupRunState.Completed, store.LoadRun(history.Id)!.State);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-background-history.png"), 1220, 940);
        }
        finally
        {
            store.SaveRun(active with { State = CleanupRunState.Stopped, Message = "Owned metadata-only fixture stopped." });
            AwaitCleanupState(workspace.StopAsync()); host.Content = null;
        }
    }

    private static void CheckCleanupDraftNavigation(MainWindow window, ContentControl host, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        for (int i = 0; i < 205; i++) _ = CleanupRulePlanTests.Project(fixture.Path, "project-" + i.ToString("000"), 60);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false); AwaitCleanupState(scanning);
        var scan = scanning.GetAwaiter().GetResult(); var findings = DeveloperOpportunityFixture.Find(scan);
        string data = Directory.CreateDirectory(Path.Join(fixture.Path, "profile")).FullName;
        var store = new CleanupRunStore(window.Operations.Files, data);
        var workspace = new CleanupWorkspace(window.Operations, store, DeveloperOpportunityFixture.Find, data);
        host.Content = workspace; workspace.SetSource(scan, findings, 30); WaitForWorkspacePlan(workspace, 30);
        var exactScopes = findings.Select(item => item.Path).ToArray();
        workspace.ReviewSelection(scan, findings, exactScopes); WaitForWorkspacePlan(workspace, 30); window.UpdateLayout();
        Assert.Equal(exactScopes, workspace.Prepared!.Definition.ScopePaths);
        try
        {
            Guid draftId = workspace.Prepared!.Record.Id;
            var historical = workspace.Prepared.Record with { Id = Guid.NewGuid(), State = CleanupRunState.Completed,
                Definition = workspace.Prepared.Definition with { Id = Guid.NewGuid(), Name = "Navigation history fixture" },
                Targets = workspace.Prepared.Record.Targets.Take(1).Select(item => item with { Decision = "Kept", Reason = "Metadata-only history fixture." }).ToArray() };
            store.SaveRun(historical);
            store.SaveDefinition(workspace.Prepared.Definition with { Id = Guid.NewGuid(), Name = "Saved navigation fixture" });
            ClickCleanupState(workspace, "InspectButton");
            var inspector = Assert.IsType<CleanupInspector>(((Border)workspace.FindName("InspectorHost")).Child);
            ((ComboBox)inspector.FindName("GroupPicker")).SelectedIndex = 2;
            var summary = (ListBox)inspector.FindName("SummaryList"); window.UpdateLayout();
            PumpUntil(() => Descendants(summary).OfType<Button>().Any(button => Equals(button.Content, "View folders")), TimeSpan.FromSeconds(5));
            Descendants(summary).OfType<Button>().First(button => Equals(button.Content, "View folders")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TextBox)inspector.FindName("SearchBox")).Text = "project-";
            var folders = (ListBox)inspector.FindName("FolderList"); folders.SelectedIndex = 0;
            string omitted = Assert.IsType<CleanupInspectionEntry>(folders.SelectedItem).Location.Path;
            ((Button)inspector.FindName("IncludeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)inspector.FindName("NextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); folders.SelectedIndex = 30;
            ((TextBox)workspace.FindName("DaysBox")).Text = "030";
            var outer = (ScrollViewer)workspace.FindName("WorkspaceScroll"); outer.UpdateLayout(); outer.ScrollToVerticalOffset(100);
            var inner = Descendants(folders).OfType<ScrollViewer>().First(); inner.ScrollToVerticalOffset(25); window.UpdateLayout();
            var before = inspector.CapturePresentation();
            Assert.True(before.List); Assert.Equal(1, before.Page); Assert.Equal("project-", before.Search); Assert.NotNull(before.Drill);
            Assert.NotNull(before.Undo); Assert.Equal(omitted, Assert.Single(inspector.Entries, entry => entry.CanChoose && !entry.Included).Location.Path);
            double outerOffset = outer.VerticalOffset;
            foreach (string navigation in new[] { "HistoryButton", "SavedButton" })
            {
                ((Button)workspace.FindName(navigation)).Focus(); ClickCleanupState(workspace, navigation);
                var library = (StackPanel)workspace.FindName("LibraryPanel");
                PumpUntil(() => library.Children.Count > 0, TimeSpan.FromSeconds(5));
                if (navigation == "HistoryButton")
                {
                    PumpUntil(() => CleanupStateButton(library, "View run: Navigation history fixture") is not null, TimeSpan.FromSeconds(5));
                    CleanupStateButton(library, "View run: Navigation history fixture")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => ((StackPanel)workspace.FindName("ResultPanel")).Visibility == Visibility.Visible, TimeSpan.FromSeconds(5));
                    ClickCleanupState(workspace, "InspectResultsButton"); Assert.Equal(historical.Id, inspector.CapturePresentation().ContextId);
                }
                ClickCleanupState(workspace, "BackButton");
                PumpUntil(() => workspace.Prepared?.Record.Id == draftId && inspector.CapturePresentation().ContextId == draftId, TimeSpan.FromSeconds(5));
                PumpUntil(() => Math.Abs(inspector.CapturePresentation().ListOffset - before.ListOffset) < .5, TimeSpan.FromSeconds(5));
                var after = inspector.CapturePresentation();
                Assert.Equal(before.List, after.List); Assert.Equal(before.Group, after.Group); Assert.Equal(before.Outcome, after.Outcome);
                Assert.Equal(before.Search, after.Search); Assert.Equal(before.Page, after.Page); Assert.Equal(before.Drill, after.Drill); Assert.Equal(before.Selected, after.Selected);
                Assert.Equal(before.Omitted.Order(StringComparer.Ordinal), after.Omitted.Order(StringComparer.Ordinal));
                Assert.Equal(before.Undo!.Order(StringComparer.Ordinal), after.Undo!.Order(StringComparer.Ordinal));
                Assert.Equal(before.UndoText, after.UndoText); Assert.Equal("030", ((TextBox)workspace.FindName("DaysBox")).Text);
                Assert.InRange(Math.Abs(outer.VerticalOffset - outerOffset), 0, 1);
                Assert.Equal(omitted, Assert.Single(inspector.Entries, entry => entry.CanChoose && !entry.Included).Location.Path);
            }
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-draft-resumed.png"), 1220, 940);
            Assert.All(findings, item => Assert.True(Directory.Exists(item.Path)));
        }
        finally { AwaitCleanupState(workspace.StopAsync()); host.Content = null; }
    }

    private static void ClickCleanupState(CleanupWorkspace workspace, string name)
        => ((Button)workspace.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Button? CleanupStateButton(FrameworkElement parent, string name)
        => Descendants(parent).OfType<Button>().FirstOrDefault(button => AutomationProperties.GetName(button) == name);
    private static void AwaitCleanupState(Task task)
    { PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(15)); task.GetAwaiter().GetResult(); }
}
