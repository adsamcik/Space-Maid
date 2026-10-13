using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.AI;
using Microsoft.Win32;
using SpaceMaid.Extensions.Contracts;
using SpaceMaid.LocalModel;

namespace SpaceMaid.Extensions.Assistant;

public partial class AssistantView : UserControl
{
    private sealed record Settings(bool Enabled, string? ModelPath);
    private sealed record SuggestionRow(EvidenceReviewItem Item)
    {
        public string Name => Item.Entry.Name;
        public string Path => Item.Entry.Path;
        public string Explanation => Item.UntrustedExplanation;
        public string Policy => Item.Entry.ReviewPolicy.Disposition == EvidenceReviewDisposition.Blocked ? "Blocked" : "Investigate first";
    }
    private readonly IExtensionHost _host;
    private readonly CancellationToken _lifetime;
    private readonly string _settingsPath;
    private IInventoryEvidenceSession? _session;
    private CancellationTokenSource? _work;
    private Task? _active;
    private long _version;
    private string? _root;
    private bool _initialized, _closed, _busy;
    private readonly Func<string, string, IChatClient> _createClient;

    internal AssistantView(IExtensionHost host, CancellationToken lifetime, Func<string, string, IChatClient>? createClient = null)
    {
        _host = host; _lifetime = lifetime; _settingsPath = Path.Join(host.DataDirectory, "assistant.json");
        _createClient = createClient ?? ((worker, model) => new IsolatedModelChatClient(worker, model));
        InitializeComponent();
        try
        {
            if (File.Exists(_settingsPath))
            {
                if (new FileInfo(_settingsPath).Length > 16384) throw new InvalidDataException("Settings exceed their limit.");
                var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath), new JsonSerializerOptions
                { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("Settings are missing.");
                EnabledBox.IsChecked = saved.Enabled; ModelPathBox.Text = saved.ModelPath ?? "";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { EnabledBox.IsChecked = false; StatusText.Text = "Couldn't load settings. Choose a model and enable the assistant."; }
        _initialized = true; SettingsPanel.IsExpanded = EnabledBox.IsChecked != true || string.IsNullOrEmpty(ModelPathBox.Text);
        SuggestionList.SizeChanged += (_, _) =>
        {
            // WPF retains a star column's previous extent when this window
            // shrinks. Keep the policy visible without horizontal scrolling.
            SuggestionList.Columns[1].Width = Math.Max(190, SuggestionList.ActualWidth - 240 - 125 - 24);
        };
        UpdateActions();
        // Fluent theme changes briefly unload/reload the visual tree. Revoke
        // a detached workflow after that transition, preserving live results
        // when the same view remains open under a new Windows appearance.
        Unloaded += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded) Invalidate();
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(_host.DataDirectory);
        string temporary = _settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(EnabledBox.IsChecked == true, ModelPathBox.Text)));
        File.Move(temporary, _settingsPath, true);
    }
    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        Invalidate();
        try { SaveSettings(); StatusText.Text = EnabledBox.IsChecked == true ? "Choose a scanned folder." : "Assistant disabled."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { EnabledBox.IsChecked = false; StatusText.Text = "Settings could not be saved: " + error.Message; }
        UpdateActions();
    }
    private void ChooseModel_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose a local GGUF instruction model", Filter = "GGUF models (*.gguf)|*.gguf|All files (*.*)|*.*", CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        Invalidate(); ModelPathBox.Text = picker.FileName;
        try { SaveSettings(); StatusText.Text = _root is null ? "Model selected. Choose a scanned folder." : "Model selected. Refresh to continue."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StatusText.Text = "Settings could not be saved: " + error.Message; }
        UpdateActions();
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a folder included in the current scan", Multiselect = false };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        _root = picker.FolderName; FolderText.Text = _root;
        await StartAsync(RefreshSummaryAsync);
    }
    private void Exclusions_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        Invalidate(); StatusText.Text = "Exclusions changed. Refresh to continue."; UpdateActions();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await StartAsync(RefreshSummaryAsync);
    private async void Run_Click(object sender, RoutedEventArgs e) => await StartAsync(InvestigateAsync);
    private async Task StartAsync(Func<CancellationToken, Task> action, bool bounded = true)
    {
        if (_closed || _busy) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        if (bounded) work.CancelAfter(TimeSpan.FromMinutes(6));
        _work = work; _busy = true; UpdateActions();
        try { _active = action(work.Token); await _active; }
        catch (OperationCanceledException)
        { if (!_closed) StatusText.Text = "Stopped."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or JsonException or System.ComponentModel.Win32Exception or NotSupportedException or TimeoutException or AggregateException)
        {
            Invalidate();
            if (!_closed) StatusText.Text = error.Message;
        }
        finally { _work = null; _active = null; _busy = false; if (!_closed) UpdateActions(); }
    }
    internal async Task RefreshSummaryAsync(CancellationToken token)
    {
        Invalidate(cancel: false);
        if (_root is null || _host.Inventory is not IInventoryEvidence evidence) throw new InvalidOperationException("Scan and choose a folder before opening the assistant.");
        string[] excluded = ExclusionsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long version = _version;
        var session = await evidence.OpenEvidenceSessionAsync(_root, excluded, token);
        try
        {
            var summary = await session.SummarizeAsync([session.Scope.Root.Id], token);
            token.ThrowIfCancellationRequested();
            if (_closed || version != _version) return;
            _session = session; session = null!;
            ShowEvidence(summary);
            EmptyText.Text = "No suggestions yet.";
            StatusText.Text = summary.Coverage.Complete ? "Ready."
                : summary.Coverage.WarningCount > 0 || summary.Coverage.RetainsEarlierMetadata
                    ? "Scan incomplete or out of date. Rescan this folder, then refresh."
                    : "Incomplete summary. Rescan or choose a smaller folder.";
        }
        finally { session?.Dispose(); UpdateActions(); }
    }
    private async Task InvestigateAsync(CancellationToken token)
    {
        var session = _session ?? throw new InvalidOperationException("Refresh the selected folder summary first.");
        if (EnabledBox.IsChecked != true || string.IsNullOrEmpty(ModelPathBox.Text)) throw new InvalidOperationException("Enable the assistant and choose a local GGUF model.");
        SuggestionList.ItemsSource = null; EmptyText.Visibility = Visibility.Visible;
        long version = _version;
        string worker = Path.Join(Path.GetDirectoryName(typeof(AssistantExtension).Assembly.Location)!, "Worker", "SpaceMaid.ModelWorker.exe");
        using var client = _createClient(worker, ModelPathBox.Text);
        var result = await new AssistantInvestigation(client, session).RunAsync(QuestionBox.Text, new Progress<AssistantProgress>(update =>
        { if (!_closed && version == _version) StatusText.Text = update.Phase + $" · Step {update.Turn}"; }), token);
        token.ThrowIfCancellationRequested();
        if (_closed || version != _version) return;
        // Native/model process is disposed on return, before human review.
        SuggestionList.ItemsSource = result.Suggestions.Select(item => new SuggestionRow(item)).ToArray();
        EmptyText.Visibility = result.Suggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = result.UntrustedNote.Length > 0 ? "AI note (unverified): " + result.UntrustedNote : "No suggestions found.";
        ShowEvidence(result.Summary);
        StatusText.Text = result.Suggestions.Count == 0 ? "Finished." : $"{result.Suggestions.Count:N0} suggestions · Review before cleanup";
    }
    private void ShowEvidence(EvidenceSummary summary) => EvidenceText.Text = summary.Text + "\n" + string.Join("\n", summary.Cautions)
        + $"\nSource: {summary.Coverage.Backend} · Observed {summary.Coverage.ScannedAtUtc.ToLocalTime():g}";
    private async void Review_Click(object sender, RoutedEventArgs e) => await StartAsync(async token =>
    {
        if (_session is not { } session || _host is not IExtensionEvidenceReview review) throw new InvalidOperationException("Host cleanup review is unavailable.");
        var ids = SuggestionList.SelectedItems.Cast<SuggestionRow>().Select(row => row.Item.Entry.Id).ToArray();
        var result = await review.ReviewEvidenceAsync(session, ids, token);
        Invalidate(cancel: false); StatusText.Text = result.Message + " Refresh to continue.";
    }, bounded: false);
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session || SuggestionList.SelectedItem is not SuggestionRow row) return;
        try { var entry = await session.DetailsAsync(row.Item.Entry.Id, _lifetime); _host.RevealLocation(entry.Path); }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { Invalidate(); StatusText.Text = error.Message; }
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void Cancel_Click(object sender, RoutedEventArgs e) { Invalidate(); StatusText.Text = "Stopping…"; }
    private void Invalidate(bool cancel = true)
    {
        _version++; if (cancel) _work?.Cancel();
        _session?.Dispose(); _session = null;
        SuggestionList.ItemsSource = null; EmptyText.Visibility = Visibility.Visible; EvidenceText.Text = "";
        EmptyText.Text = _root is null ? "Choose a scanned folder to begin." : "Refresh to find suggestions.";
        UpdateActions();
    }
    private void UpdateActions()
    {
        if (!_initialized) return;
        ModelButton.IsEnabled = FolderButton.IsEnabled = ExclusionsBox.IsEnabled = QuestionBox.IsEnabled = !_busy;
        RefreshButton.IsEnabled = !_busy && _root is not null;
        RunButton.IsEnabled = !_busy && EnabledBox.IsChecked == true && _session is not null && !string.IsNullOrEmpty(ModelPathBox.Text);
        CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressStrip.Visibility = _busy ? Visibility.Visible : Visibility.Hidden;
        OpenButton.IsEnabled = !_busy && _session is not null && SuggestionList.SelectedItem is SuggestionRow;
        ReviewButton.IsEnabled = !_busy && _session is not null && _host is IExtensionEvidenceReview && SuggestionList.SelectedItems.Count > 0
            && SuggestionList.SelectedItems.Cast<SuggestionRow>().All(row => row.Item.Entry.IsDirectory);
    }
    internal async Task ShutdownAsync()
    {
        _closed = true; Invalidate();
        if (_active is not null) try { await _active; } catch (Exception) { }
    }
}
