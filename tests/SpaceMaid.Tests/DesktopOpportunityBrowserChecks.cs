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
    private static void CheckOverlappingOpportunityExtensions(MainWindow window, Application app, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        var runtime = Assert.IsType<SpaceMaid.App.Extensions.ExtensionRuntime>(typeof(MainWindow)
            .GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var provider = new OverlappingOpportunityProvider(fixture.Path);
        runtime.Opportunities.Register("test.previews", provider);
        string project = Directory.CreateDirectory(Path.Join(fixture.Path, "project")).FullName;
        string bin = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
        File.WriteAllText(Path.Join(project, "sample.csproj"), "<Project />");
        string file = Path.Join(bin, "sample.dll"); File.WriteAllBytes(file, new byte[5000]);
        BuildActivityFixture.AgeProject(project);
        var scanning = new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        PumpUntil(() => scanning.IsCompleted, TimeSpan.FromSeconds(5)); var scan = scanning.GetAwaiter().GetResult();
        var analysis = runtime.Opportunities.Analyze(scan, CancellationToken.None);
        Assert.Equal(2, analysis.Findings.Count);
        Assert.Single(analysis.Findings.Select(item => item.Path).Distinct());
        ((TextBox)window.FindName("OpportunitySearch")).Text = "";
        ((ComboBox)window.FindName("OpportunityTypeFilter")).SelectedIndex = 0;
        var load = window.InitializeExplorerAsync(scan, analysis.Findings, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, analysis.Findings, analysis.Groups, null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        var grouping = (ComboBox)window.FindName("OpportunityGroupingPicker");
        grouping.SelectedIndex = 0;
        var cards = (ItemsControl)window.FindName("CleanupGroupCards");
        var list = (ListBox)window.FindName("OpportunityGrid");
        var groups = cards.Items.Cast<OpportunityGroupRow>().ToArray();
        Assert.Equal(3, groups.Length);
        var developer = Assert.Single(groups, group => group.Group.ProviderId == DeveloperOpportunityFixture.ProviderId);
        var previewGroups = groups.Where(group => group.Group.ProviderId == "test.previews").ToArray();
        Assert.Equal(2, previewGroups.Length);
        Assert.All(groups, group => Assert.Equal(group.Group.ProviderId, Assert.Single(group.Matches).Item.ProviderId));
        Assert.Same(previewGroups[0].Matches[0], previewGroups[1].Matches[0]);
        Assert.NotSame(developer.Matches[0], previewGroups[0].Matches[0]);
        window.UpdateLayout();
        var previewContainer = cards.ItemContainerGenerator.ContainerFromItem(previewGroups[0]);
        Descendants(previewContainer).OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, previewGroups[0]))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(previewGroups[0].Checked); Assert.True(previewGroups[1].Checked); Assert.False(developer.Checked);
        Assert.False(developer.Matches[0].Selected);
        var refresh = window.RecheckFolderAsync(bin);
        PumpUntil(() => refresh.IsCompleted, TimeSpan.FromSeconds(5)); refresh.GetAwaiter().GetResult();
        groups = cards.Items.Cast<OpportunityGroupRow>().ToArray();
        developer = Assert.Single(groups, group => group.Group.ProviderId == DeveloperOpportunityFixture.ProviderId);
        previewGroups = groups.Where(group => group.Group.ProviderId == "test.previews").ToArray();
        Assert.Equal(2, previewGroups.Length);
        Assert.True(previewGroups[0].Checked); Assert.True(previewGroups[1].Checked); Assert.False(developer.Checked);
        Assert.False(developer.Matches[0].Selected); // Same path never transfers consent between providers.
        window.UpdateLayout();
        previewContainer = cards.ItemContainerGenerator.ContainerFromItem(previewGroups[0]);
        Descendants(previewContainer).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, previewGroups[0]))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("test.previews", Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>())).Item.ProviderId);
        ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        grouping.SelectedIndex = 1;
        var navigation = (ListBox)window.FindName("OpportunityGroupList");
        Assert.Equal(3, navigation.Items.Count); // One developer project plus two overlapping extension project groups.
        foreach (var group in navigation.Items.Cast<OpportunityGroupRow>().ToArray())
        {
            navigation.SelectedItem = group;
            var row = Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>()));
            Assert.Equal(group.Group.ProviderId, row.Item.ProviderId);
            Assert.Same(row, Assert.Single(group.Matches));
            Assert.False(row.Selected);
        }
        grouping.SelectedIndex = 0;
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-extension-overlap.png"), 1220, 910);
        grouping.SelectedIndex = 1;
        ((Button)window.FindName("AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, list.Items.Count);
        foreach (var row in list.Items.Cast<OpportunityRow>()) row.Selected = true;
        provider.Enabled = false; // Only this provider's rule disappears during fresh review.
        Exception? failure = null; bool reviewed = false;
        var closeReview = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            var dialog = app.Windows.OfType<ReviewWindow>().FirstOrDefault(candidate => candidate.IsVisible);
            if (dialog is null) return;
            try
            {
                reviewed = true;
                var reviewedLocation = Assert.IsType<CleanupReviewRow>(Assert.Single(((ListBox)dialog.FindName("LocationList")).Items.Cast<object>()));
                Assert.Equal(DeveloperOpportunityFixture.ProviderId, reviewedLocation.Location.ProviderId);
                Assert.Equal(bin, reviewedLocation.Path);
            }
            catch (Exception error) { failure = error; }
            finally { dialog.Close(); }
        }, Dispatcher.CurrentDispatcher);
        try
        {
            var review = window.ReviewSelectionAsync();
            PumpUntil(() => review.IsCompleted, TimeSpan.FromSeconds(10)); review.GetAwaiter().GetResult();
            if (failure is not null) throw failure;
            Assert.True(reviewed);
            var retained = Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>()));
            Assert.Equal(DeveloperOpportunityFixture.ProviderId, retained.Item.ProviderId);
            Assert.True(retained.Selected); // Excluding another provider at this path must retain this valid selection.
            Assert.Equal(5000, new FileInfo(file).Length);
        }
        finally { closeReview.Stop(); }
    }

    private sealed class OverlappingOpportunityProvider(string rootPath) : IOpportunityExtension
    {
        internal bool Enabled { get; set; } = true;
        public OpportunityAnalysis Analyze(IOpportunityInventory inventory, CancellationToken token)
        {
            if (!Enabled || inventory.RootPath != rootPath) return new([], new([], []));
            int directory = Enumerable.Range(0, inventory.FileCount).Single(index => inventory.IsDirectory(index)
                && inventory.PrimaryLink(index) is >= 0 and var link && inventory.Name(link).SequenceEqual("bin"));
            var finding = new Opportunity(directory, "Generated previews", "Preview cache", inventory.PathForFile(directory)!,
                inventory.LogicalSize(directory), inventory.AllocatedSize(directory), "Generated fixture previews", "Review custom files", "Recycle", true)
            { RuleId = "previews" };
            OpportunityGroup Group(string key, string name) => new(key, name, inventory.RootPath, "Review", [finding], finding.AllocatedBytes);
            return new([finding], new([Group("project:first", "Preview project"), Group("project:second", "Media project")], [],
                Cleanup: [Group("cleanup:previews", "Preview cache"), Group("cleanup:review", "Previews to review")]));
        }
    }

    private static void CheckOpportunityBrowser(MainWindow window, Application app, string artifactRoot)
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        ulong id = 2;
        var folders = new List<(int Index, string Title, string Type)>();
        for (int project = 0; project < 200; project++)
        {
            var repo = Folder(1, project == 0 ? "Chatty-NPC" : project == 1 ? "good-shit" : $"Project-{project:D3}");
            Folder(repo.Identity, ".git");
            for (int item = 0; item < 20; item++)
            {
                var module = Folder(repo.Identity, $"module-{item:D2}");
                string name = item == 8 ? "dist" : item == 12 ? "__pycache__" : (item % 4) switch { 0 => "obj", 1 => "node_modules", 2 => ".venv", _ => "checkout" };
                if (item % 4 == 0 && item != 12)
                {
                    int manifest = builder.AddFile(new(new(id++), FileAttributes.Normal, 0, 0, 0));
                    builder.AddLink(manifest, new(module.Identity), item == 8 ? "package.json" : item == 16 ? "app.fsproj" : "app.csproj");
                }
                var folder = Folder(module.Identity, name);
                int file = builder.AddFile(new(new(id++), FileAttributes.Normal, (200 - project) * 256L * 1024, (200 - project) * 256L * 1024, 0));
                builder.AddLink(file, new(folder.Identity), item == 12 ? "data.pyc" : "data.bin");
                if (item % 4 == 3)
                { int marker = builder.AddFile(new(new(id++), FileAttributes.Normal, 0, 0, 0)); builder.AddLink(marker, new(folder.Identity), ".git"); }
                folders.Add((folder.Index, item == 8 ? "Possible Node build output" : item == 12 ? "Python bytecode cache" : (item % 4) switch { 0 => ".NET build output", 1 => "Node dependencies", 2 => "Python virtual environment", _ => "Linked Git checkout" },
                    (item % 4) switch { 0 => "Build output", 1 => "Dependencies", 2 => "Python environments", _ => "Git worktrees" }));
            }
        }
        var scan = builder.Build(@"G:\Github", "Illustrative inventory", TimeSpan.Zero, 0, default);
        var locations = folders.Select(folder => OpportunityGroupingTests.Finding(scan, folder.Index, folder.Type) with { Title = folder.Title }).ToArray();
        var groups = OpportunityGrouping.Build(scan, locations);
        var load = window.InitializeExplorerAsync(scan, locations, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(10)); load.GetAwaiter().GetResult();
        var publish = typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!;
        publish.Invoke(window, [scan, locations, groups, null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        var list = (ListBox)window.FindName("OpportunityGrid");
        var navigation = (ListBox)window.FindName("OpportunityGroupList");
        var search = (TextBox)window.FindName("OpportunitySearch");
        var grouping = (ComboBox)window.FindName("OpportunityGroupingPicker");
        var filter = (ComboBox)window.FindName("OpportunityTypeFilter");
        var selection = (TextBlock)window.FindName("SelectionText");
        grouping.SelectedIndex = 0;
        Assert.Equal(Visibility.Visible, ((Border)window.FindName("CleanupOverview")).Visibility);
        var cards = (ItemsControl)window.FindName("CleanupGroupCards");
        Assert.Equal(4, cards.Items.Count); Assert.Empty(list.Items);
        Assert.Equal(100, cards.Items.Cast<OpportunityGroupRow>().Max(group => group.RelativeSize));
        Assert.All(cards.Items.Cast<OpportunityGroupRow>(), group => Assert.InRange(group.RelativeSize, 0, 100));
        Assert.False(cards.Items.Cast<OpportunityGroupRow>().Single(group => group.Name == "Other worktrees").CanSelectGroup);
        Assert.Equal(1000, cards.Items.Cast<OpportunityGroupRow>().Single(group => group.Name == "Build files").Matches.Count);
        window.UpdateLayout();
        var firstCard = cards.ItemContainerGenerator.ContainerFromIndex(0);
        var groupCheck = Descendants(firstCard).OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, cards.Items[0]));
        groupCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("1 group selected · 1,000 folders", selection.Text);
        Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
        Descendants(firstCard).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, cards.Items[0])).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1000, list.Items.Count); Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
        Assert.IsType<OpportunityRow>(list.Items[0]).Selected = false;
        Assert.Equal(Visibility.Visible, ((Button)window.FindName("BackToCleanupButton")).Visibility);
        ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(list.Items); Assert.Equal("1 group selected · 999 folders", selection.Text);
        Assert.Null(cards.Items.Cast<OpportunityGroupRow>().First().Checked);
        window.UpdateLayout();
        Descendants(cards.ItemContainerGenerator.ContainerFromIndex(0)).OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, cards.Items[0])).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("1 group selected · 1,000 folders", selection.Text);
        ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((TextBlock)window.FindName("ScannedSizeText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("ScannedSizeCaption")).Text = "On disk";
        ((TextBlock)window.FindName("CandidateText")).Text = SizeText.Format(CleanupPlanner.Create(scan, locations).EstimatedBytes);
        ((TextBlock)window.FindName("CandidateCaption")).Text = "Estimate · 4 groups";
        ((TextBlock)window.FindName("StatusText")).Text = "Illustrative inventory · 200 projects, 4,000 locations";
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            string theme = mode == ThemeMode.Light ? "light" : "dark";
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-groups-{theme}.png"), 1220, 910);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-groups-compact-{theme}.png"), 1020, 790);
        }
#pragma warning restore WPF0001
        CheckBuildClassification();
        CheckWorktreeFilters();
        grouping.SelectedIndex = 1;
        navigation.SelectedIndex = 0;
        Assert.Equal(200, navigation.Items.Count); Assert.Equal(20, list.Items.Count);
        Assert.Equal("Chatty-NPC", ((TextBlock)window.FindName("OpportunityHeading")).Text);
        Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.DoesNotContain(@"G:\", row.DisplayPath));
        ((Button)window.FindName("SelectListedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("20 locations selected", selection.Text);
        search.Text = "good-shit";
        PumpUntil(() => list.Items.Count == 20 && ((OpportunityRow)list.Items[0]).GroupName == "good-shit", TimeSpan.FromSeconds(3));
        Assert.Single(navigation.Items.Cast<object>()); Assert.Contains("20 outside this view", selection.Text);
        navigation.SelectedIndex = 0;
        ((Button)window.FindName("SelectListedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        grouping.SelectedIndex = 2; filter.SelectedItem = "Build output"; navigation.SelectedIndex = 0;
        Assert.Equal(5, list.Items.Count); Assert.Contains("35 outside this view", selection.Text);
        var selected = locations.Where(location => location.Path.Contains("Chatty-NPC", StringComparison.Ordinal) || location.Path.Contains("good-shit", StringComparison.Ordinal))
            .Select(location => (location.ProviderId, location.Path, location.RuleId)).ToHashSet();
        publish.Invoke(window, [scan, locations, groups, selected, ((OpportunityRow)list.SelectedItem).Path, true]);
        Assert.Equal("Build output", ((TextBlock)window.FindName("OpportunityHeading")).Text);
        Assert.Equal("good-shit", search.Text); Assert.Equal("Build output", filter.SelectedItem);
        Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
        search.Text = "no-such-location";
        PumpUntil(() => list.Items.Count == 0, TimeSpan.FromSeconds(3));
        Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("EmptyState")).Visibility);
        Assert.Contains("40 outside this view", selection.Text);
        // This inventory is illustrative and must never be rechecked on disk.
        var preview = new ReviewWindow(CleanupPlanner.Create(scan, locations.Where(location => selected.Contains((location.ProviderId, location.Path, location.RuleId)))));
        Assert.Equal(40, ((ListBox)preview.FindName("GroupList")).Items.Cast<CleanupReviewGroup>().Single(group => group.Key == "all").Rows.Count);
        Assert.InRange(((ListBox)preview.FindName("LocationList")).Items.Count, 0, 40);
        Assert.False(Descendants(preview.Content as DependencyObject).OfType<Button>().Single(button => Equals(button.Content, "Move to Recycle Bin")).IsEnabled);
        preview.Close();
        search.Text = ""; filter.SelectedIndex = 0; grouping.SelectedIndex = 1;
        ((Button)window.FindName("AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(4000, list.Items.Count);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "opportunity-groups-all.png"), 1220, 910);
        Assert.InRange(Descendants(list).OfType<ListBoxItem>().Count(), 1, 80);
        Assert.InRange(Descendants(navigation).OfType<ListBoxItem>().Count(), 1, 40);
        navigation.SelectedItem = navigation.Items.Cast<OpportunityGroupRow>().Single(group => group.Name == "Chatty-NPC");
        ((TextBlock)window.FindName("ScannedSizeText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("CandidateText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("StatusText")).Text = "Illustrative inventory · 200 projects, 4,000 locations";
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            string theme = mode == ThemeMode.Light ? "light" : "dark";
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"opportunity-groups-{theme}.png"), 1220, 910);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"opportunity-groups-compact-{theme}.png"), 1020, 790);
        }
#pragma warning restore WPF0001
        window.MarkScanStale("Illustrative out-of-date results; recheck or rescan.");
        Assert.Equal("Chatty-NPC", ((TextBlock)window.FindName("OpportunityHeading")).Text);
        Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("CleanupFreshnessText")).Visibility);
        Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
        Assert.True(((Button)window.FindName("SelectListedButton")).IsEnabled);
        window.UpdateLayout();
        double listHeight = list.ActualHeight;
        string fullWarning = "Could not recycle the folder: The recheck was incomplete. "
            + string.Join(" ", Enumerable.Repeat(@"Junction target not followed: G:\$RECYCLE.BIN\fixture\.cache.", 25))
            + " Results may be out of date.";
        window.MarkScanStale(fullWarning); window.UpdateLayout();
        Assert.Equal(listHeight, list.ActualHeight, 2); // Full diagnostics cannot squeeze the default list.
        Assert.InRange(((TextBlock)window.FindName("StatusText")).ActualHeight, 1, 20);
        var diagnosticsToggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("DiagnosticsToggle");
        Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("DiagnosticsPanel")).Visibility);
        diagnosticsToggle.IsChecked = true; window.UpdateLayout();
        var fullMessage = (TextBox)window.FindName("DiagnosticMessage");
        Assert.True(fullMessage.IsVisible); Assert.True(fullMessage.IsReadOnly); Assert.Equal(fullWarning, fullMessage.Text);
        Assert.InRange(fullMessage.ActualHeight, 1, 160);
        diagnosticsToggle.IsChecked = false; window.UpdateLayout();
        Assert.Equal(listHeight, list.ActualHeight, 2);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "opportunity-groups-stale-dark.png"), 1220, 910);
        ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.DoesNotContain("selected", selection.Text);

        void CheckBuildClassification()
        {
            var build = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == "Build files");
            Assert.Equal(4, build.Children.Count);
            var csharp = Assert.Single(build.Children, row => row.Name == "C# / .NET");
            Assert.Equal(400, csharp.Matches.Count); Assert.Equal("200 projects", csharp.ProjectCount);
            Assert.Equal(1000, build.Children.Sum(child => child.Matches.Count));
            window.UpdateLayout();
            var container = cards.ItemContainerGenerator.ContainerFromItem(build);
            Descendants(container).OfType<Expander>().Single(expander => expander.Name == "CleanupBreakdownExpander").IsExpanded = true; window.UpdateLayout();
            var checkbox = Descendants(container).OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, csharp));
            checkbox.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(csharp.Checked); Assert.Null(build.Checked);
            Assert.Equal("1 group selected · 400 folders", selection.Text);
            Descendants(container).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, csharp)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Build files › C# / .NET", ((TextBlock)window.FindName("CleanupScopeText")).Text);
            Assert.Equal(200, navigation.Items.Count); Assert.Equal(2, list.Items.Count);
            Assert.Equal("Chatty-NPC", ((TextBlock)window.FindName("OpportunityHeading")).Text);
            ((Button)window.FindName("AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(400, list.Items.Count);
            Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.Equal(".NET build output", row.Title));
            navigation.SelectedItem = navigation.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == "Chatty-NPC");
            Assert.Equal(2, list.Items.Count); Assert.Contains("398 outside this view", selection.Text);
            Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
            // Retain language/project scope and existing selections through refreshed metadata publication.
            var chosen = csharp.Matches.Select(row => row.SelectionIdentity).ToHashSet();
            publish.Invoke(window, [scan, locations, groups, chosen, ((OpportunityRow)list.SelectedItem).Path, true]);
            Assert.Equal(2, list.Items.Count); Assert.Equal("Chatty-NPC", ((TextBlock)window.FindName("OpportunityHeading")).Text);
            Assert.Equal("Build files › C# / .NET", ((TextBlock)window.FindName("CleanupScopeText")).Text);
            var withoutProject = locations.Where(item => !item.Path.Contains("Chatty-NPC", StringComparison.Ordinal) || !chosen.Contains((item.ProviderId, item.Path, item.RuleId))).ToArray();
            publish.Invoke(window, [scan, withoutProject, OpportunityGrouping.Build(scan, withoutProject), chosen, null, true]);
            Assert.Empty(list.Items); Assert.Equal(199, navigation.Items.Count);
            Assert.Equal("Chatty-NPC", ((TextBlock)window.FindName("OpportunityHeading")).Text);
            Assert.Contains("No matching folders remain", ((TextBlock)window.FindName("OpportunityResultSummary")).Text);
            Assert.False(((Button)window.FindName("SelectListedButton")).IsEnabled);
            publish.Invoke(window, [scan, locations, groups, chosen, null, true]);
            Assert.Equal(2, list.Items.Count); // Restoring the project keeps the same scope.
            search.Text = "good-shit";
            PumpUntil(() => list.Items.Count == 2 && ((OpportunityRow)list.Items[0]).GroupName == "good-shit", TimeSpan.FromSeconds(3));
            Assert.Single(navigation.Items.Cast<object>());
            navigation.SelectedIndex = 0;
#pragma warning disable WPF0001
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = mode;
                string theme = mode == ThemeMode.Light ? "light" : "dark";
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"build-projects-{theme}.png"), 1220, 910);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"build-projects-compact-{theme}.png"), 1020, 790);
            }
#pragma warning restore WPF0001
            search.Text = "node_modules";
            PumpUntil(() => list.Items.Count == 0, TimeSpan.FromSeconds(3)); // Never escape the selected build-language scope.
            ((Button)window.FindName("SelectListedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("400 outside this view", selection.Text);
            var remaining = locations.Where(item => !chosen.Contains((item.ProviderId, item.Path, item.RuleId))).ToArray();
            publish.Invoke(window, [scan, remaining, OpportunityGrouping.Build(scan, remaining), chosen, null, true]);
            Assert.Empty(navigation.Items); Assert.Empty(list.Items);
            Assert.Equal("Build files › C# / .NET", ((TextBlock)window.FindName("CleanupScopeText")).Text);
            ((Button)window.FindName("AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(list.Items); // A disappeared classification does not expose unrelated findings.
            publish.Invoke(window, [scan, locations, groups, chosen, null, true]);
            ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("CleanupScopeText")).Visibility);
            build = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == "Build files");
            Assert.Null(build.Checked); Assert.True(build.Children.Single(row => row.Name == "C# / .NET").Checked);
            ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.All(build.Children, child => Assert.False(child.Checked));
        }

        void CheckWorktreeFilters()
        {
            // Cached facts exercise rule changes without running Git against this illustrative inventory.
            var facts = (Dictionary<string, GitWorktreeAssessment>)typeof(MainWindow)
                .GetField("_worktreeAssessments", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var worktrees = locations.Where(item => item.Category == "Git worktrees").Take(4).ToArray();
            var now = DateTime.UtcNow;
            AddFact(0, 75, integrated: true); AddFact(1, 45, integrated: false);
            AddFact(2, 10, integrated: true); AddFact(3, 100, integrated: true, clean: false);
            var rebuild = typeof(MainWindow).GetMethod("RebuildWorktreeGroups", BindingFlags.Instance | BindingFlags.NonPublic)!;
            rebuild.Invoke(window, null);
            typeof(MainWindow).GetMethod("ApplyOpportunityFilters", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null]);
            var worktreeOptions = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("WorktreeOptionsToggle");
            var viewOptions = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("ViewOptionsToggle");
            Assert.True(worktreeOptions.IsVisible);
            viewOptions.IsChecked = true; viewOptions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            worktreeOptions.IsChecked = true; worktreeOptions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Assert.Equal(Visibility.Visible, ((Border)window.FindName("WorktreeCleanupControls")).Visibility);
            Assert.False(viewOptions.IsChecked);
            Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("ViewOptionsPanel")).Visibility);
            var rule = (ComboBox)window.FindName("WorktreeRulePicker");
            var slider = (Slider)window.FindName("WorktreeAgeSlider");
            var threshold = (TextBlock)window.FindName("WorktreeAgeText");
            var summary = (TextBlock)window.FindName("WorktreeCheckSummary");
            var selectMatches = (Button)window.FindName("SelectWorktreeMatchesButton");
            Assert.Contains("1 worktree matches", summary.Text);
            Assert.Single(MatchingGroup().Matches);
            Assert.True(selectMatches.IsEnabled);
            selectMatches.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var capture = typeof(MainWindow).GetMethod("SelectedCheckedWorktrees", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var firstCapture = Capture([worktrees[0]]);
            Assert.Equal(WorktreeCleanupMode.OldAndIntegrated, firstCapture[worktrees[0].Path].Mode);

            rule.SelectedIndex = 1; slider.Value = 35;
            Assert.Equal("35 days", threshold.Text); Assert.True(slider.IsEnabled);
            Assert.Equal(2, MatchingGroup().Matches.Count);
            Assert.Single(MatchingGroup().Matches, row => row.Selected); // Never select new matches automatically.
            Assert.Equal(WorktreeCleanupMode.Old, Capture([worktrees[0]])[worktrees[0].Path].Mode);
            Assert.Equal(WorktreeCleanupMode.OldAndIntegrated, firstCapture[worktrees[0].Path].Mode); // Immutable review capture.
            slider.Value = 90;
            Assert.DoesNotContain(cards.Items.Cast<OpportunityGroupRow>(), row => row.Group.Key == "cleanup:old-worktrees");
            Assert.False(selectMatches.IsEnabled);
            Assert.DoesNotContain("selected", selection.Text);
            rule.SelectedIndex = 2;
            Assert.False(slider.IsEnabled); Assert.Equal("Any age", threshold.Text);
            Assert.Equal(2, MatchingGroup().Matches.Count);
            Assert.All(MatchingGroup().Matches, row => Assert.False(row.Selected));
            Assert.Contains("2 worktrees match", summary.Text);
            Assert.DoesNotContain(MatchingGroup().Matches, row => row.Path == worktrees[3].Path); // Local changes block every rule.
            selectMatches.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.All(MatchingGroup().Matches, row => Assert.True(row.Selected));
            var retained = Capture([worktrees[0], worktrees[2]]);
            var selectedWorktrees = locations.Where(item => retained.ContainsKey(item.Path))
                .Select(item => (item.ProviderId, item.Path, item.RuleId)).ToHashSet();
            publish.Invoke(window, [scan, locations, groups, selectedWorktrees, worktrees[0].Path, true]);
            Assert.Empty(facts); // Refresh invalidates displayed checks, while selected paths retain their guard policy.
            Assert.Equal(2, Capture([worktrees[0], worktrees[2]]).Count);
            Assert.All(Capture([worktrees[0], worktrees[2]]).Values, policy => Assert.Equal(WorktreeCleanupMode.Integrated, policy.Mode));
            Assert.All(retained.Values, policy => Assert.Equal(WorktreeCleanupMode.Integrated, policy.Mode));

            foreach (var index in new[] { 0, 1, 2, 3 })
                AddFact(index, index switch { 0 => 75, 1 => 45, 2 => 10, _ => 100 }, index != 1, index != 3);
            rule.SelectedIndex = 1; slider.Value = 35;
            Assert.DoesNotContain(MatchingGroup().Matches, row => row.Selected);
            selectMatches.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("1 group selected · 2 folders", selection.Text);
            worktreeOptions.IsChecked = false;
            Assert.True(((Button)window.FindName("ReviewButton")).IsEnabled);
            ((TextBlock)window.FindName("StatusText")).Text = "Illustrative worktree checks · No Git commands or cleanup run";
#pragma warning disable WPF0001
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = mode;
                string theme = mode == ThemeMode.Light ? "light" : "dark";
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"worktree-cleanup-{theme}.png"), 1220, 910);
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"worktree-cleanup-compact-{theme}.png"), 1020, 790);
            }
#pragma warning restore WPF0001
            ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            rule.SelectedIndex = 0; slider.Value = 30;
            publish.Invoke(window, [scan, locations, groups, null, null, false]);
            Assert.Equal(4, cards.Items.Count);

            OpportunityGroupRow MatchingGroup() => cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Group.Key == "cleanup:old-worktrees");
            IReadOnlyDictionary<string, WorktreeCleanupPolicy> Capture(Opportunity[] chosen) =>
                (IReadOnlyDictionary<string, WorktreeCleanupPolicy>)capture.Invoke(window, [chosen])!;
            void AddFact(int index, int days, bool integrated, bool clean = true)
            {
                var activity = now.AddDays(-days);
                facts[worktrees[index].Path] = new(worktrees[index].Path, clean && integrated && days >= 30, "Fixture", "Illustrative facts",
                    LastObservedModificationUtc: activity, LastReflogActivityUtc: activity,
                    VerifiedClean: clean, Integrated: integrated, HasActivityEvidence: true);
            }
        }

        (int Index, ulong Identity) Folder(ulong parent, string name)
        { ulong identity = id++; int index = builder.AddFile(new(new(identity), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(index, new(parent), name); return (index, identity); }
    }

    private static void CheckAdditionalCleanupCategories(MainWindow window, Application app, string artifactRoot)
    {
        var builder = new ScanBuilder(); ulong next = 2;
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        var items = new List<(int Index, string Title, string Category)>();
        for (int project = 0; project < 2; project++)
        {
            ulong repo = next++; Folder(repo, 1, project == 0 ? "photo-tools" : "web-client"); Folder(next++, repo, ".git");
            Add(repo, ".pytest_cache", "pytest cache", "Tool caches", 1.4);
            Add(repo, ".ruff_cache", "Ruff cache", "Tool caches", 0.8);
            Add(repo, "test-results", "Playwright test results", "Test artifacts", 0.9);
            Add(repo, "playwright-report", "Playwright HTML report", "Test artifacts", 0.4);
            if (project != 0) continue;
            foreach (string title in new[] { "Cargo", "Maven", "Gradle", "CMake", "SvelteKit", "Nuxt" })
                Add(repo, title + "-output", title + " build output", "Build output", 0.6);
            Add(repo, "TestResults", ".NET test results", "Test artifacts", 0.2);
            Add(repo, ".vs", "Visual Studio workspace data", "Editor workspace data", 0.5);
            Add(repo, "dists", "Gradle wrapper downloads", "Downloaded tooling", 0.8);
            Add(repo, "uv-cache", "uv cache", "Package caches", 0.3);
        }
        var scan = builder.Build(@"G:\Github", "Illustrative cleanup categories", TimeSpan.Zero, 0, default);
        var locations = items.Select(item => OpportunityGroupingTests.Finding(scan, item.Index, item.Category) with { Title = item.Title }).ToArray();
        var groups = OpportunityGrouping.Build(scan, locations);
        var load = window.InitializeExplorerAsync(scan, locations, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        var publish = typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex = 0;
        publish.Invoke(window, [scan, locations, groups, null, null, false]);
        ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var cards = (ItemsControl)window.FindName("CleanupGroupCards");
        var list = (ListBox)window.FindName("OpportunityGrid");
        var navigation = (ListBox)window.FindName("OpportunityGroupList");
        var scope = (TextBlock)window.FindName("CleanupScopeText");
        Assert.Equal(6, cards.Items.Count); Assert.Empty(list.Items);
        Assert.Contains(cards.Items.Cast<OpportunityGroupRow>(), row => row.Name == "Downloaded tooling");
        Assert.Contains(cards.Items.Cast<OpportunityGroupRow>(), row => row.Name == "Editor workspace data");
        window.UpdateLayout();
        foreach (string name in new[] { "Tool caches", "Test results & reports" })
        {
            var root = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == name);
            Assert.Equal(2, root.Children.Count); Assert.Equal("By tool and project", root.BreakdownHeading);
            var container = cards.ItemContainerGenerator.ContainerFromItem(root);
            Assert.False(Assert.Single(Descendants(container).OfType<Expander>()).IsExpanded);
        }
        ((TextBlock)window.FindName("StatusText")).Text = "Illustrative inventory · Additional cleanup categories";
        ((TextBlock)window.FindName("ScannedSizeText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("ScannedSizeCaption")).Text = "On disk";
        ((TextBlock)window.FindName("CandidateText")).Text = SizeText.Format(CleanupPlanner.Create(scan, locations).EstimatedBytes);
        ((TextBlock)window.FindName("CandidateCaption")).Text = "Estimate · 6 groups";
        // Every breakdown starts collapsed so the top-level checklist remains short.
        var build = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == "Build files");
        Assert.Equal(5, build.Children.Count);
        Assert.False(Descendants(cards.ItemContainerGenerator.ContainerFromItem(build)).OfType<Expander>().Single(expander => expander.Name == "CleanupBreakdownExpander").IsExpanded);
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-categories-{(mode == ThemeMode.Light ? "light" : "dark")}.png"), 1220, 910);
        }
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-categories-compact-dark.png"), 1020, 790);
#pragma warning restore WPF0001
        foreach (var (name, tool) in new[] { ("Tool caches", "pytest"), ("Test results & reports", "Playwright") })
        {
            window.UpdateLayout();
            var root = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == name);
            var child = root.Children.Single(row => row.Name == tool);
            var container = cards.ItemContainerGenerator.ContainerFromItem(root);
            Assert.Single(Descendants(container).OfType<Expander>()).IsExpanded = true; window.UpdateLayout();
            Descendants(container).OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, child)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(child.Checked); Assert.Null(root.Checked);
            Descendants(container).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, child)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(name + " › " + tool, scope.Text); Assert.Equal(2, navigation.Items.Count);
            int perProject = tool == "Playwright" ? 2 : 1;
            Assert.Equal(perProject, list.Items.Count); Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
            var selected = child.Matches.Select(row => row.SelectionIdentity).ToHashSet();
            string projectPath = ((OpportunityGroupRow)navigation.SelectedItem).Path;
            var remaining = locations.Where(item => !selected.Contains((item.ProviderId, item.Path, item.RuleId)) || !item.Path.StartsWith(projectPath + "\\", StringComparison.Ordinal)).ToArray();
            publish.Invoke(window, [scan, remaining, OpportunityGrouping.Build(scan, remaining), selected, null, true]);
            Assert.Empty(list.Items); Assert.Single(navigation.Items.Cast<object>());
            Assert.Contains("No matching folders remain", ((TextBlock)window.FindName("OpportunityResultSummary")).Text);
            publish.Invoke(window, [scan, locations, groups, selected, null, true]);
            Assert.Equal(perProject, list.Items.Count);
            ((Button)window.FindName("AllOpportunitiesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(perProject * 2, list.Items.Count);
            if (tool == "Playwright") Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-test-projects-dark.png"), 1220, 910);
            ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex = 1;

        int Folder(ulong id, ulong parent, string name)
        { int file = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(file, new(parent), name); return file; }
        void Add(ulong parent, string name, string title, string category, double gib)
        {
            ulong id = next++; int folder = Folder(id, parent, name);
            int data = builder.AddFile(new(new(next++), FileAttributes.Normal, GiB(gib), GiB(gib), 0)); builder.AddLink(data, new(id), "data");
            items.Add((folder, title, category));
        }
    }
}
