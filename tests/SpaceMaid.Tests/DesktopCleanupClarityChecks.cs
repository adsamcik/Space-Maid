using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckCleanupClarity(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        var old = CleanupRulePlanTests.Project(fixture.Path, "Orbit video editor", 60);
        var second = CleanupRulePlanTests.Project(fixture.Path, "Photo library prototype", 90);
        var recent = CleanupRulePlanTests.Project(fixture.Path, "Current project", 0);
        foreach (var project in new[] { old, second })
        {
            File.WriteAllBytes(project.File, new byte[4 * 1024 * 1024]);
            BuildActivityFixture.AgeProject(project.Root, 60);
        }
        string data = Directory.CreateDirectory(Path.Join(fixture.Path, "profile")).FullName;
        var scanTask = window.Operations.ScanAsync(fixture.Path); AwaitCleanupState(scanTask);
        var scan = scanTask.GetAwaiter().GetResult();
        var findings = DeveloperOpportunityFixture.Find(scan);
        var host = (ContentControl)window.FindName("CleanupWorkspaceContent");
        var browser = (ContentControl)window.FindName("OpportunitiesContent");
        object original = host.Content;
        var workspace = new CleanupWorkspace(window.Operations, new(window.Operations.Files, data), DeveloperOpportunityFixture.Find, data);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        host.Content = workspace; host.Visibility = Visibility.Visible; browser.Visibility = Visibility.Collapsed;
        workspace.SetSource(scan, findings, 30); WaitForWorkspacePlan(workspace, 30);
        var action = (Button)workspace.FindName("CleanupButton");
        var caption = (TextBlock)workspace.FindName("EstimateCaption");
        var estimate = (TextBlock)workspace.FindName("EstimateText");
        var consequence = (TextBlock)workspace.FindName("ConsequenceText");
        var permanent = (TextBlock)workspace.FindName("PermanentText");
        var scroll = (ScrollViewer)workspace.FindName("WorkspaceScroll");
        try
        {
            Assert.Equal(2, workspace.Prepared!.Plan.Preview.Locations.Count);
            Assert.Contains("emptying the Recycle Bin", caption.Text);
            Assert.Equal(SizeText.Format(workspace.Prepared.Plan.Preview.EstimatedBytes), estimate.Text);
            Assert.Contains("Recoverable", consequence.Text);
            Assert.Equal("Recycle 2 folders", action.Content);
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = theme;
                Draw($"overview-{theme}", 1220, 940);
                Draw($"overview-compact-{theme}", 1020, 790);
                AssertFooter();
            }
#pragma warning restore WPF0001
            ClickCleanupState(workspace, "InspectButton");
            var inspector = Assert.IsType<CleanupInspector>(((Border)workspace.FindName("InspectorHost")).Child);
            Assert.True(((RadioButton)inspector.FindName("ListTab")).IsChecked);
            var list = (ListBox)inspector.FindName("FolderList");
            window.Activate(); PumpUntil(() => window.IsActive, TimeSpan.FromSeconds(5));
            window.UpdateLayout();
            var ready = inspector.Entries.First(entry => entry.CanChoose);
            Assert.Contains(Path.GetFileName(Path.GetDirectoryName(ready.Location.Path))!, ready.Name);
            Toggle(ready.Location.Path);
            Assert.Equal("Recycle 1 folder", action.Content);
            Assert.Single(inspector.Entries, entry => entry.CanChoose && !entry.Included);
            Assert.Contains(Descendants(list).OfType<CheckBox>(), check => check.IsKeyboardFocusWithin);
            var undo = Descendants((DockPanel)inspector.FindName("UndoPanel")).OfType<Button>().Single();
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Recycle 2 folders", action.Content);
            inspector.SetBusy(true); window.UpdateLayout();
            Assert.All(Descendants(list).OfType<CheckBox>(), check => Assert.False(check.IsEnabled));
            inspector.SetBusy(false); window.UpdateLayout();
            var kept = inspector.Entries.Single(entry => entry.Location.Path == recent.Output);
            Assert.False(kept.CanChoose);
            Assert.False(RowCheck(kept.Location.Path).IsEnabled);
            ((ComboBox)workspace.FindName("ActionBox")).SelectedIndex = 1;
            WaitForWorkspacePlan(workspace, 30, action: BuildCleanupAction.PermanentlyDelete);
            Assert.True(permanent.IsVisible);
            Assert.Contains("No recovery", permanent.Text);
            Assert.Equal("Delete 2 folders permanently", action.Content);
            scroll.ScrollToEnd(); window.UpdateLayout();
            Draw("review-permanent", 1020, 790); AssertFooter();
            foreach (var entry in inspector.Entries.Where(entry => entry.CanChoose).ToArray()) Toggle(entry.Location.Path);
            Assert.False(action.IsEnabled);
            Assert.Equal("Nothing selected", estimate.Text);
            Assert.False(permanent.IsVisible);
            Assert.Contains("Nothing will be removed", consequence.Text);
            Draw("nothing-selected", 1020, 790);
            Toggle(old.Output);
            Assert.Contains("permanently deleted", ((TextBlock)workspace.FindName("EstimateNotes")).Text);
            Assert.DoesNotContain("remain recoverable", ((TextBlock)workspace.FindName("EstimateNotes")).Text);
            ((ComboBox)inspector.FindName("OutcomePicker")).SelectedIndex = 1;
            Toggle(old.Output);
            Assert.Empty(list.Items);
            Assert.True(undo.IsKeyboardFocusWithin);
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Delete 1 folder permanently", action.Content);
            workspace.SetSource(null, []);
            Assert.Contains("Scan changed", ((TextBlock)workspace.FindName("FreshnessText")).Text);
            Assert.True(((Button)workspace.FindName("UpdateEstimateButton")).IsVisible);
            ((Expander)workspace.FindName("SaveOptions")).IsExpanded = true;
            window.UpdateLayout();
            Assert.True(((Button)workspace.FindName("InspectButton")).IsVisible);
            Assert.True(((Button)workspace.FindName("UpdateEstimateButton")).IsVisible);
            Assert.False(action.IsVisible);
            ((Expander)workspace.FindName("SaveOptions")).IsExpanded = false;
            var pending = workspace.Prepared!.Record;
            workspace.Store.SaveRun(pending);
            var open = (Task)typeof(CleanupWorkspace).GetMethod("OpenRecordAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(workspace, [pending])!;
            AwaitCleanupState(open);
            Assert.Contains("Estimated space", ((TextBlock)workspace.FindName("PendingImpactCaption")).Text);
            Assert.Contains("No recovery", ((TextBlock)workspace.FindName("PendingConsequenceText")).Text);
            Assert.Equal("Review folders", ((Button)workspace.FindName("InspectResultsButton")).Content);
            Draw("saved-review", 1020, 790);
            Assert.All(new[] { old, second, recent }, project => Assert.True(File.Exists(project.File)));

            CheckBox RowCheck(string path)
            {
                var entry = list.Items.Cast<CleanupInspectionEntry>().Single(item => item.Location.Path == path);
                list.ScrollIntoView(entry); window.UpdateLayout();
                return Descendants(list.ItemContainerGenerator.ContainerFromItem(entry)).OfType<CheckBox>().Single();
            }
            void Toggle(string path)
            {
                var check = RowCheck(path);
                Assert.True(check.IsEnabled && check.Focus());
                var peer = new CheckBoxAutomationPeer(check);
                ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
                window.UpdateLayout();
            }
        }
        finally { AwaitCleanupState(workspace.StopAsync()); host.Content = original; }

        void Draw(string name, int width, int height) => Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-clarity-{name}.png"), width, height);
        void AssertFooter()
        {
            var content = (FrameworkElement)window.Content;
            double bottom = action.TranslatePoint(new Point(0, action.ActualHeight), content).Y;
            Assert.True(action.IsVisible && bottom <= content.ActualHeight && action.ActualWidth >= action.DesiredSize.Width - 1);
            var warning = permanent.IsVisible ? permanent : consequence;
            Assert.True(warning.IsVisible && warning.TranslatePoint(new Point(), content).Y < bottom);
        }
    }
}
