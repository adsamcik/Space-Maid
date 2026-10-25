using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SpaceMaid.Extensions.Agents;

public partial class OpenCodeSessionsView : UserControl
{
    private CancellationToken _lifetime;
    private CancellationTokenSource? _work;
    private Task? _operation;
    private OpenCodeSessionSnapshot? _snapshot;
    private int _observedAge = 30;
    private bool _deleting;
    private Window? _owner;
    internal Func<OpenCodeConnection, int, CancellationToken, Task<OpenCodeSessionSnapshot>> Inspector { get; set; } = OpenCodeSessions.InspectAsync;
    internal Func<OpenCodeConnection, OpenCodeSessionSnapshot, OpenCodeSession, int, CancellationToken, Task<OpenCodeSessionPlan>> Preparer { get; set; } = OpenCodeSessions.PrepareAsync;
    internal Func<OpenCodeConnection, OpenCodeSessionPlan, CancellationToken, Task<OpenCodeSessionResult>> Executor { get; set; } = OpenCodeSessions.ExecuteAsync;

    public OpenCodeSessionsView()
    {
        InitializeComponent();
        Loaded += (_, _) => AttachOwner();
        Unloaded += (_, _) => { if (!_deleting) { _work?.Cancel(); DetachOwner(); } };
    }
    internal void Initialize(CancellationToken lifetime) => _lifetime = lifetime;
    private void AttachOwner()
    {
        var owner = Window.GetWindow(this);
        if (ReferenceEquals(owner, _owner)) return;
        DetachOwner(); _owner = owner;
        if (_owner is not null) _owner.Closing += Owner_Closing;
    }
    private void DetachOwner()
    { if (_owner is not null) _owner.Closing -= Owner_Closing; _owner = null; }
    private void Owner_Closing(object? sender, CancelEventArgs e)
    { if (_deleting) { e.Cancel = true; StatusText.Text = "Wait for the OpenCode cleanup result before closing the app."; } }
    private void Inputs_Changed(object sender, TextChangedEventArgs e) => Invalidate();
    private void Password_Changed(object sender, RoutedEventArgs e) => Invalidate();
    private void Invalidate()
    {
        if (SessionList is null) return;
        _work?.Cancel(); _snapshot = null; SessionList.ItemsSource = null; ReviewButton.IsEnabled = false;
        StatusText.Text = "Settings changed. Inspect sessions to refresh the list.";
    }
    private int CurrentAge()
    {
        if (!int.TryParse(AgeBox.Text, out int age) || age is < 1 or > 3650)
            throw new InvalidOperationException("Enter an age from 1 through 3650 days.");
        return age;
    }
    private OpenCodeConnection CurrentConnection() => new(ServerPassword.Password);
    private async void Inspect_Click(object sender, RoutedEventArgs e) => await InspectAsync();
    internal Task InspectAsync() => RunAsync(async token =>
    {
        var connection = CurrentConnection(); int age = CurrentAge();
        _snapshot = null; SessionList.ItemsSource = null; StatusText.Text = "Inspecting local OpenCode session state…";
        var snapshot = await Inspector(connection, age, token); token.ThrowIfCancellationRequested();
        _snapshot = snapshot; _observedAge = age; SessionList.ItemsSource = snapshot.Sessions;
        StatusText.Text = $"{snapshot.Sessions.Count:N0} sessions in {snapshot.Server.Directory} · {snapshot.Sessions.Count(session => session.CanDelete):N0} old, idle and standalone";
        DiagnosticText.Text = $"OpenCode {snapshot.Server.Version}{Environment.NewLine}Project: {snapshot.Server.Directory}{Environment.NewLine}State: {snapshot.Server.StateDirectory}";
    });
    private async void Review_Click(object sender, RoutedEventArgs e) => await ReviewCleanupAsync();
    internal Task ReviewCleanupAsync()
    {
        if (_snapshot is not { } snapshot || SessionList.SelectedItem is not OpenCodeSession { CanDelete: true } selected)
            return Task.CompletedTask;
        return RunAsync(async token =>
        {
            var connection = CurrentConnection();
            StatusText.Text = "Checking this session's identity, activity and children…";
            var plan = await Preparer(connection, snapshot, selected, _observedAge, token); token.ThrowIfCancellationRequested();
            var preview = new OpenCodeSessionPreview(plan) { Owner = Window.GetWindow(this) };
            if (preview.ShowDialog() != true) { StatusText.Text = "Cleanup cancelled. No deletion started."; return; }
            token.ThrowIfCancellationRequested(); _deleting = true; AttachOwner(); CancelButton.Content = "Stop waiting";
            _snapshot = null; SessionList.ItemsSource = null;
            StatusText.Text = "Requesting session deletion… Stopping the wait may not stop OpenCode's deletion.";
            var result = await Executor(connection, plan, token);
            StatusText.Text = result.Message;
        });
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_work is not null || _lifetime.IsCancellationRequested) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _work = work;
        InspectButton.IsEnabled = AgeBox.IsEnabled = PasswordRow.IsEnabled = ReviewButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        _operation = CoreAsync();
        try { await _operation; }
        finally
        {
            _work = null; _operation = null; _deleting = false;
            InspectButton.IsEnabled = AgeBox.IsEnabled = PasswordRow.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed; CancelButton.Content = "Cancel"; UpdateSelection();
            if (!IsLoaded) DetachOwner();
        }
        async Task CoreAsync()
        {
            try { await action(work.Token); }
            catch (Exception error) when (OpenCodeSessions.Expected(error))
            {
                _snapshot = null; SessionList.ItemsSource = null; DiagnosticText.Text = OpenCodeSessions.SafeError(error);
                StatusText.Text = _deleting ? "The deletion result is uncertain. Inspect again before retrying."
                    : error is OperationCanceledException ? "Cancelled. No deletion started."
                    : "Local OpenCode inspection failed. " + OpenCodeSessions.SafeError(error);
            }
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void UpdateSelection()
    {
        if (DetailsText is null || ReviewButton is null) return;
        var selected = SessionList.SelectedItem as OpenCodeSession;
        ReviewButton.IsEnabled = _work is null && selected?.CanDelete == true;
        DetailsText.Text = selected is null ? "Only old idle standalone sessions without sharing or managed workspaces can be removed. Disk recovery is unknown."
            : selected.CanDelete ? selected.Id + " · " + selected.Directory
            : selected.Shared ? "Shared sessions are kept because OpenCode may remove their remote share during deletion."
            : selected.ManagedWorkspace ? "Workspace-managed sessions are kept; review them in OpenCode."
            : selected.ParentId is not null ? "Child sessions are kept with their parent to preserve active work and recovery context."
            : !selected.OldEnough ? "This session has recent activity and is kept." : "This session is busy or retrying and is kept.";
    }
    internal async Task ShutdownAsync()
    {
        _work?.Cancel(); if (_operation is { } operation) await operation;
        ServerPassword.Clear(); DetachOwner();
    }
}
