using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SpaceMaid.Extensions.Models;

public partial class ModelsView : UserControl
{
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _work;
    private Task? _operation;
    private bool _deleting;
    private OllamaSnapshot? _snapshot;
    internal Func<CancellationToken, Task<OllamaSnapshot>> Inspector { get; set; } = OllamaCleanup.InspectAsync;
    internal Func<OllamaModel, CancellationToken, Task<OllamaCleanupPlan>> Preparer { get; set; } = OllamaCleanup.PrepareAsync;
    internal Func<OllamaCleanupPlan, CancellationToken, Task<OllamaCleanupResult>> Executor { get; set; } = OllamaCleanup.ExecuteAsync;

    internal ModelsView(CancellationToken lifetime)
    {
        _lifetime = lifetime;
        InitializeComponent();
        DiagnosticText.Text = "Local Ollama only: 127.0.0.1:11434. No proxy, redirects or remote host settings are used. Sizes can include shared model data.";
        Unloaded += (_, _) => _work?.Cancel();
    }
    private async void Inspect_Click(object sender, RoutedEventArgs e) => await InspectAsync();
    internal Task InspectAsync() => RunAsync(async token =>
    {
        string? selectedName = (ModelList.SelectedItem as OllamaModel)?.Name;
        _snapshot = null; Filter();
        StatusText.Text = "Inspecting local Ollama…";
        var snapshot = await Inspector(token);
        token.ThrowIfCancellationRequested();
        _snapshot = snapshot; Filter();
        ModelList.SelectedItem = ModelList.Items.Cast<OllamaModel>().FirstOrDefault(model => model.Name == selectedName);
        StatusText.Text = $"Local Ollama · {snapshot.Models.Count:N0} models · Inspected {snapshot.ObservedAt.LocalDateTime:HH:mm:ss}";
    });
    private async void Review_Click(object sender, RoutedEventArgs e) => await ReviewCleanupAsync();
    internal Task ReviewCleanupAsync()
    {
        if (ModelList.SelectedItem is not OllamaModel { CanDelete: true } selected) return Task.CompletedTask;
        return RunAsync(async token =>
        {
            StatusText.Text = "Checking the selected model and loaded state…";
            var plan = await Preparer(selected, token);
            token.ThrowIfCancellationRequested();
            var preview = new ModelCleanupWindow(plan) { Owner = Window.GetWindow(this) };
            if (preview.ShowDialog() != true)
            { StatusText.Text = "Cleanup cancelled. No deletion started."; return; }
            token.ThrowIfCancellationRequested();
            _deleting = true; CancelButton.Content = "Stop waiting";
            StatusText.Text = "Removing the confirmed model… Stopping the wait may not stop Ollama's deletion.";
            _snapshot = null; Filter();
            var result = await Executor(plan, token);
            _snapshot = result.Snapshot; Filter();
            StatusText.Text = result.Message;
        });
    }
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (_work is not null || _lifetime.IsCancellationRequested) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _work = cancellation; _deleting = false;
        InspectButton.IsEnabled = false; ReviewButton.IsEnabled = false; CancelButton.Visibility = Visibility.Visible;
        _operation = RunCoreAsync(work, cancellation.Token);
        try { await _operation; }
        finally
        {
            _operation = null; _work = null; _deleting = false;
            InspectButton.IsEnabled = true; CancelButton.Visibility = Visibility.Collapsed; CancelButton.Content = "Cancel";
            UpdateSelection();
        }
    }
    private async Task RunCoreAsync(Func<CancellationToken, Task> work, CancellationToken token)
    {
        try { await work(token); }
        catch (Exception error) when (OllamaCleanup.Expected(error))
        {
            _snapshot = null; Filter(); DiagnosticText.Text = error.Message;
            StatusText.Text = _deleting ? "Deletion may have reached Ollama. Inspect again before retrying."
                : error is OperationCanceledException ? token.IsCancellationRequested ? "Operation cancelled. No deletion started." : "Ollama did not respond in time. Retry inspection."
                : error is InvalidOperationException or InvalidDataException ? error.Message
                : "Local Ollama could not be inspected. Check Ollama is running and retry.";
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();
    private void Search_Changed(object sender, TextChangedEventArgs e) => Filter();
    private void Filter()
    {
        if (ModelList is null || SearchBox is null) return;
        string? selected = (ModelList.SelectedItem as OllamaModel)?.Name;
        string query = SearchBox.Text.Trim();
        ModelList.ItemsSource = (_snapshot?.Models ?? []).Where(model => query.Length == 0
            || model.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || model.Digest.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(model => model.Bytes).ThenBy(model => model.Name, StringComparer.Ordinal).ToArray();
        ModelList.SelectedItem = ModelList.Items.Cast<OllamaModel>().FirstOrDefault(model => model.Name == selected);
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void UpdateSelection()
    {
        if (DetailsText is null) return;
        var selected = ModelList.SelectedItem as OllamaModel;
        ReviewButton.IsEnabled = _work is null && selected?.CanDelete == true;
        DetailsText.Text = selected is null ? "Choose a model to review cleanup. Loaded models are kept."
            : selected.Loaded ? "This model, or another tag sharing its data, is loaded. Finish sessions using it before cleanup."
            : "Remove this model through local Ollama after preview and confirmation. Keep offline or local-only models; observed size is not guaranteed savings.";
    }
    internal async Task ShutdownAsync()
    { _work?.Cancel(); if (_operation is { } operation) await operation; }
}
