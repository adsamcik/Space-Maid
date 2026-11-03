using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckCleanupCapturedReviewIsolation(MainWindow window, ContentControl host, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        var projectA = CleanupRulePlanTests.Project(fixture.Path, "scope-a", 60);
        var projectB = CleanupRulePlanTests.Project(fixture.Path, "scope-b", 60);
        var scanningA = window.Operations.ScanAsync(projectA.Root); AwaitCleanupState(scanningA); var scanA = scanningA.GetAwaiter().GetResult();
        var scanningB = window.Operations.ScanAsync(projectB.Root); AwaitCleanupState(scanningB); var scanB = scanningB.GetAwaiter().GetResult();
        string data = Directory.CreateDirectory(Path.Join(fixture.Path, "profile")).FullName;
        var store = new CleanupRunStore(window.Operations.Files, data);
        var coordinator = new CleanupRunCoordinator(window.Operations, store, DeveloperOpportunityFixture.Find);
        var preparingA = coordinator.PrepareAsync(CleanupRunStoreTests.Definition(projectA.Root) with { Name = "Review A" }, scanA, DeveloperOpportunityFixture.Find(scanA), default);
        AwaitCleanupState(preparingA); var preparedA = preparingA.GetAwaiter().GetResult();
        store.SaveDefinition(preparedA.Definition);
        var pendingA = preparedA.Record with { Source = "Scheduled" }; store.SaveRun(pendingA);
        using var enteredA = new ManualResetEventSlim(); using var releaseA = new ManualResetEventSlim();
        bool holdA = false;
        var workspace = new CleanupWorkspace(window.Operations, store, (scan, token) =>
        {
            if (holdA && scan.RootPath == projectA.Root)
            {
                enteredA.Set();
                if (!releaseA.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Owned review restore gate timed out.");
            }
            return DeveloperOpportunityFixture.Find(scan, token);
        }, data);
        host.Content = workspace; workspace.SetSource(scanB, DeveloperOpportunityFixture.Find(scanB), 30); WaitForWorkspacePlan(workspace, 30);
        var draftB = workspace.Prepared!;
        var opening = typeof(CleanupWorkspace).GetMethod("OpenRecordAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task Open(CleanupRunRecord record) => (Task)opening.Invoke(workspace, [record])!;
        try
        {
            AwaitCleanupState(Open(pendingA)); ClickCleanupState(workspace, "InspectResultsButton");
            var inspector = Assert.IsType<CleanupInspector>(((Border)workspace.FindName("InspectorHost")).Child);
            ((RadioButton)inspector.FindName("ListTab")).IsChecked = true;
            var folders = (ListBox)inspector.FindName("FolderList"); folders.SelectedIndex = 0;
            var exclude = (Button)inspector.FindName("ExcludeButton");
            Assert.Equal(Visibility.Collapsed, exclude.Visibility);
            exclude.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // A stale routed action must not edit the hidden draft.
            Assert.Equal("", ((TextBox)workspace.FindName("ExclusionsBox")).Text);
            Assert.Equal(pendingA.Id, workspace.Prepared!.Record.Id);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-pending-adversarial.png"), 1220, 940);
            var include = (Button)inspector.FindName("IncludeButton");
            Assert.True(include.IsEnabled); include.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(((Button)workspace.FindName("ApprovePendingButton")).IsEnabled);
            include.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(((Button)workspace.FindName("ApprovePendingButton")).IsEnabled);
            Guid? approved = null;
            workspace.ExecuteRun = (chosen, _, _) =>
            {
                approved = chosen.Record.Id;
                var receipts = chosen.Record.Targets.Select(target => new CleanupTargetReceipt(target.Location.Path, target.Location.ProviderId,
                    target.Location.RuleId, CleanupOutcome.Skipped, "Owned non-mutating approval fixture.", false, DateTimeOffset.UtcNow)).ToArray();
                store.SaveRun(chosen.Record with { State = CleanupRunState.Completed, Receipts = receipts });
                return Task.FromResult(new CleanupBatchResult(chosen.Plan.Preview.Locations.Select(location =>
                    new CleanupLocationResult(location, CleanupOutcome.Skipped, "Owned non-mutating approval fixture.")).ToArray(), null));
            };
            ClickCleanupState(workspace, "ApprovePendingButton");
            PumpUntil(() => workspace.RunTask is null && workspace.Result is not null, TimeSpan.FromSeconds(5));
            Assert.Equal(pendingA.Id, approved);

            var slowA = pendingA with { Id = Guid.NewGuid() };
            var pendingB = draftB.Record;
            store.SaveRun(slowA); store.SaveRun(pendingB);
            holdA = true;
            var oldOpen = Open(slowA); PumpUntil(() => enteredA.IsSet, TimeSpan.FromSeconds(5));
            AwaitCleanupState(Open(pendingB));
            releaseA.Set(); AwaitCleanupState(oldOpen);
            Assert.Equal(pendingB.Id, workspace.Prepared!.Record.Id);
            Assert.Contains(projectB.Root, ((TextBlock)workspace.FindName("ResultPolicy")).Text);
            Assert.True(((Button)workspace.FindName("ApprovePendingButton")).IsEnabled);

            enteredA.Reset(); releaseA.Reset();
            var abandonedOpen = Open(slowA); PumpUntil(() => enteredA.IsSet, TimeSpan.FromSeconds(5));
            ClickCleanupState(workspace, "BackButton"); releaseA.Set(); AwaitCleanupState(abandonedOpen);
            Assert.Equal(Visibility.Visible, ((StackPanel)workspace.FindName("PlanPanel")).Visibility);
            Assert.Equal(draftB.Record.Id, workspace.Prepared!.Record.Id);
            Assert.True(((Button)workspace.FindName("CleanupButton")).IsEnabled);
            Assert.Equal("generated output", File.ReadAllText(projectA.File)); Assert.Equal("generated output", File.ReadAllText(projectB.File));
        }
        finally { releaseA.Set(); AwaitCleanupState(workspace.StopAsync()); host.Content = null; }
    }

    private static void CheckCleanupBackgroundRecovery(MainWindow window, ContentControl host)
    {
        using var fixture = new TestDirectory(); _ = CleanupRulePlanTests.Project(fixture.Path, "project", 60);
        var scanning = window.Operations.ScanAsync(fixture.Path); AwaitCleanupState(scanning); var scan = scanning.GetAwaiter().GetResult();
        string data = Directory.CreateDirectory(Path.Join(fixture.Path, "profile")).FullName;
        var store = new CleanupRunStore(window.Operations.Files, data);
        var workspace = new CleanupWorkspace(window.Operations, store, DeveloperOpportunityFixture.Find, data);
        host.Content = workspace; workspace.SetSource(scan, DeveloperOpportunityFixture.Find(scan), 30); WaitForWorkspacePlan(workspace, 30);
        try
        {
            var cleanup = (Button)workspace.FindName("CleanupButton"); Assert.True(cleanup.IsEnabled);
            var active = new CleanupRunRecord { Definition = workspace.Prepared!.Definition, ScanRootPath = fixture.Path, Source = "Scheduled", State = CleanupRunState.Running };
            store.SaveRun(active); AwaitCleanupState(workspace.RefreshStatusAsync());
            PumpUntil(() => !cleanup.IsEnabled, TimeSpan.FromSeconds(5));
            ((TextBox)workspace.FindName("DaysBox")).Text = "31"; WaitForWorkspacePlan(workspace, 31, requireIncluded: false);
            Assert.False(cleanup.IsEnabled);
            store.SaveRun(active with { State = CleanupRunState.Completed }); AwaitCleanupState(workspace.RefreshStatusAsync());
            PumpUntil(() => cleanup.IsEnabled, TimeSpan.FromSeconds(5));
            var dead = workspace.Prepared!.Record with { Id = Guid.NewGuid(), Source = "Scheduled", State = CleanupRunState.Running,
                OwnerProcessId = int.MaxValue, OwnerStartedUtc = DateTimeOffset.UnixEpoch };
            store.SaveRun(dead); var target = dead.Targets[0].Location;
            store.AppendReceipt(dead.Id, new(target.Path, target.ProviderId, target.RuleId, CleanupOutcome.Uncertain, "Lost worker confirmation", true, DateTimeOffset.UtcNow));
            AwaitCleanupState(workspace.RefreshStatusAsync());
            PumpUntil(() => store.LoadRun(dead.Id)!.State == CleanupRunState.NeedsAttention, TimeSpan.FromSeconds(5));
            Assert.Equal(Visibility.Collapsed, ((StackPanel)workspace.FindName("RunningPanel")).Visibility);
            Assert.Equal(CleanupOutcome.Uncertain, Assert.Single(store.LoadRun(dead.Id)!.Receipts).Outcome);
            Assert.True(cleanup.IsEnabled);

            PumpUntil(() => File.Exists(Path.Join(data, "workspace-draft.json")), TimeSpan.FromSeconds(5));
            var ended = workspace.Prepared!.Record with { Id = Guid.NewGuid(), State = CleanupRunState.Running, ApprovalClaimId = Guid.NewGuid() };
            store.SaveRun(ended); store.MarkWorkerEnded(ended.Id, ended.ApprovalClaimId.Value, "Owned worker ended during a profile outage.");
            string unavailable = data + "-unavailable"; Directory.Move(data, unavailable);
            try { AwaitCleanupState(workspace.RefreshStatusAsync()); }
            finally { Directory.Move(unavailable, data); }
            // The visible workspace must retry by itself after storage returns.
            PumpUntil(() => store.LoadRun(ended.Id)!.State == CleanupRunState.Stopped, TimeSpan.FromSeconds(7));
            Assert.True(cleanup.IsEnabled);
        }
        finally { AwaitCleanupState(workspace.StopAsync()); host.Content = null; }
    }
}
