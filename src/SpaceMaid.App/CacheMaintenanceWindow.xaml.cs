using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Core.Optimization;

namespace SpaceMaid.App;

public partial class CacheMaintenanceWindow : Window
{
    private readonly ToolCacheService _service;
    private readonly string? _scope;
    private readonly CancellationToken _lifetime;
    private readonly Func<ToolCachePlan, CancellationToken, Task<ToolCacheRunResult>> _execute;
    private IReadOnlyList<ToolCacheObservation> _rows = [];
    private CancellationTokenSource? _work;
    private bool _closeAfterCancel;
    internal Func<ToolCachePlan, bool>? ConfirmMaintenance { get; set; }

    internal CacheMaintenanceWindow(ToolCacheService service, string? scope,
        Func<ToolCachePlan, CancellationToken, Task<ToolCacheRunResult>> execute, CancellationToken lifetime = default)
    {
        InitializeComponent(); _service = service; _scope = scope; _execute = execute; _lifetime = lifetime;
        Loaded += async (_, _) => { if (_rows.Count == 0) await InspectAsync(); };
    }

    internal async Task InspectAsync()
    {
        if (_work is not null) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _work = work; SetWorking(true);
        try
        {
            StatusText.Text = "Inspecting installed tools and configured cache locations…";
            var selected = (CacheList.SelectedItem as ToolCacheObservation)?.Kind;
            _rows = await _service.InspectAsync(_scope, work.Token);
            ApplyFilter(selected);
            StatusText.Text = "Native tools own maintenance. Unknown sizes and unverified resources are not advertised as reclaimable space.";
        }
        catch (Exception error) when (Expected(error)) { StatusText.Text = error is OperationCanceledException ? "Inspection cancelled." : error.Message; }
        finally { _work = null; SetWorking(false); if (_closeAfterCancel) Close(); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await InspectAsync();
    private void Search_Changed(object sender, TextChangedEventArgs e)
    { if (CacheList is not null) ApplyFilter((CacheList.SelectedItem as ToolCacheObservation)?.Kind); }
    private void ApplyFilter(ToolCacheKind? selected)
    {
        string query = SearchBox.Text.Trim();
        CacheList.ItemsSource = _rows.Where(row => query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Status.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Root?.Contains(query, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        CacheList.SelectedItem = CacheList.Items.Cast<ToolCacheObservation>().FirstOrDefault(row => row.Kind == selected)
            ?? CacheList.Items.Cast<ToolCacheObservation>().FirstOrDefault();
    }
    private void Cache_Changed(object sender, SelectionChangedEventArgs e)
    {
        var row = CacheList.SelectedItem as ToolCacheObservation;
        DetailHeading.Text = row?.Name ?? "Choose a tool"; RootText.Text = row?.Root ?? "";
        DetailText.Text = row?.Details ?? ""; RestoreText.Text = row?.Restoration ?? "";
        ResourceList.ItemsSource = row?.Resources;
        ResourceList.Visibility = row?.Resources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CommandText.Text = ReportText.Text = "";
        ReviewButton.IsEnabled = _work is null && row?.CanPrepare == true;
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || CacheList.SelectedItem is not ToolCacheObservation { CanPrepare: true } row) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _work = work; SetWorking(true);
        try
        {
            StatusText.Text = "Rechecking the native tool, exact cache root and maintenance contract…";
            var plan = await _service.PrepareAsync(row, work.Token);
            CommandText.Text = plan.CommandPreview;
            bool confirmed = ConfirmMaintenance?.Invoke(plan) ?? MessageBox.Show(this,
                plan.Action + "\n\n" + plan.Observation.Root + "\n\n" + plan.Consequence
                    + "\n\nThis native operation can change multiple cache records. The automatic optimizer's byte cap does not apply.",
                "Tool cache maintenance", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!confirmed) { StatusText.Text = "Maintenance cancelled before any native cleanup started."; return; }
            StatusText.Text = "Running " + plan.Action + "…";
            var result = await _execute(plan, work.Token);
            StatusText.Text = result.Summary;
            ReportText.Text = result.Output + (result.Error.Length > 0 ? "\n" + result.Error : "");
        }
        catch (Exception error) when (Expected(error))
        { StatusText.Text = error is OperationCanceledException ? "Maintenance cancelled before execution." : error.Message; }
        finally { _work = null; SetWorking(false); if (_closeAfterCancel) Close(); }
    }
    private void SetWorking(bool busy)
    {
        RefreshButton.IsEnabled = CacheList.IsEnabled = SearchBox.IsEnabled = !busy;
        ReviewButton.IsEnabled = !busy && CacheList.SelectedItem is ToolCacheObservation { CanPrepare: true };
        CloseButton.Content = busy ? "Cancel" : "Close";
    }
    private void Close_Click(object sender, RoutedEventArgs e) { if (_work is { } work) work.Cancel(); else Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    { if (_work is { } work) { e.Cancel = true; _closeAfterCancel = true; work.Cancel(); } }
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException;
}
