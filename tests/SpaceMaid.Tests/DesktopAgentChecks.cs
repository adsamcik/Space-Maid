using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Expression = System.Linq.Expressions.Expression;
using SpaceMaid.App;
using SpaceMaid.App.Extensions;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckAgents(MainWindow window, Application app, string artifactRoot)
    {
        var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow).GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var entry = Assert.Single(runtime.Entries, item => item.Id == "space-maid.agents");
        Assert.Equal("Loaded", entry.Status);
        var view = Assert.IsAssignableFrom<UserControl>(entry.View); var type = view.GetType(); var assembly = type.Assembly;
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly));
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "sessions", "2026", "08", "01")).FullName;
        string transcript = Path.Join(folder, "rollout-2026-08-01T10-00-00-owned-session.jsonl");
        File.WriteAllBytes(transcript, new byte[32768]); File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddDays(-60));
        var homeType = assembly.GetType("SpaceMaid.Extensions.Agents.AgentHome")!;
        var toolType = assembly.GetType("SpaceMaid.Extensions.Agents.AgentTool")!;
        var homes = Array.CreateInstance(homeType, 1);
        homes.SetValue(Activator.CreateInstance(homeType, [Enum.ToObject(toolType, 0), fixture.Path]), 0);
        var cleanupType = assembly.GetType("SpaceMaid.Extensions.Agents.AgentHistoryCleanup")!;
        object snapshot = cleanupType.GetMethod("Inspect", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [homes, 30, DateTime.UtcNow, CancellationToken.None])!;
        int inspections = 0, executions = 0;
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
        var executor = type.GetProperty("Executor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        SetDelegate("Inspector", _ => { inspections++; return Completed(inspector, snapshot); });
        SetDelegate("Executor", _ =>
        {
            executions++;
            var result = Activator.CreateInstance(assembly.GetType("SpaceMaid.Extensions.Agents.AgentHistoryResult")!, [1, "Owned fixture cleanup completed."])!;
            return Completed(executor, result);
        });
        Task Invoke(string name) => (Task)type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        var workflow = new Window { Title = "Agent cleanup", Owner = window, Content = view, Width = 1050, Height = 720,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        workflow.SetResourceReference(Window.BackgroundProperty, "WindowBackground"); workflow.SetResourceReference(Window.ForegroundProperty, "WindowForeground");
        try
        {
            workflow.Show();
            var list = (DataGrid)view.FindName("HistoryList"); var review = (Button)view.FindName("ReviewButton");
            Assert.Empty(list.Items); Assert.Equal(0, inspections); Assert.False(review.IsEnabled);
            var inspect = Invoke("InspectAsync"); PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(3)); inspect.GetAwaiter().GetResult();
            Assert.Equal(1, inspections); Assert.Single(list.Items); Assert.Empty(list.SelectedItems);
            list.SelectedItem = list.Items[0]; Assert.True(review.IsEnabled);
#pragma warning disable WPF0001
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = mode;
                Render(workflow, Path.Join(artifactRoot, "artifacts", "ui", $"agents-{(mode == ThemeMode.Light ? "light" : "dark")}.png"), 1050, 720);
            }
#pragma warning restore WPF0001
            Render(workflow, Path.Join(artifactRoot, "artifacts", "ui", "agents-compact.png"), 740, 590);
            Preview(confirm: false); Assert.Equal(0, executions); Assert.Single(list.SelectedItems);
            Preview(confirm: true); Assert.Equal(1, executions); Assert.Empty(list.Items);
            Assert.Contains("cleanup completed", ((TextBlock)view.FindName("StatusText")).Text);
            Assert.True(File.Exists(transcript)); // The desktop executor is deliberately inert.
            ((TextBox)view.FindName("AgeBox")).Text = "0";
            inspect = Invoke("InspectAsync"); PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(3)); inspect.GetAwaiter().GetResult();
            Assert.Contains("1 through 3650", ((TextBlock)view.FindName("StatusText")).Text);
            Assert.Equal(1, inspections);

            void Preview(bool confirm)
            {
                Exception? failure = null; bool shown = false;
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
                {
                    var dialog = app.Windows.Cast<Window>().FirstOrDefault(candidate => candidate.GetType().FullName == "SpaceMaid.Extensions.Agents.AgentHistoryPreview" && candidate.IsVisible);
                    if (dialog is null) return;
                    try
                    {
                        shown = true;
                        Assert.Contains(Descendants(dialog).OfType<TextBlock>(), block => block.Text == transcript);
                        var action = (Button)dialog.FindName("RecycleButton"); Assert.False(action.IsEnabled);
                        if (!confirm)
                        {
                            Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "agents-cleanup-preview.png"), 740, 590);
                            dialog.DialogResult = false;
                        }
                        else
                        {
                            ((CheckBox)dialog.FindName("StoppedAcknowledgement")).IsChecked = true;
                            Assert.True(action.IsEnabled); action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                    }
                    catch (Exception error) { failure = error; dialog.Close(); }
                }, Dispatcher.CurrentDispatcher);
                try
                {
                    var task = Invoke("ReviewCleanupAsync"); PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)); task.GetAwaiter().GetResult();
                    if (failure is not null) throw failure;
                    Assert.True(shown);
                }
                finally { timer.Stop(); }
            }
        }
        finally { workflow.Content = null; workflow.Close(); }
    }
}
