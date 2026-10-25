using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace SpaceMaid.Extensions.Containers;

public partial class ContainersView : UserControl
{
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _work;
    private Task? _inspection;
    private IReadOnlyList<DockerStorageItem> _items = [];
    internal Func<CancellationToken, Task<IReadOnlyList<DockerStorageItem>>> Inspector { get; set; } = DockerUsage.InspectAsync;

    internal ContainersView(CancellationToken lifetime)
    {
        _lifetime = lifetime;
        InitializeComponent();
        KindPicker.ItemsSource = new[] { "Containers", "Images", "Build cache", "Volumes" };
        KindPicker.SelectedIndex = 0;
        Unloaded += (_, _) => _work?.Cancel();
    }
    private async void Inspect_Click(object sender, RoutedEventArgs e) => await InspectAsync();
    internal async Task InspectAsync()
    {
        if (_work is not null || _lifetime.IsCancellationRequested) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _work = work;
        _inspection = InspectCoreAsync(work.Token);
        try { await _inspection; }
        finally { _inspection = null; _work = null; UpdateSelection(); }
    }
    private async Task InspectCoreAsync(CancellationToken token)
    {
        string? selected = (StorageList.SelectedItem as DockerStorageItem)?.Id;
        InspectButton.IsEnabled = false; CancelButton.Visibility = Visibility.Visible;
        // A refresh invalidates old cleanup advice immediately; failure cannot leave stale commands enabled.
        _items = []; Filter(); CommandText.Text = ""; CopyButton.IsEnabled = false;
        StatusText.Text = "Inspecting local Docker…"; DiagnosticText.Text = "";
        try
        {
            var items = await Inspector(token);
            token.ThrowIfCancellationRequested();
            _items = items; Filter();
            StorageList.SelectedItem = StorageList.Items.Cast<DockerStorageItem>().FirstOrDefault(item => item.Id == selected);
            StatusText.Text = $"Local Docker · {items.Count:N0} objects · Inspected {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        { StatusText.Text = token.IsCancellationRequested ? "Inspection cancelled. Inspect again when ready." : "Docker did not respond in time. Check Docker Desktop and retry."; }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        { StatusText.Text = "Local Docker could not be inspected. Check Docker Desktop is running and retry."; DiagnosticText.Text = error.Message; }
        finally { InspectButton.IsEnabled = true; CancelButton.Visibility = Visibility.Collapsed; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => Filter();
    private void Search_Changed(object sender, TextChangedEventArgs e) => Filter();
    private void Filter()
    {
        if (StorageList is null || KindPicker is null || SearchBox is null) return;
        string kind = KindPicker.SelectedItem as string ?? "Containers", search = SearchBox.Text.Trim();
        string? selected = (StorageList.SelectedItem as DockerStorageItem)?.Id;
        StorageList.ItemsSource = _items.Where(item => item.Kind == kind && (search.Length == 0
            || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || item.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.State.Contains(search, StringComparison.OrdinalIgnoreCase))).OrderByDescending(item => item.Bytes).ThenBy(item => item.Name).ToArray();
        StorageList.SelectedItem = StorageList.Items.Cast<DockerStorageItem>().FirstOrDefault(item => item.Id == selected);
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
        => UpdateSelection();
    private void UpdateSelection()
    {
        if (DetailsText is null) return;
        var item = StorageList.SelectedItem as DockerStorageItem;
        DetailsText.Text = item is null ? "Choose an object to review its data and cleanup options." : item.Details;
        CommandText.Text = item?.Command ?? "";
        CommandText.Visibility = item?.CanCopy == true ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = item?.CanCopy == true && _work is null;
        CopyButton.Content = item?.Kind == "Build cache" ? "Copy builder cleanup command" : "Copy cleanup command";
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || StorageList.SelectedItem is not DockerStorageItem { Command: { } command }) return;
        try { Clipboard.SetText(command); StatusText.Text = "Command copied. Review its scope and preserve needed data before running it in your terminal."; }
        catch (ExternalException) { StatusText.Text = "Clipboard unavailable. Copy the visible command text instead."; }
    }
    private void Guide_Navigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { StatusText.Text = "Could not open the guide. Use the visible link in your browser."; }
    }
    internal async Task ShutdownAsync()
    { _work?.Cancel(); if (_inspection is { } inspection) await inspection; }
}
