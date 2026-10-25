using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckBuildContents(MainWindow window, Application app, string artifactRoot)
    {
        var builder = new ScanBuilder(); ulong next = 2;
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        ulong rust = Project("planet-renderer", "Cargo.toml");
        ulong target = Folder(rust, "target"); File(target, ".rustc_info.json", 100);
        File(Folder(target, "debug"), "renderer.pdb", GiB(72));
        File(Folder(target, "release"), "renderer.exe", GiB(16));
        for (int i = 1; i <= 10; i++) File(target, $"custom-file-{i:D2}.txt", i * 100);
        ulong gradle = Project("night-sky-android", "build.gradle.kts");
        ulong build = Folder(gradle, "build");
        File(Folder(build, "intermediates"), "compiled.bin", GiB(12));
        File(Folder(build, "outputs"), "app.apk", GiB(3));
        ulong dotnet = Project("Space-Maid", "SpaceMaid.csproj");
        File(Folder(Folder(dotnet, "bin"), "Release"), "SpaceMaid.dll", GiB(8));
        File(Folder(Folder(dotnet, "obj"), "Release"), "SpaceMaid.dll", GiB(2));
        ulong web = Project("observatory-web", "package.json");
        File(Folder(web, "dist"), "index.js", GiB(1));
        File(Folder(Folder(web, ".next"), "cache"), "data.bin", GiB(2));
        File(web, "svelte.config.js", 100); File(web, "nuxt.config.ts", 100);
        File(Folder(web, ".svelte-kit"), "generated.js", 4096);
        File(Folder(web, ".nuxt"), "generated.js", 8192);
        File(Folder(Project("orbit-analysis", "pyproject.toml"), "__pycache__"), "orbit.cpython-314.pyc", 1024 * 1024);
        File(Folder(Folder(Project("star-index", "pom.xml"), "target"), "classes"), "Star.class", GiB(1));
        ulong cmake = Project("telescope-driver", "CMakeLists.txt");
        ulong cmakeBuild = Folder(cmake, "build-release"); File(cmakeBuild, "CMakeCache.txt", 4096);
        File(Folder(cmakeBuild, "CMakeFiles"), "driver.obj", GiB(1));
        File(Folder(Project("ephemeris", "ephemeris.fsproj"), "obj"), "ephemeris.dll", 4096);
        File(Folder(Project("legacy-tools", "tools.vbproj"), "bin"), "tools.dll", 4096);

        var scan = builder.Build(@"G:\Code", "Illustrative build inventory", TimeSpan.Zero, 0, default);
        var locations = DeveloperOpportunityFixture.Find(scan);
        var groups = OpportunityGrouping.Build(scan, locations);
        var load = window.InitializeExplorerAsync(scan, locations, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(10)); load.GetAwaiter().GetResult();
        ((ComboBox)window.FindName("OpportunityGroupingPicker")).SelectedIndex = 0;
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, locations, groups, null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        ((TextBlock)window.FindName("StatusText")).Text = "Illustrative build inventory · No cleanup run";
        ((TextBlock)window.FindName("ScannedSizeText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("CandidateText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("CandidateCaption")).Text = "Estimate · Build files";
        var cards = (ItemsControl)window.FindName("CleanupGroupCards");
        var root = Assert.Single(cards.Items.Cast<OpportunityGroupRow>());
        Assert.Equal(9, root.Children.Count);
        Assert.All(root.Children, row => Assert.True(row.HasContentGroups));
        var csharp = root.Children.Single(row => row.Name == "C# / .NET");
        Assert.Equal(new[] { "bin/", "obj/" }, csharp.ContentGroups.Select(row => row.Name));
        Assert.Equal(4, root.Children.Single(row => row.Name == "JavaScript / TypeScript").ContentGroups.Count);
        window.UpdateLayout();
        DependencyObject Container() => cards.ItemContainerGenerator.ContainerFromIndex(0);
        Descendants(Container()).OfType<Expander>().Single(expander => expander.Name == "CleanupBreakdownExpander").IsExpanded = true;
        window.UpdateLayout();
        var cargo = root.Children.Single(row => row.Name == "Rust / Cargo");
        Descendants(Container()).OfType<Expander>().Single(expander => expander.Name == "CleanupContentExpander"
            && ReferenceEquals(expander.DataContext, cargo)).IsExpanded = true;
        window.UpdateLayout();
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            string theme = mode == ThemeMode.Light ? "light" : "dark";
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"build-contents-{theme}.png"), 1220, 910);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"build-contents-compact-{theme}.png"), 1020, 790);
        }
#pragma warning restore WPF0001
        Assert.Contains(Descendants(Container()).OfType<TextBlock>(), block => block.Inlines.OfType<System.Windows.Documents.Run>()
            .Any(run => run.Text.Contains("incremental compilation")));
        Assert.Contains(Descendants(Container()).OfType<TextBlock>(), block => block.Text.Contains("Cargo rebuilds"));
        var scroll = (ScrollViewer)window.FindName("CleanupOverviewScroll");
        scroll.ScrollToVerticalOffset(80); window.UpdateLayout();
        double offset = scroll.VerticalOffset;
        cargo.Matches[0].Selected = true;
        Descendants(Container()).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, cargo))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var contents = (ItemsControl)window.FindName("BuildFolderContentsList");
        var preview = contents.Items.Cast<MainWindow.BuildFolderContentRow>().ToArray();
        Assert.Equal(8, preview.Length);
        Assert.Equal("debug/", preview[0].Name); Assert.Equal("72 GiB", preview[0].Size);
        Assert.Equal("release/", preview[1].Name); Assert.Equal("16 GiB", preview[1].Size);
        Assert.Contains("Largest 8 of 13 entries", ((TextBlock)window.FindName("BuildFolderContentsNote")).Text);
        Assert.True(Assert.IsType<OpportunityRow>(((ListBox)window.FindName("OpportunityGrid")).SelectedItem).Selected);
        Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("BuildFolderContentsPanel")).Visibility);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "build-folder-contents-dark.png"), 1220, 910);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "build-folder-contents-compact-dark.png"), 1020, 790);
        ((Button)window.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Math.Abs(scroll.VerticalOffset - offset) < 1, TimeSpan.FromSeconds(3));
        var restored = Assert.Single(cards.Items.Cast<OpportunityGroupRow>());
        Assert.True(restored.Expanded);
        Assert.True(restored.Children.Single(row => row.Name == "Rust / Cargo").ContentsExpanded);
        Assert.True(restored.Children.Single(row => row.Name == "Rust / Cargo").Checked);
        ((Button)window.FindName("ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        ulong Project(string name, string manifest)
        { ulong project = Folder(1, name); Folder(project, ".git"); File(project, manifest, 0); return project; }
        ulong Folder(ulong parent, string name)
        { ulong id = next++; int index = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(index, new(parent), name); return id; }
        void File(ulong parent, string name, long bytes)
        { int index = builder.AddFile(new(new(next++), FileAttributes.Normal, bytes, bytes, 0)); builder.AddLink(index, new(parent), name); }
    }
}
