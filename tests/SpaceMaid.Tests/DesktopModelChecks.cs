using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckModels(MainWindow window, Application app, string artifactRoot)
    {
        var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow).GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var entry = Assert.Single(runtime.Entries, item => item.Id == "space-maid.models");
        Assert.Equal("Loaded", entry.Status);
        var view = Assert.IsAssignableFrom<UserControl>(entry.View); var type = view.GetType();
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly));
        var assembly = type.Assembly;
        var parser = assembly.GetType("SpaceMaid.Extensions.Models.OllamaCleanup")!.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!;
        byte[] empty = System.Text.Encoding.UTF8.GetBytes("{\"models\":[]}");
        string digest = new('a', 64), loadedDigest = new('b', 64);
        byte[] tags = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { models = new[]
        {
            new { name = "research:local", model = "research:local", digest, size = 2L << 30 },
            new { name = "active-agent:latest", model = "active-agent:latest", digest = loadedDigest, size = 3L << 30 }
        } });
        byte[] loaded = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { models = new[] { new { name = "active-agent:latest", digest = loadedDigest } } });
        object snapshot = parser.Invoke(null, [new ReadOnlyMemory<byte>(tags), new ReadOnlyMemory<byte>(loaded), CancellationToken.None])!;
        object after = parser.Invoke(null, [new ReadOnlyMemory<byte>(empty), new ReadOnlyMemory<byte>(empty), CancellationToken.None])!;
        object Completed(PropertyInfo property, object result) => typeof(Task).GetMethods().Single(method => method.Name == "FromResult")
            .MakeGenericMethod(property.PropertyType.GetMethod("Invoke")!.ReturnType.GetGenericArguments()[0]).Invoke(null, [result])!;
        void SetDelegate(string name, Func<object?[], object> provider)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            var invoke = property.PropertyType.GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
            var values = Expression.NewArrayInit(typeof(object), parameters.Select(parameter => Expression.Convert(parameter, typeof(object))));
            property.SetValue(view, Expression.Lambda(property.PropertyType,
                Expression.Convert(Expression.Invoke(Expression.Constant(provider), values), invoke.ReturnType), parameters).Compile());
        }
        var inspector = type.GetProperty("Inspector", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var preparer = type.GetProperty("Preparer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var executor = type.GetProperty("Executor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int inspections = 0, preparations = 0, executions = 0;
        SetDelegate("Inspector", _ => { inspections++; return Completed(inspector, snapshot); });
        SetDelegate("Preparer", values =>
        {
            preparations++;
            var plan = Activator.CreateInstance(assembly.GetType("SpaceMaid.Extensions.Models.OllamaCleanupPlan")!, [values[0], DateTimeOffset.UtcNow])!;
            return Completed(preparer, plan);
        });
        SetDelegate("Executor", _ =>
        {
            executions++;
            var result = Activator.CreateInstance(assembly.GetType("SpaceMaid.Extensions.Models.OllamaCleanupResult")!, [true, true, "Owned fixture model removed.", after])!;
            return Completed(executor, result);
        });
        Task Invoke(string name) => (Task)type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        var workflow = new Window { Title = "AI models", Owner = window, Content = view, Width = 1050, Height = 720,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        workflow.SetResourceReference(Window.BackgroundProperty, "WindowBackground"); workflow.SetResourceReference(Window.ForegroundProperty, "WindowForeground");
        try
        {
            workflow.Show();
            var list = (DataGrid)view.FindName("ModelList"); var review = (Button)view.FindName("ReviewButton");
            Assert.Empty(list.Items); Assert.Equal(0, inspections); Assert.False(review.IsEnabled);
            var task = Invoke("InspectAsync"); PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(3)); task.GetAwaiter().GetResult();
            Assert.Equal(1, inspections); Assert.Equal(2, list.Items.Count);
            list.SelectedItem = list.Items.Cast<object>().Single(model => model.GetType().GetProperty("Loaded")!.GetValue(model) is true);
            Assert.False(review.IsEnabled);
            var selected = list.Items.Cast<object>().Single(model => model.GetType().GetProperty("Loaded")!.GetValue(model) is false);
            list.SelectedItem = selected; Assert.True(review.IsEnabled);
#pragma warning disable WPF0001
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = mode;
                Render(workflow, Path.Join(artifactRoot, "artifacts", "ui", $"models-{(mode == ThemeMode.Light ? "light" : "dark")}.png"), 1050, 720);
            }
#pragma warning restore WPF0001
            Render(workflow, Path.Join(artifactRoot, "artifacts", "ui", "models-compact.png"), 740, 590);
            Preview(confirm: false); Assert.Equal(0, executions); Assert.Same(selected, list.SelectedItem);
            Preview(confirm: true); Assert.Equal(1, executions); Assert.Equal(2, preparations);
            Assert.Empty(list.Items); Assert.False(review.IsEnabled);
            Assert.Contains("model removed", ((TextBlock)view.FindName("StatusText")).Text);

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
                        Assert.Contains(Descendants(dialog).OfType<TextBox>(), box => box.IsReadOnly && box.Text == digest);
                        Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text == "research:local");
                        if (!confirm) Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "models-cleanup-preview.png"), 640, 520);
                        if (confirm) ((Button)dialog.FindName("DeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else dialog.DialogResult = false;
                    }
                    catch (Exception error) { failure = error; dialog.Close(); }
                }, Dispatcher.CurrentDispatcher);
                try
                {
                    var cleanup = Invoke("ReviewCleanupAsync"); PumpUntil(() => cleanup.IsCompleted, TimeSpan.FromSeconds(5)); cleanup.GetAwaiter().GetResult();
                    if (failure is not null) throw failure;
                    Assert.True(shown);
                }
                finally { timer.Stop(); }
            }
        }
        finally { workflow.Content = null; workflow.Close(); }
    }
}
