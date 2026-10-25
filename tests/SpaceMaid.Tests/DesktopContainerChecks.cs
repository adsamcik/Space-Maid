using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckContainers(MainWindow window, Application app, string artifactRoot)
    {
        var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow).GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var entry = Assert.Single(runtime.Entries, item => item.Id == "space-maid.containers");
        Assert.Equal("Loaded", entry.Status);
        var view = Assert.IsAssignableFrom<UserControl>(entry.View); var type = view.GetType();
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly));
        var inspector = type.GetProperty("Inspector", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var returnType = inspector.PropertyType.GetMethod("Invoke")!.ReturnType;
        var resultType = returnType.GetGenericArguments()[0];
        var parser = type.Assembly.GetType("SpaceMaid.Extensions.Containers.DockerUsage")!.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!;
        object rows = parser.Invoke(null, [new ReadOnlyMemory<byte>(ContainerStorageFixture.Report), CancellationToken.None])!;
        object completed = typeof(Task).GetMethods().Single(method => method.Name == "FromResult").MakeGenericMethod(resultType).Invoke(null, [rows])!;
        void SetInspector(Func<CancellationToken, object> provider)
        {
            var token = Expression.Parameter(typeof(CancellationToken));
            inspector.SetValue(view, Expression.Lambda(inspector.PropertyType, Expression.Convert(
                Expression.Invoke(Expression.Constant(provider), token), returnType), token).Compile());
        }
        Task Inspect() => (Task)type.GetMethod("InspectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        var workflow = new Window { Title = "Containers & WSL", Owner = window, Content = view, Width = 1050, Height = 720,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        workflow.SetResourceReference(Window.BackgroundProperty, "WindowBackground"); workflow.SetResourceReference(Window.ForegroundProperty, "WindowForeground");
        try
        {
            workflow.Show();
            var list = (DataGrid)view.FindName("StorageList"); var picker = (ComboBox)view.FindName("KindPicker");
            var cleanup = (Button)view.FindName("CleanupButton");
            Assert.Empty(list.Items); Assert.False(cleanup.IsEnabled);
            int calls = 0; SetInspector(_ => { calls++; return completed; });
            var inspect = Inspect(); PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(3)); inspect.GetAwaiter().GetResult();
            Assert.Equal(1, calls); Assert.Equal(2, list.Items.Count);
            list.SelectedItem = list.Items.Cast<object>().Single(item => item.GetType().GetProperty("State")!.GetValue(item) as string == "exited");
            Assert.True(cleanup.IsEnabled);
            Assert.Contains("agent work", ((TextBlock)view.FindName("DetailsText")).Text);
#pragma warning disable WPF0001
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = mode;
                Render(workflow, Path.Join(artifactRoot, "artifacts", "ui", $"containers-{(mode == ThemeMode.Light ? "light" : "dark")}.png"), 1050, 720);
            }
#pragma warning restore WPF0001
            Render(workflow, Path.Join(artifactRoot, "artifacts", "ui", "containers-compact.png"), 740, 590);
            picker.SelectedItem = "Images";
            list.SelectedItem = list.Items.Cast<object>().Single(item => item.GetType().GetProperty("CanClean")!.GetValue(item) is true);
            Assert.True(cleanup.IsEnabled);
            picker.SelectedItem = "Build cache";
            list.SelectedItem = list.Items.Cast<object>().Single(item => item.GetType().GetProperty("CanClean")!.GetValue(item) is true);
            Assert.True(cleanup.IsEnabled);
            Assert.Contains("exact cache record", ((TextBlock)view.FindName("DetailsText")).Text);
            picker.SelectedItem = "Volumes"; list.SelectedItem = list.Items[0]; Assert.True(cleanup.IsEnabled);
            Assert.Contains("Permanently deleting", ((TextBlock)view.FindName("DetailsText")).Text);
            ((TextBox)view.FindName("SearchBox")).Text = "missing"; Assert.Empty(list.Items);
            ((TextBox)view.FindName("SearchBox")).Text = ""; Assert.Single(list.Items.Cast<object>());
            CheckDockerConfirmation();
            var tabs = Descendants(view).OfType<TabControl>().Single(); tabs.SelectedIndex = 1;
            var disks = Assert.IsAssignableFrom<UserControl>(((ContentControl)view.FindName("WslContent")).Content);
            CheckWslCompaction(disks, artifactRoot);
            Render(workflow, Path.Join(artifactRoot, "artifacts", "ui", "containers-wsl-disks.png"), 1050, 720);
            tabs.SelectedIndex = 0;
            object failed = typeof(Task).GetMethods().Single(method => method.Name == "FromException" && method.IsGenericMethod)
                .MakeGenericMethod(resultType).Invoke(null, [new IOException("Owned unavailable-engine fixture")])!;
            SetInspector(_ => failed); inspect = Inspect(); PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(3)); inspect.GetAwaiter().GetResult();
            Assert.Empty(list.Items); Assert.False(cleanup.IsEnabled);
            Assert.Contains("retry", ((TextBlock)view.FindName("StatusText")).Text);
            SetInspector(token => typeof(Task).GetMethods().Single(method => method.Name == "FromCanceled" && method.IsGenericMethod)
                .MakeGenericMethod(resultType).Invoke(null, [new CancellationToken(true)])!);
            inspect = Inspect(); PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(3)); inspect.GetAwaiter().GetResult();
            Assert.False(cleanup.IsEnabled); Assert.True(((Button)view.FindName("InspectButton")).IsEnabled);

            void CheckDockerConfirmation()
            {
                int executed = 0;
                var planType = type.Assembly.GetType("SpaceMaid.Extensions.Containers.DockerCleanupPlan")!;
                var result = type.Assembly.GetType("SpaceMaid.Extensions.Containers.DockerCleanupResult")!;
                object TaskFor(Type valueType, object value) => typeof(Task).GetMethods().Single(method => method.Name == "FromResult")
                    .MakeGenericMethod(valueType).Invoke(null, [value])!;
                void SetFunction(string propertyName, Func<object, CancellationToken, object> provider)
                {
                    var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var signature = property.PropertyType.GetMethod("Invoke")!;
                    var target = Expression.Parameter(signature.GetParameters()[0].ParameterType); var token = Expression.Parameter(typeof(CancellationToken));
                    property.SetValue(view, Expression.Lambda(property.PropertyType, Expression.Convert(Expression.Invoke(Expression.Constant(provider),
                        Expression.Convert(target, typeof(object)), token), signature.ReturnType), target, token).Compile());
                }
                SetFunction("Preparer", (target, _) => TaskFor(planType, Activator.CreateInstance(planType, ["fixture-engine", target])!));
                SetFunction("Executor", (_, _) => { executed++; return TaskFor(result, Activator.CreateInstance(result, [true, "Owned fixture removal confirmed", null])!); });
                foreach (bool confirm in new[] { false, true })
                {
                    picker.SelectedItem = confirm ? "Containers" : "Volumes";
                    list.SelectedItem = list.Items.Cast<object>().First(item => item.GetType().GetProperty("CanClean")!.GetValue(item) is true);
                    Exception? failure = null; bool reviewed = false;
                    var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(20), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
                    {
                        var preview = app.Windows.Cast<Window>().FirstOrDefault(candidate => candidate.Title == "Review Docker cleanup" && candidate.IsVisible);
                        if (preview is null) return;
                        try
                        {
                            if (reviewed) { preview.Close(); return; }
                            reviewed = true;
                            var action = (Button)preview.FindName("ConfirmButton"); Assert.False(action.IsEnabled);
                            ((CheckBox)preview.FindName("PreservedCheck")).IsChecked = true;
                            if (!confirm)
                            {
                                var name = (TextBox)preview.FindName("VolumeNameBox"); Assert.False(action.IsEnabled);
                                name.Text = "wrong-volume"; Assert.False(action.IsEnabled);
                                name.Text = "agent-models-and-results"; Assert.True(action.IsEnabled);
                                ((Button)preview.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                            else
                            {
                                Assert.True(action.IsEnabled);
                                Render(preview, Path.Join(artifactRoot, "artifacts", "ui", "containers-cleanup-preview.png"), 660, 540);
                                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                        }
                        catch (Exception error) { failure = error; preview.Close(); }
                    }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
                    timer.Start();
                    try
                    {
                        var review = (Task)type.GetMethod("ReviewCleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
                        PumpUntil(() => review.IsCompleted, TimeSpan.FromSeconds(5)); review.GetAwaiter().GetResult();
                        if (failure is not null) throw failure;
                        Assert.True(reviewed); Assert.Equal(confirm ? 1 : 0, executed);
                    }
                    finally { timer.Stop(); }
                }
            }
        }
        finally { workflow.Content = null; workflow.Close(); }
    }
}
