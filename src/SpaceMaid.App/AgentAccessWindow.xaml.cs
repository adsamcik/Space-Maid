using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using SpaceMaid.App.AgentAccess;

namespace SpaceMaid.App;

public partial class AgentAccessWindow : Window
{
    private readonly Func<string, IReadOnlyList<string>, AgentAccessLevel, CancellationToken, Task<AgentAggregateSession>> _create;
    private readonly Func<AgentAggregateSession, Task> _review;
    private readonly CancellationTokenSource _lifetime = new();
    private AgentAggregateSession? _session;
    private AgentConnection? _connection;
    private bool _busy, _closing, _shutdownComplete;

    internal AgentAccessWindow(string root,
        Func<string, IReadOnlyList<string>, AgentAccessLevel, CancellationToken, Task<AgentAggregateSession>> create,
        Func<AgentAggregateSession, Task> review)
    {
        InitializeComponent(); ScopeBox.Text = root; _create = create; _review = review;
        Closing += OnClosing;
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_session is not null)
        {
            _busy = true; ShareButton.IsEnabled = false;
            try { await StopSharingAsync(); }
            finally { _busy = false; if (!_closing) ShareButton.IsEnabled = true; }
            return;
        }
        _busy = true; ShareButton.IsEnabled = false; ScopeControls.IsEnabled = false;
        try
        {
            var exclusions = ExclusionsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var session = await _create(ScopeBox.Text, exclusions, (AgentAccessLevel)LevelBox.SelectedIndex, _lifetime.Token);
            if (_closing) { session.Dispose(); return; }
            _session = session;
            _connection = new AgentConnection(CallAsync, session.Level);
            SetupBox.Text = _connection.Configuration(Environment.ProcessPath ?? throw new InvalidOperationException("App location unavailable."));
            SetupExpander.IsEnabled = true;
            GroupsGrid.ItemsSource = session.Groups;
            SummaryText.Text = $"{session.Overview.FileNames:N0} file names · {SizeText.Format(session.Overview.LogicalBytes)} logical"
                + (session.Overview.AllocatedBytes is { } bytes ? $" · {SizeText.Format(bytes)} allocated" : " · Allocation unknown")
                + (session.Overview.Coverage.Complete ? " · Complete metadata traversal" : " · Partial or retained metadata")
                + (session.Overview.Coverage.ClassificationsComplete ? "" : " · Cleanup classifications incomplete")
                + ". Group sizes can overlap; recovery is unknown.";
            AccessStatus.Text = "Sharing · " + ((System.Windows.Controls.ComboBoxItem)LevelBox.SelectedItem).Content;
            ShareButton.Content = "Stop sharing";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException or OverflowException)
        {
            await StopSharingAsync();
            if (!_closing) AccessStatus.Text = "Could not share. Scan a regular folder and check exclusions, then try again.";
        }
        finally { _busy = false; if (!_closing) { ShareButton.IsEnabled = true; ScopeControls.IsEnabled = _session is null; } }
    }

    private Task<object> CallAsync(string name, JsonElement arguments) => Dispatcher.InvokeAsync(() =>
    {
        if (_closing || _session is null) throw new InvalidOperationException();
        try
        {
            var output = AgentProtocol.Execute(_session, name, arguments);
            AccessStatus.Text = name == "suggest_review" ? "Suggestion waiting for your review" : "Sharing · Agent queried aggregates";
            UpdateSuggestion();
            return output;
        }
        catch (InvalidOperationException)
        {
            _ = Dispatcher.BeginInvoke(new Action(async () => { await StopSharingAsync(); AccessStatus.Text = "Access ended · Scan changed or became stale. Start a new session."; }));
            throw;
        }
    }).Task;

    private void UpdateSuggestion()
    {
        SuggestionText.Text = _session?.Suggestion is { } suggestion
            ? $"Agent note · verify yourself: {suggestion.AgentNote} ({suggestion.Groups.Count} {(suggestion.Groups.Count == 1 ? "group" : "groups")})" : "";
        ReviewButton.IsEnabled = DismissButton.IsEnabled = _session?.Suggestion is not null;
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _session is not { } session) return;
        _busy = true; ReviewButton.IsEnabled = DismissButton.IsEnabled = ShareButton.IsEnabled = false;
        try
        {
            // Stop external requests before entering the human-only live review.
            if (_connection is not null) { await _connection.DisposeAsync(); _connection = null; }
            SetupBox.Clear(); SetupExpander.IsEnabled = false;
            await _review(session);
            AccessStatus.Text = "Review finished. Scan again and start a new session to evaluate the result.";
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or OperationCanceledException)
        { AccessStatus.Text = "Review unavailable. Finish current work and refresh the scan, then start a new session."; }
        finally { await StopSharingAsync(clearStatus: false); _busy = false; if (!_closing) ShareButton.IsEnabled = true; }
    }
    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        try { _session?.Dismiss(); UpdateSuggestion(); AccessStatus.Text = "Sharing · Suggestion dismissed"; }
        catch (InvalidOperationException) { AccessStatus.Text = "Access ended. Start a new session."; }
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { if (_session is not null && SetupBox.Text.Length > 0) Clipboard.SetText(SetupBox.Text); }
        catch (System.Runtime.InteropServices.ExternalException) { AccessStatus.Text = "Clipboard unavailable. Select and copy the setup text."; }
    }
    internal async Task StopSharingAsync(bool clearStatus = true)
    {
        _session?.Dispose(); _session = null;
        var connection = _connection; _connection = null;
        if (connection is not null) await connection.DisposeAsync();
        SetupBox.Clear(); SetupExpander.IsEnabled = false; ShareButton.Content = "Start sharing";
        ScopeControls.IsEnabled = true; GroupsGrid.ItemsSource = null; UpdateSuggestion();
        if (!_closing && clearStatus) AccessStatus.Text = "Sharing off";
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) { e.Cancel = !_shutdownComplete; return; }
        e.Cancel = true; _closing = true; _lifetime.Cancel();
        await StopSharingAsync();
        _lifetime.Dispose(); _shutdownComplete = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
