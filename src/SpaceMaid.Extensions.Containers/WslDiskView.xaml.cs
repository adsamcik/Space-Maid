using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpaceMaid.Extensions.Containers;

internal sealed record WslDiskRow(string Owner, string Path, long Length)
{
    public string Size => $"{Length / 1073741824d:0.##} GiB";
}

public partial class WslDiskView : UserControl
{
    private readonly CancellationToken _lifetime;
    private readonly List<WslDiskRow> _disks = [];
    private bool _busy;
    private Task? _execution;
    private Window? _owner;
    internal Func<string, CancellationToken, Task<VirtualDiskTarget>> Prepare { get; set; } = WslDiskClient.PreviewAsync;
    internal Func<VirtualDiskRequest, CancellationToken, Task<VirtualDiskResult>> Execute { get; set; } = WslDiskClient.ExecuteAsync;
    public WslDiskView() : this(CancellationToken.None) { }
    internal WslDiskView(CancellationToken lifetime)
    {
        _lifetime = lifetime; InitializeComponent();
        Loaded += (_, _) =>
        {
            var owner = Window.GetWindow(this);
            if (owner == _owner) return;
            DetachOwner(); _owner = owner;
            if (_owner is not null) _owner.Closing += Owner_Closing;
        };
        Unloaded += (_, _) => { if (!_busy) DetachOwner(); };
    }
    private void DetachOwner()
    { if (_owner is not null) _owner.Closing -= Owner_Closing; _owner = null; }
    private void Owner_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true; StatusText.Text = "Wait for Windows to finish. Closing now cannot undo compaction.";
    }
    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var disks = await Task.Run(Discover, _lifetime);
            _disks.Clear(); _disks.AddRange(disks); Publish();
            StatusText.Text = disks.Count == 0 ? "No registered WSL2 ext4.vhdx files found. Add a disk for Docker Desktop or a custom location." : $"{disks.Count} WSL2 disks from up to 128 registrations. Add any other disk explicitly; preview checks the exact file before confirmation.";
        }
        catch (Exception error) when (Expected(error)) { StatusText.Text = error.Message; }
        finally { SetBusy(false); }
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picker = new OpenFileDialog { Filter = "Virtual hard disk (*.vhdx)|*.vhdx", CheckFileExists = true, Multiselect = false, Title = "Choose the exact virtual disk to compact" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            string path = VirtualDiskCleanup.ValidatePath(picker.FileName);
            if (!_disks.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) _disks.Add(new("Custom · verify owner", path, new FileInfo(path).Length));
            Publish(); DiskList.SelectedItem = _disks.First(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (Expected(error)) { StatusText.Text = error.Message; }
    }
    private void Publish()
    {
        string? selectedPath = (DiskList.SelectedItem as WslDiskRow)?.Path;
        DiskList.ItemsSource = _disks.OrderByDescending(item => item.Length).ToArray();
        DiskList.SelectedItem = DiskList.Items.Cast<WslDiskRow>().FirstOrDefault(item => item.Path == selectedPath);
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    { if (PreviewButton is not null) PreviewButton.IsEnabled = !_busy && DiskList.SelectedItem is WslDiskRow; }
    private async void Preview_Click(object sender, RoutedEventArgs e) => await PreviewAsync();
    internal async Task PreviewAsync()
    {
        if (_busy || DiskList.SelectedItem is not WslDiskRow row) return;
        SetBusy(true);
        try
        {
            var target = await Prepare(row.Path, _lifetime);
            var preview = new WslDiskPreview(target, row.Owner) { Owner = Window.GetWindow(this) };
            if (preview.ShowDialog() != true) return;
            StatusText.Text = "Waiting for Windows compaction. Keep this window open; this operation cannot be rolled back by Cancel.";
            _execution = ExecuteCoreAsync(new(target, preview.ShutdownWsl));
            await _execution;
        }
        catch (Exception error) when (Expected(error)) { StatusText.Text = error is Win32Exception { NativeErrorCode: 1223 } ? "Administrator approval was cancelled. Compaction was not started." : error.Message; }
        finally { _execution = null; SetBusy(false); }
    }
    private async Task ExecuteCoreAsync(VirtualDiskRequest request)
    {
        var result = await Execute(request, _lifetime);
        StatusText.Text = result.Message;
        if (result.Succeeded && result.BeforeBytes is { } before && result.AfterBytes is { } after)
        {
            StatusText.Text += $" File length: {before / 1073741824d:0.##} → {after / 1073741824d:0.##} GiB. Host allocation savings may differ.";
            int index = _disks.FindIndex(item => item.Path.Equals(request.Target.Path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _disks[index] = _disks[index] with { Length = after };
            Publish();
        }
    }
    internal Task ShutdownAsync() => _execution ?? Task.CompletedTask;
    private void SetBusy(bool busy)
    {
        _busy = busy; AddButton.IsEnabled = FindButton.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy && DiskList.SelectedItem is WslDiskRow;
        if (!busy && !IsLoaded) DetachOwner();
    }
    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or OperationCanceledException or System.Text.Json.JsonException;
    internal static IReadOnlyList<WslDiskRow> Discover()
    {
        var result = new List<WslDiskRow>();
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        foreach (string name in key?.GetSubKeyNames().Take(128) ?? [])
        {
            using var distro = key!.OpenSubKey(name);
            if (distro?.GetValue("Version") is not int version || version != 2 || distro.GetValue("BasePath") is not string path) continue;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            path = System.IO.Path.Join(path, "ext4.vhdx");
            try { path = VirtualDiskCleanup.ValidatePath(path); }
            catch (InvalidOperationException) { continue; }
            if (File.Exists(path)) result.Add(new(distro.GetValue("DistributionName") as string ?? "WSL2", path, new FileInfo(path).Length));
        }
        return result;
    }
}

internal sealed class WslDiskPreview : Window
{
    private readonly CheckBox _backup = new() { Content = "I have a backup and have stopped work that uses this disk.", Margin = new(0, 12, 0, 8) };
    private readonly CheckBox _shutdown = new() { Content = "Shut down all WSL distributions before compaction", Margin = new(0, 0, 0, 12) };
    internal bool ShutdownWsl => _shutdown.IsChecked == true;
    internal WslDiskPreview(VirtualDiskTarget target, string owner)
    {
        Title = "Preview disk compaction"; Width = 650; Height = 480; MinWidth = 520; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackground"); SetResourceReference(ForegroundProperty, "WindowForeground");
        var panel = new DockPanel { Margin = new(22) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new(0, 0, 8, 0), Padding = new(14, 6, 14, 6) };
        var confirm = new Button { Content = "Compact this disk…", IsEnabled = false, Padding = new(14, 6, 14, 6) };
        confirm.Click += (_, _) => DialogResult = true; _backup.Click += (_, _) => confirm.IsEnabled = _backup.IsChecked == true;
        controls.Children.Add(cancel); controls.Children.Add(confirm); DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = owner, FontSize = 18, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBox { Text = target.Path, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) });
        content.Children.Add(new TextBlock { Text = $"Observed file length: {target.Length / 1073741824d:0.##} GiB · Expected recovery: unknown", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Windows will compact zero-filled blocks in this exact backing disk. Guest files and virtual capacity stay in place. This modifies the disk directly, outside the Recycle Bin; there is no app rollback. Windows checks that it is detached, dynamic and supported. Close Docker Desktop and virtual machines first. Administrator approval is requested only after this confirmation.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
        content.Children.Add(_backup); content.Children.Add(_shutdown);
        content.Children.Add(new TextBlock { Text = "WSL shutdown interrupts every WSL distribution and dependent container. If the disk changes while shutting down, compaction is refused and you must preview again. Do not close Space Maid while Windows is compacting.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = panel;
    }
}
