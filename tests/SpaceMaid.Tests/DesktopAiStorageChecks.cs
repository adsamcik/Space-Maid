using SpaceMaid.Extensions.Contracts;
using System.IO;
using System.Reflection;
using Expression = System.Linq.Expressions.Expression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckAiStorage(MainWindow window, Application app, string artifactRoot)
    {
        string[] repositories = ["models--buttercrab--dia-v1-1.6b", "models--nari-labs--Dia-1.6B-0626", "models--nari-labs--Dia-1.6B",
            "models--NbAiLab--nb-wav2vec2-1b-nynorsk", "models--jonatasgrosman--wav2vec2-large-xlsr-53-japanese",
            .. Enumerable.Range(1, 20).Select(index => $"models--example--speech-model-{index:D2}")];
        const string hub = ".cache/huggingface/hub/";
        var scan = AiStorageFixture.Inventory(AiStorageFixture.ExampleFiles.Concat(repositories.SelectMany((name, index) =>
                Enumerable.Range(1, index < 3 ? 6 : 1).Select(shard => $"{hub}{name}/snapshots/revision/model-{shard}.safetensors"))),
            repositories.Append("models--org--model").Select(name => hub + name + "/refs"));
        var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow)
            .GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        CheckOllamaLibrary(window, app, artifactRoot, runtime);
        var analysis = runtime.Opportunities.Analyze(scan, default);
        var load = window.InitializeExplorerAsync(scan, analysis.Findings, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        ((TextBox)OpportunityControl(window, "OpportunitySearch")).Text = "";
        ((ComboBox)OpportunityControl(window, "OpportunityTypeFilter")).SelectedIndex = 0;
        ((ComboBox)OpportunityControl(window, "OpportunityGroupingPicker")).SelectedIndex = 0;
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, analysis.Findings, analysis.Groups, null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        ((Button)OpportunityControl(window, "BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var cards = (ItemsControl)OpportunityControl(window, "CleanupGroupCards");
        var models = Assert.Single(cards.Items.Cast<OpportunityGroupRow>(), row => row.Name == "AI models & data");
        Assert.Equal(3, models.Children.Count); Assert.True(models.CanSelectGroup);
        Assert.Contains(cards.Items.Cast<OpportunityGroupRow>(), row => row.Name == "Agent file checkpoints");
        ((TextBlock)window.FindName("StatusText")).Text = "Illustrative inventory · AI tools and local models";
        ((TextBlock)window.FindName("ScannedSizeText")).Text = SizeText.Format(scan.AllocatedBytes);
        ((TextBlock)window.FindName("ScannedSizeCaption")).Text = "On disk";
        ((TextBlock)window.FindName("CandidateText")).Text = SizeText.Format(new FileOperations().EstimateCleanup(scan, analysis.Findings).EstimatedBytes);
        ((TextBlock)window.FindName("CandidateCaption")).Text = $"Observed · {analysis.Findings.Count} folders";
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-ai-{(mode == ThemeMode.Light ? "light" : "dark")}.png"), 1220, 910);
        }
#pragma warning restore WPF0001
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-ai-compact.png"), 1020, 790);
        window.UpdateLayout();
        var container = cards.ItemContainerGenerator.ContainerFromItem(models);
        Assert.Single(Descendants(container).OfType<Expander>()).IsExpanded = true; window.UpdateLayout();
        var ollama = Assert.Single(models.Children, row => row.Name == "Ollama");
        Assert.False(ollama.CanSelectGroup);
        Descendants(container).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, ollama))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var list = (ListBox)OpportunityControl(window, "OpportunityGrid");
        var row = Assert.IsType<OpportunityRow>(Assert.Single(list.Items.Cast<object>()));
        Assert.Equal("Manage in owning tool", row.Review); Assert.Contains("Review removal", row.Item.SuggestedAction);
        Assert.False(row.CanSelect); Assert.False(((Button)OpportunityControl(window, "SelectListedButton")).IsEnabled);
        row.Selected = true; Assert.False(row.Selected);
        Assert.Contains("Space Maid keeps", row.Item.Caution);
        Assert.Equal("AI models & data › Ollama", ((TextBlock)OpportunityControl(window, "CleanupScopeText")).Text);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "cleanup-ai-model-details.png"), 1220, 910);
        ((Button)OpportunityControl(window, "BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        models = cards.Items.Cast<OpportunityGroupRow>().Single(row => row.Name == "AI models & data");
        container = cards.ItemContainerGenerator.ContainerFromItem(models);
        Assert.Single(Descendants(container).OfType<Expander>(), expander => Equals(expander.Header, "By tool and project")).IsExpanded = true; window.UpdateLayout();
        var huggingFace = Assert.Single(models.Children, row => row.Name == "Hugging Face");
        Descendants(container).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, huggingFace))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(26, list.Items.Count);
        ((Button)OpportunityControl(window, "SelectListedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.All(list.Items.Cast<OpportunityRow>(), row => Assert.True(row.Selected));
        Assert.Equal("26 locations selected", ((TextBlock)OpportunityControl(window, "SelectionText")).Text);
        window.MarkScanStale("Illustrative inventory · Review rechecks selected folders");
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            string theme = mode == ThemeMode.Light ? "light" : "dark";
            foreach (var (width, height, size) in new[] { (1220, 910, "normal"), (1020, 790, "compact") })
            {
                Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"cleanup-huggingface-{size}-{theme}.png"), width, height);
                Assert.True(list.ActualWidth > 320);
                Assert.Equal(((OpportunityRow)list.SelectedItem).Name, ((TextBlock)OpportunityControl(window, "DetailName")).Text);
                Assert.All(Descendants(list).OfType<TextBlock>().Where(text => text.Text == "Hugging Face repository downloads"),
                    text => Assert.Equal(Visibility.Collapsed, text.Visibility));
            }
        }
#pragma warning restore WPF0001
        var detailsExpander = (Expander)OpportunityControl(window, "FolderDetailsExpander");
        detailsExpander.IsExpanded = true; window.UpdateLayout();
        Assert.Contains(Descendants(detailsExpander).OfType<TextBlock>(), text => text.Text.Contains("Windows without symlinks"));
        detailsExpander.IsExpanded = false;
        var search = (TextBox)OpportunityControl(window, "OpportunitySearch");
        search.Text = "buttercrab/dia-v1-1.6b";
        PumpUntil(() => list.Items.Count == 1, TimeSpan.FromSeconds(3));
        Assert.Contains("25 outside this view", ((TextBlock)OpportunityControl(window, "SelectionText")).Text);
        var readableModel = Assert.IsType<OpportunityRow>(list.Items[0]);
        Assert.Equal("buttercrab/dia-v1-1.6b", readableModel.Name);
        Assert.Contains(Descendants((StackPanel)OpportunityControl(window, "DetailPanel")).OfType<TextBox>(),
            text => text.IsReadOnly && text.Text == readableModel.Path && text.Text.EndsWith("models--buttercrab--dia-v1-1.6b", StringComparison.Ordinal));
        search.Text = "no-such-model";
        PumpUntil(() => list.Items.Count == 0, TimeSpan.FromSeconds(3));
        Assert.Equal(Visibility.Visible, ((StackPanel)OpportunityControl(window, "EmptyState")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)OpportunityControl(window, "DetailPanel")).Visibility);
        search.Text = "";
        PumpUntil(() => list.Items.Count == 26, TimeSpan.FromSeconds(3));
        ((Button)OpportunityControl(window, "BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)OpportunityControl(window, "ClearOpportunitySelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((ComboBox)OpportunityControl(window, "OpportunityGroupingPicker")).SelectedIndex = 1;
    }

    private static void CheckOllamaLibrary(MainWindow window, Application app, string artifactRoot, ExtensionRuntime runtime)
    {
        using var fixture = new TestDirectory();
        string library = Path.Join(fixture.Path, ".ollama", "models");
        string first = OllamaLibraryTests.WriteModel(library, "gemma3:12b-it-qat", 8L << 30);
        OllamaLibraryTests.WriteModel(library, "qwen3:8b", 5L << 30);
        OllamaLibraryTests.WriteModel(library, "research:local", 3L << 30, space: "team");
        Directory.CreateDirectory(Path.Join(library, "blobs"));
        File.WriteAllText(Path.Join(library, "blobs", "sha256-" + new string('b', 64)), "owned fixture");
        var scanTask = window.Operations.Files.ScanAsync(fixture.Path);
        PumpUntil(() => scanTask.IsCompleted, TimeSpan.FromSeconds(5));
        var scan = scanTask.GetAwaiter().GetResult();
        var analysis = runtime.Opportunities.Analyze(scan, default);
        var load = window.InitializeExplorerAsync(scan, analysis.Findings, default);
        PumpUntil(() => load.IsCompleted, TimeSpan.FromSeconds(5)); load.GetAwaiter().GetResult();
        ((ComboBox)OpportunityControl(window, "OpportunityGroupingPicker")).SelectedIndex = 1;
        typeof(MainWindow).GetMethod("PublishOpportunities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [scan, analysis.Findings, analysis.Groups, null, null, false]);
        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        var entry = Assert.Single(runtime.Entries, item => item.Id == "space-maid.opportunities");
        var view = Assert.IsAssignableFrom<UserControl>(entry.View);
        Assert.Same(window.Operations.Files, view.GetType().GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view));
        var list = (DataGrid)view.FindName("LibraryModelList");
        var status = (TextBlock)view.FindName("ModelStatus");
        var search = (TextBox)view.FindName("ModelSearch");
        var refresh = (Button)view.FindName("RefreshModelsButton");
        var remove = (Button)view.FindName("RemoveModelButton");
        PumpUntil(() => list.Items.Count == 3, TimeSpan.FromSeconds(5));
        Assert.Equal(Visibility.Collapsed, ((ListBox)view.FindName("OpportunityGrid")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((Grid)view.FindName("OpportunityFilters")).Visibility);
        Assert.Equal("3 models", status.Text);
        Assert.False(((Button)view.FindName("SelectListedButton")).IsEnabled);
        var folderSearch = (TextBox)view.FindName("OpportunitySearch");
        folderSearch.Text = ".ollama";
        PumpUntil(() => ((Grid)view.FindName("OpportunityFilters")).Visibility == Visibility.Visible && list.Items.Count == 3, TimeSpan.FromSeconds(5));
        folderSearch.Text = "";
        PumpUntil(() => ((Grid)view.FindName("OpportunityFilters")).Visibility == Visibility.Collapsed && list.Items.Count == 3, TimeSpan.FromSeconds(5));
        search.Text = "QWEN"; Assert.Single(list.Items);
        search.Text = "missing"; Assert.Empty(list.Items); Assert.Equal("0 of 3 models", status.Text);
        search.Text = "";
        Assert.False(remove.IsEnabled);
        list.SelectedIndex = 0;
        Assert.True(remove.IsEnabled);
#pragma warning disable WPF0001
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = mode;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"ollama-library-{mode}.png"), 1220, 910);
        }
#pragma warning restore WPF0001
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "ollama-library-compact.png"), 1020, 790);
        Assert.True(list.ActualHeight > 250);
        Assert.True(list.ActualWidth >= 240);
        File.WriteAllBytes(first, OllamaLibraryTests.Manifest(10L << 30));
        search.Text = "gemma";
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => refresh.IsEnabled, TimeSpan.FromSeconds(5));
        Assert.Equal("gemma", search.Text); Assert.Single(list.Items);
        Assert.Equal("10 GiB", list.Items[0].GetType().GetProperty("SizeText")!.GetValue(list.Items[0]));
        CheckInlineRemoval();
        Directory.Delete(Path.Join(library, "manifests"), recursive: true);
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => refresh.IsEnabled, TimeSpan.FromSeconds(5));
        Assert.Empty(list.Items); Assert.Contains("Check folder access", status.Text);
        OllamaLibraryTests.WriteModel(library, "recovered:latest", 1024);
        search.Text = "";
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => refresh.IsEnabled, TimeSpan.FromSeconds(5));
        Assert.Single(list.Items); Assert.Equal("1 model", status.Text);
        ((Button)view.FindName("BackToCleanupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((ComboBox)view.FindName("OpportunityGroupingPicker")).SelectedIndex = 0;
        Assert.Equal(Visibility.Collapsed, ((Grid)view.FindName("OllamaModelsPanel")).Visibility);

        void CheckInlineRemoval()
        {
            var modelsView = Assert.Single(runtime.Entries, item => item.Id == "space-maid.models").View!;
            Assert.IsAssignableFrom<IModelRemovalWorkflow>(modelsView);
            var modelType = modelsView.GetType();
            var preparer = modelType.GetProperty("LibraryPreparer", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var executor = modelType.GetProperty("Executor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object originalPreparer = preparer.GetValue(modelsView)!, originalExecutor = executor.GetValue(modelsView)!;
            int preparations = 0, executions = 0;
            bool loaded = true;
            bool failRefresh = false;
            string expectedModel = "gemma3:12b-it-qat";
            object Completed(PropertyInfo property, object value) => typeof(Task).GetMethods().Single(method => method.Name == "FromResult")
                .MakeGenericMethod(property.PropertyType.GetMethod("Invoke")!.ReturnType.GetGenericArguments()[0]).Invoke(null, [value])!;
            void SetDelegate(PropertyInfo property, Func<object?[], object> provider)
            {
                var invoke = property.PropertyType.GetMethod("Invoke")!;
                var parameters = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
                var values = Expression.NewArrayInit(typeof(object), parameters.Select(parameter => Expression.Convert(parameter, typeof(object))));
                property.SetValue(modelsView, Expression.Lambda(property.PropertyType,
                    Expression.Convert(Expression.Invoke(Expression.Constant(provider), values), invoke.ReturnType), parameters).Compile());
            }
            SetDelegate(preparer, values =>
            {
                preparations++;
                if (loaded) throw new InvalidOperationException("This model is loaded. Finish its sessions before cleanup.");
                var request = Assert.IsType<ModelRemovalRequest>(values[0]);
                Assert.Equal(library, request.LibraryPath); Assert.Equal(expectedModel, request.Name);
                var model = Activator.CreateInstance(modelType.Assembly.GetType("SpaceMaid.Extensions.Models.OllamaModel")!,
                    [request.Name, request.Digest, request.Bytes, false])!;
                var plan = Activator.CreateInstance(modelType.Assembly.GetType("SpaceMaid.Extensions.Models.OllamaCleanupPlan")!, [model, DateTimeOffset.UtcNow])!;
                plan.GetType().GetProperty("Library")!.SetValue(plan, request);
                return Completed(preparer, plan);
            });
            SetDelegate(executor, _ =>
            {
                executions++;
                if (failRefresh) Directory.Delete(Path.Join(library, "manifests"), recursive: true);
                else File.Delete(first);
                var result = Activator.CreateInstance(modelType.Assembly.GetType("SpaceMaid.Extensions.Models.OllamaCleanupResult")!,
                    [true, true, "Model removed by Ollama. Shared data may remain.", null])!;
                return Completed(executor, result);
            });
            Task Remove() => (Task)view.GetType().GetMethod("RemoveLibraryModelAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
            try
            {
                list.SelectedIndex = 0; Assert.True(remove.IsEnabled);
                var blocked = Remove(); PumpUntil(() => blocked.IsCompleted, TimeSpan.FromSeconds(3)); blocked.GetAwaiter().GetResult();
                Assert.Contains("loaded", status.Text); Assert.Equal(0, executions); Assert.True(File.Exists(first));
                loaded = false;
                Preview(confirm: false); Assert.Equal(0, executions); Assert.True(File.Exists(first));
                Assert.Equal("gemma", search.Text); Assert.Single(list.Items);
                Preview(confirm: true); Assert.Equal(1, executions); Assert.Equal(3, preparations);
                Assert.False(File.Exists(first)); Assert.Empty(list.Items); Assert.Equal("gemma", search.Text);
                Assert.Contains("Model removed", status.Text); Assert.False(remove.IsEnabled);
                Assert.Contains("Out of date", ((TextBlock)view.FindName("CleanupFreshnessText")).Text);
                Assert.True(File.Exists(Path.Join(library, "manifests", "registry.ollama.ai", "library", "qwen3", "8b")));
                search.Text = "qwen"; list.SelectedIndex = 0;
                expectedModel = "qwen3:8b"; failRefresh = true;
                Preview(confirm: true);
                Assert.Contains("Model removed", status.Text); Assert.Contains("could not be refreshed", status.Text);
                Assert.Empty(list.Items); Assert.Equal("qwen", search.Text);
                Directory.CreateDirectory(Path.Join(library, "manifests"));
            }
            finally { preparer.SetValue(modelsView, originalPreparer); executor.SetValue(modelsView, originalExecutor); }

            void Preview(bool confirm)
            {
                Exception? failure = null; bool shown = false;
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
                {
                    var dialog = app.Windows.Cast<Window>().FirstOrDefault(candidate => candidate.GetType().FullName == "SpaceMaid.Extensions.Models.ModelCleanupWindow" && candidate.IsVisible);
                    if (dialog is null) return;
                    try
                    {
                        shown = true;
                        Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text == expectedModel);
                        Assert.Contains(Descendants(dialog).OfType<TextBox>(), text => text.IsReadOnly && text.Text == library);
                        if (!confirm) Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "ollama-inline-removal-review.png"), 640, 570);
                        if (confirm) ((Button)dialog.FindName("DeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else dialog.DialogResult = false;
                    }
                    catch (Exception error) { failure = error; dialog.Close(); }
                }, Dispatcher.CurrentDispatcher);
                try
                {
                    var task = Remove(); PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)); task.GetAwaiter().GetResult();
                    if (failure is not null) throw failure;
                    Assert.True(shown);
                }
                finally { timer.Stop(); }
            }
        }
    }
}
