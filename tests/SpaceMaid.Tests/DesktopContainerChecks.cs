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
            var copy = (Button)view.FindName("CopyButton"); var command = (TextBox)view.FindName("CommandText");
            Assert.Empty(list.Items); Assert.False(copy.IsEnabled);
            int calls = 0; SetInspector(_ => { calls++; return completed; });
            var inspect = Inspect(); PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(3)); inspect.GetAwaiter().GetResult();
            Assert.Equal(1, calls); Assert.Equal(2, list.Items.Count);
            list.SelectedItem = list.Items.Cast<object>().Single(item => item.GetType().GetProperty("State")!.GetValue(item) as string == "exited");
            Assert.True(copy.IsEnabled); Assert.Contains(" container rm ", command.Text);
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
            list.SelectedItem = list.Items.Cast<object>().Single(item => item.GetType().GetProperty("CanCopy")!.GetValue(item) is true);
            Assert.NotEmpty(command.Text);
            picker.SelectedItem = "Build cache";
            list.SelectedItem = list.Items.Cast<object>().Single(item => item.GetType().GetProperty("CanCopy")!.GetValue(item) is true);
            Assert.Equal("Copy builder cleanup command", copy.Content);
            Assert.Contains("builder-wide", ((TextBlock)view.FindName("DetailsText")).Text);
            picker.SelectedItem = "Volumes"; list.SelectedItem = list.Items[0]; Assert.False(copy.IsEnabled); Assert.Empty(command.Text);
            Assert.Contains("no volume deletion", ((TextBlock)view.FindName("DetailsText")).Text);
            ((TextBox)view.FindName("SearchBox")).Text = "missing"; Assert.Empty(list.Items);
            ((TextBox)view.FindName("SearchBox")).Text = ""; Assert.Single(list.Items.Cast<object>());
            var tabs = Descendants(view).OfType<TabControl>().Single(); tabs.SelectedIndex = 1;
            Render(workflow, Path.Join(artifactRoot, "artifacts", "ui", "containers-wsl-guide.png"), 1050, 720);
            tabs.SelectedIndex = 0;
            object failed = typeof(Task).GetMethods().Single(method => method.Name == "FromException" && method.IsGenericMethod)
                .MakeGenericMethod(resultType).Invoke(null, [new IOException("Owned unavailable-engine fixture")])!;
            SetInspector(_ => failed); inspect = Inspect(); PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(3)); inspect.GetAwaiter().GetResult();
            Assert.Empty(list.Items); Assert.False(copy.IsEnabled); Assert.Empty(command.Text);
            Assert.Contains("retry", ((TextBlock)view.FindName("StatusText")).Text);
            SetInspector(token => typeof(Task).GetMethods().Single(method => method.Name == "FromCanceled" && method.IsGenericMethod)
                .MakeGenericMethod(resultType).Invoke(null, [new CancellationToken(true)])!);
            inspect = Inspect(); PumpUntil(() => inspect.IsCompleted, TimeSpan.FromSeconds(3)); inspect.GetAwaiter().GetResult();
            Assert.False(copy.IsEnabled); Assert.True(((Button)view.FindName("InspectButton")).IsEnabled);
        }
        finally { workflow.Content = null; workflow.Close(); }
    }
}
