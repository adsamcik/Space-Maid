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
    private static void CheckOpenCodeSessions(MainWindow window, Application app, string artifactRoot)
    {
        var runtime = Assert.IsType<ExtensionRuntime>(typeof(MainWindow).GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var entry = Assert.Single(runtime.Entries, item => item.Id == "space-maid.agents");
        Assert.Equal("Loaded", entry.Status);
        var parent = Assert.IsAssignableFrom<UserControl>(entry.View);
        var tabs = Assert.IsType<TabControl>(parent.FindName("AgentTabs"));
        var view = Assert.IsAssignableFrom<UserControl>(parent.FindName("OpenCodeView"));
        var type = view.GetType(); var assembly = type.Assembly;
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly));
        const string sessionId = "ses_owned123";
        const string directory = @"C:\Projects\agent-work\research-project-with-a-long-workspace-name\experiments";
        long created = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();
        long updated = DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeMilliseconds();
        var sessionType = assembly.GetType("SpaceMaid.Extensions.Agents.OpenCodeSession")!;
        object idle = Activator.CreateInstance(sessionType, [sessionId, "Investigate build cache growth", directory,
            "owned-project", created, updated, null, "idle", true])!;
        object busy = Activator.CreateInstance(sessionType, ["ses_busy123", "Current agent work", directory,
            "owned-project", created, updated, null, "busy", true])!;
        var sessions = Array.CreateInstance(sessionType, 2); sessions.SetValue(idle, 0); sessions.SetValue(busy, 1);
        object server = Activator.CreateInstance(assembly.GetType("SpaceMaid.Extensions.Agents.OpenCodeServer")!,
            ["1.2.27", @"C:\Users\owned-fixture\.local\state\opencode", directory])!;
        object snapshot = Activator.CreateInstance(assembly.GetType("SpaceMaid.Extensions.Agents.OpenCodeSessionSnapshot")!, [server, sessions])!;
        int inspections = 0, preparations = 0, executions = 0;
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
        SetDelegate("Inspector", _ => { inspections++; return Completed(inspector, snapshot); });
        SetDelegate("Preparer", values =>
        {
            preparations++; Assert.Same(idle, values[2]); Assert.Equal(30, values[3]);
            object plan = Activator.CreateInstance(assembly.GetType("SpaceMaid.Extensions.Agents.OpenCodeSessionPlan")!, [server, values[2], 30])!;
            return Completed(preparer, plan);
        });
        SetDelegate("Executor", values =>
        {
            executions++;
            var selected = values[1]!.GetType().GetProperty("Session")!.GetValue(values[1]);
            Assert.Same(idle, selected);
            object result = Activator.CreateInstance(assembly.GetType("SpaceMaid.Extensions.Agents.OpenCodeSessionResult")!,
                [true, true, "Owned fixture session removed."])!;
            return Completed(executor, result);
        });
        Task Invoke(string name) => (Task)type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        var workflow = new Window { Title = "Agent cleanup", Owner = window, Content = parent, Width = 1050, Height = 720,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        workflow.SetResourceReference(Window.BackgroundProperty, "WindowBackground"); workflow.SetResourceReference(Window.ForegroundProperty, "WindowForeground");
        try
        {
            tabs.SelectedIndex = 1; workflow.Show();
            var list = (DataGrid)view.FindName("SessionList"); var review = (Button)view.FindName("ReviewButton");
            Assert.Empty(list.Items); Assert.Equal(0, inspections); Assert.False(review.IsEnabled);
            var inspect = Invoke("InspectAsync"); PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(3)); inspect.GetAwaiter().GetResult();
            Assert.Equal(1, inspections); Assert.Equal(2, list.Items.Count); Assert.Empty(list.SelectedItems);
            list.SelectedItem = busy; Assert.False(review.IsEnabled);
            var blocked = Invoke("ReviewCleanupAsync"); Assert.True(blocked.IsCompletedSuccessfully); Assert.Equal(0, preparations);
            list.SelectedItem = idle; Assert.True(review.IsEnabled);
            tabs.SelectedIndex = 0; tabs.SelectedIndex = 1;
            Assert.Same(idle, list.SelectedItem); Assert.True(review.IsEnabled);
#pragma warning disable WPF0001
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = mode;
                Render(workflow, Path.Join(artifactRoot, "artifacts", "ui", $"opencode-sessions-{(mode == ThemeMode.Light ? "light" : "dark")}.png"), 1050, 720);
            }
#pragma warning restore WPF0001
            Render(workflow, Path.Join(artifactRoot, "artifacts", "ui", "opencode-sessions-compact.png"), 740, 590);
            Preview(confirm: false); Assert.Equal(0, executions); Assert.Same(idle, list.SelectedItem);
            Preview(confirm: true); Assert.Equal(1, executions); Assert.Equal(2, preparations);
            Assert.Empty(list.Items); Assert.False(review.IsEnabled);
            Assert.Contains("fixture session removed", ((TextBlock)view.FindName("StatusText")).Text);
            ((TextBox)view.FindName("AgeBox")).Text = "0";
            inspect = Invoke("InspectAsync"); PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(3)); inspect.GetAwaiter().GetResult();
            Assert.Contains("1 through 3650", ((TextBlock)view.FindName("StatusText")).Text); Assert.Equal(1, inspections);

            void Preview(bool confirm)
            {
                Exception? failure = null; bool shown = false;
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
                {
                    var dialog = app.Windows.Cast<Window>().FirstOrDefault(candidate => candidate.GetType().FullName == "SpaceMaid.Extensions.Agents.OpenCodeSessionPreview" && candidate.IsVisible);
                    if (dialog is null) return;
                    try
                    {
                        shown = true;
                        Assert.Contains(Descendants(dialog).OfType<TextBox>(), box => box.IsReadOnly && box.Text == sessionId);
                        Assert.Contains(Descendants(dialog).OfType<TextBox>(), box => box.IsReadOnly && box.Text == directory);
                        var action = (Button)dialog.FindName("DeleteButton"); Assert.False(action.IsEnabled);
                        if (!confirm)
                        {
                            Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "opencode-session-cleanup-preview.png"), 700, 610);
                            dialog.DialogResult = false;
                        }
                        else
                        {
                            ((CheckBox)dialog.FindName("PausedAcknowledgement")).IsChecked = true;
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
