using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Expression = System.Linq.Expressions.Expression;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    // Invoke with the real packaged WslDiskView while its workflow Window is open.
    private static void CheckWslCompaction(UserControl view, string artifactRoot)
    {
        Type type = view.GetType(); var assembly = type.Assembly;
        var rows = Assert.IsAssignableFrom<IList>(type.GetField("_disks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view));
        string path = @"G:\OwnedFixture\ext4.vhdx";
        rows.Add(Activator.CreateInstance(assembly.GetType("SpaceMaid.Extensions.Containers.WslDiskRow")!, ["Owned fixture", path, 10L << 30]));
        type.GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        var list = (DataGrid)view.FindName("DiskList"); list.SelectedItem = Assert.Single(list.Items.Cast<object>());
        var targetType = assembly.GetType("SpaceMaid.Extensions.Containers.VirtualDiskTarget")!;
        object target = Activator.CreateInstance(targetType, [path, 1UL, 2UL, 3UL, 10L << 30, 4L])!;
        SetTask("Prepare", target);
        int executions = 0; bool shutdownRequested = false;
        var resultType = assembly.GetType("SpaceMaid.Extensions.Containers.VirtualDiskResult")!;
        object result = Activator.CreateInstance(resultType, [true, "Owned fixture compaction complete", (long?)(10L << 30), (long?)(8L << 30)])!;
        SetTask("Execute", result, request =>
        {
            executions++; shutdownRequested = (bool)request.GetType().GetProperty("ShutdownWsl")!.GetValue(request)!;
            object confirmedTarget = request.GetType().GetProperty("Target")!.GetValue(request)!;
            Assert.Equal(path, confirmedTarget.GetType().GetProperty("Path")!.GetValue(confirmedTarget));
        });
        bool confirm = false; Exception? failure = null;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.Title == "Preview disk compaction" && window.IsVisible);
            if (dialog is null) return;
            try
            {
                Assert.Contains(Descendants(dialog).OfType<TextBox>(), text => text.Text == path);
                var button = Descendants(dialog).OfType<Button>().Single(item => item.Content as string == "Compact this disk…");
                Assert.False(button.IsEnabled);
                Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "wsl-compaction-preview.png"), 650, 480);
                Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "wsl-compaction-preview-compact.png"), 560, 430);
                if (!confirm) { dialog.Close(); return; }
                var acknowledgement = Descendants(dialog).OfType<CheckBox>().Single(check => check.Content as string == "I have a backup and have stopped work that uses this disk.");
                acknowledgement.IsChecked = true; acknowledgement.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Descendants(dialog).OfType<CheckBox>().Single(check => check.Content as string == "Shut down all WSL distributions before compaction").IsChecked = true;
                Assert.True(button.IsEnabled); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        }, Dispatcher.CurrentDispatcher);
        try
        {
            Task Preview() => (Task)type.GetMethod("PreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
            var preview = Preview(); PumpUntil(() => preview.IsCompleted, TimeSpan.FromSeconds(5)); preview.GetAwaiter().GetResult();
            if (failure is not null) throw failure;
            Assert.Equal(0, executions);
            confirm = true; preview = Preview(); PumpUntil(() => preview.IsCompleted, TimeSpan.FromSeconds(5)); preview.GetAwaiter().GetResult();
            if (failure is not null) throw failure;
            Assert.Equal(1, executions); Assert.True(shutdownRequested); Assert.Contains("Owned fixture compaction complete", ((TextBlock)view.FindName("StatusText")).Text);
            var sourceType = typeof(TaskCompletionSource<>).MakeGenericType(resultType);
            object source = Activator.CreateInstance(sourceType)!;
            object pending = sourceType.GetProperty("Task")!.GetValue(source)!;
            SetTask("Execute", result, null, pending);
            preview = Preview();
            var owner = Window.GetWindow(view)!;
            var tabs = Descendants(owner).OfType<TabControl>().Single();
            try
            {
                Assert.False(preview.IsCompleted);
                tabs.SelectedIndex = 0;
                PumpUntil(() => !view.IsLoaded, TimeSpan.FromSeconds(3));
                owner.Close();
                Assert.True(owner.IsVisible); Assert.False(preview.IsCompleted);
            }
            finally
            {
                sourceType.GetMethod("TrySetResult")!.Invoke(source, [result]);
                PumpUntil(() => preview.IsCompleted, TimeSpan.FromSeconds(3)); preview.GetAwaiter().GetResult();
                tabs.SelectedIndex = 1;
            }
        }
        finally { timer.Stop(); rows.Clear(); type.GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null); }

        void SetTask(string name, object value, Action<object>? onCall = null, object? providedTask = null)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            var invoke = property.PropertyType.GetMethod("Invoke")!;
            var arguments = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
            var returnType = invoke.ReturnType; var result = returnType.GetGenericArguments()[0];
            object task = providedTask ?? typeof(Task).GetMethods().Single(method => method.Name == "FromResult").MakeGenericMethod(result).Invoke(null, [value])!;
            Func<object, object> call = request => { onCall?.Invoke(request); return task; };
            property.SetValue(view, Expression.Lambda(property.PropertyType, Expression.Convert(Expression.Invoke(Expression.Constant(call), Expression.Convert(arguments[0], typeof(object))), returnType), arguments).Compile());
        }
    }
}
