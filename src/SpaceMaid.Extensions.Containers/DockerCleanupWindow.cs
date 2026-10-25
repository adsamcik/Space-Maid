using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SpaceMaid.Extensions.Containers;

internal sealed class DockerCleanupWindow : Window
{
    private readonly DockerCleanupPlan _plan;
    private readonly Func<DockerCleanupPlan, CancellationToken, Task<DockerCleanupResult>> _execute;
    private readonly CheckBox _ack = new() { Name = "PreservedCheck", Content = "I have preserved the data I need.", Margin = new(0, 12, 0, 8) };
    private readonly TextBox _name = new() { Margin = new(0, 4, 0, 12) };
    private readonly Button _confirm = new() { Name = "ConfirmButton", Content = "Delete permanently", IsEnabled = false, MinWidth = 140, Margin = new(8, 0, 0, 0) };
    private readonly Button _cancel = new() { Name = "CancelButton", Content = "Cancel", IsCancel = true, MinWidth = 80 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
    private bool _busy;
    internal DockerCleanupResult? Result { get; private set; }

    internal DockerCleanupWindow(DockerCleanupPlan plan, Func<DockerCleanupPlan, CancellationToken, Task<DockerCleanupResult>> execute)
    {
        _plan = plan; _execute = execute;
        NameScope.SetNameScope(this, new NameScope());
        RegisterName(_ack.Name, _ack); RegisterName(_confirm.Name, _confirm); RegisterName(_cancel.Name, _cancel); RegisterName("VolumeNameBox", _name);
        Title = "Review Docker cleanup"; Width = 660; Height = 540; MinWidth = 520; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackground"); SetResourceReference(ForegroundProperty, "WindowForeground");
        var panel = new DockPanel { Margin = new(20) };
        var bottom = new StackPanel(); bottom.Children.Add(_ack);
        if (plan.Target.Kind == "Volumes")
        {
            bottom.Children.Add(new TextBlock { Text = "Type the volume name to confirm:", TextWrapping = TextWrapping.Wrap });
            _name.Name = "VolumeNameBox"; bottom.Children.Add(_name);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_cancel); buttons.Children.Add(_confirm); bottom.Children.Add(buttons); bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = plan.Target.Kind + " · " + plan.Target.Name, FontWeight = FontWeights.SemiBold, FontSize = 18, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBox { Text = "ID: " + plan.Target.Id + "\nLocal engine: " + plan.EngineId, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 10) });
        content.Children.Add(new TextBlock { Text = "Observed size: " + plan.Target.SizeText + " · Reclaimable host space is unknown", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        content.Children.Add(new TextBlock { Text = plan.Target.Details, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Only this exact object is authorized. Cleanup bypasses the Recycle Bin. State is checked again; pause other Docker changes during cleanup because the API has no atomic compare-and-delete operation.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = panel;
        _ack.Checked += (_, _) => Update(); _ack.Unchecked += (_, _) => Update(); _name.TextChanged += (_, _) => Update();
        _cancel.Click += (_, _) => Close(); _confirm.Click += Confirm_Click;
        Closing += (_, args) => { if (_busy) { args.Cancel = true; _status.Text = "Waiting for Docker's cleanup result. Do not start another cleanup."; } };
    }
    private void Update() => _confirm.IsEnabled = !_busy && _ack.IsChecked == true
        && (_plan.Target.Kind != "Volumes" || _name.Text == _plan.Target.Id);
    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!_confirm.IsEnabled) return;
        _busy = true; Update(); _ack.IsEnabled = _name.IsEnabled = _cancel.IsEnabled = false;
        _status.Text = "Checking the confirmed object and cleaning up…";
        try { Result = await _execute(_plan, CancellationToken.None); }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException or System.Net.Http.HttpRequestException or OperationCanceledException)
        { Result = new(false, "Cleanup did not start: " + error.Message); }
        finally { _busy = false; }
        _status.Text = Result!.Message; _cancel.IsEnabled = true; _cancel.Content = "Close"; _confirm.Visibility = Visibility.Collapsed;
    }
}
