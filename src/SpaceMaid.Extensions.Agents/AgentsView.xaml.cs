using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SpaceMaid.Extensions.Agents;

public partial class AgentsView : UserControl
{
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _work;
    private Task? _operation;
    private Window? _owner;
    private bool _recycling, _changingSource;
    private int _observedAge = 30;
    private readonly Dictionary<int, string> _homes = [];
    internal Func<IReadOnlyList<AgentHome>, int, CancellationToken, Task<AgentHistorySnapshot>> Inspector { get; set; } = AgentHistoryCleanup.InspectAsync;
    internal Func<IReadOnlyList<AgentHistoryItem>, int, CancellationToken, Task<AgentHistoryPlan>> Preparer { get; set; }
        = (items, days, token) => Task.Run(() => AgentHistoryCleanup.Prepare(items, days, token), token);
    internal Func<AgentHistoryPlan, nint, CancellationToken, Task<AgentHistoryResult>> Executor { get; set; }
        = (plan, owner, token) => AgentHistoryCleanup.ExecuteAsync(plan, owner, token);

    internal AgentsView(CancellationToken lifetime)
    {
        _lifetime = lifetime; InitializeComponent();
        Loaded += (_, _) => AttachOwner();
        Unloaded += (_, _) => { if (!_recycling) { _work?.Cancel(); DetachOwner(); } };
    }
    private void AttachOwner()
    {
        Window? owner = Window.GetWindow(this);
        if (ReferenceEquals(owner, _owner)) return;
        DetachOwner(); _owner = owner;
        if (_owner is not null) _owner.Closing += Owner_Closing;
    }
    private void DetachOwner()
    { if (_owner is not null) _owner.Closing -= Owner_Closing; _owner = null; }
    private void Owner_Closing(object? sender, CancelEventArgs e)
    { if (_recycling) { e.Cancel = true; StatusText.Text = "Wait for Windows recycling to finish before closing the app."; } }
    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HomeRow is null || HomeBox is null) return;
        _changingSource = true;
        try
        {
            HomeRow.Visibility = ToolPicker.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (ToolPicker.SelectedIndex > 0)
                HomeBox.Text = _homes.GetValueOrDefault(ToolPicker.SelectedIndex) ?? AgentHistoryCleanup.DefaultHomes()[ToolPicker.SelectedIndex - 1].Path;
        }
        finally { _changingSource = false; }
        Invalidate();
    }
    private void Home_Changed(object sender, TextChangedEventArgs e)
    {
        if (_changingSource || ToolPicker is null || ToolPicker.SelectedIndex <= 0) return;
        _homes[ToolPicker.SelectedIndex] = HomeBox.Text; Invalidate();
    }
    private void Age_Changed(object sender, TextChangedEventArgs e) => Invalidate();
    private void Invalidate()
    {
        if (HistoryList is null) return;
        _work?.Cancel(); HistoryList.ItemsSource = null; ReviewButton.IsEnabled = false;
        StatusText.Text = "Source changed. Inspect history to refresh the list.";
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose this agent's home folder" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) HomeBox.Text = picker.FolderName;
    }
    private IReadOnlyList<AgentHome> CurrentHomes() => ToolPicker.SelectedIndex <= 0 ? AgentHistoryCleanup.DefaultHomes()
        : [new((AgentTool)(ToolPicker.SelectedIndex - 1), HomeBox.Text)];
    private int CurrentAge()
    {
        if (!int.TryParse(AgeBox.Text, out int days) || days is < 1 or > 3650)
            throw new InvalidOperationException("Enter an age from 1 through 3650 days.");
        return days;
    }
    private async void Inspect_Click(object sender, RoutedEventArgs e) => await InspectAsync();
    internal Task InspectAsync() => RunAsync(async token =>
    {
        var sources = CurrentHomes(); int age = CurrentAge();
        HistoryList.ItemsSource = null; StatusText.Text = "Inspecting agent file metadata…";
        var snapshot = await Inspector(sources, age, token); token.ThrowIfCancellationRequested();
        _observedAge = age; HistoryList.ItemsSource = snapshot.Items;
        StatusText.Text = $"{snapshot.Items.Count:N0} {(snapshot.Items.Count == 1 ? "file" : "files")} last changed at least {age:N0} days ago";
        DiagnosticText.Text = string.Join(Environment.NewLine, snapshot.Warnings);
        if (snapshot.Warnings.Count > 0) StatusText.Text += " · Some paths were kept or not inspected; see details";
    });
    private async void Review_Click(object sender, RoutedEventArgs e) => await ReviewCleanupAsync();
    internal Task ReviewCleanupAsync()
    {
        var selected = HistoryList.SelectedItems.Cast<AgentHistoryItem>().ToArray();
        if (selected.Length == 0) return Task.CompletedTask;
        return RunAsync(async token =>
        {
            StatusText.Text = "Rechecking the selected history files…";
            var plan = await Preparer(selected, _observedAge, token); token.ThrowIfCancellationRequested();
            var preview = new AgentHistoryPreview(plan) { Owner = Window.GetWindow(this) };
            if (preview.ShowDialog() != true) { StatusText.Text = "Cleanup cancelled. No files moved."; return; }
            token.ThrowIfCancellationRequested(); _recycling = true; AttachOwner();
            StatusText.Text = "Moving the confirmed files to the Recycle Bin…";
            var result = await Executor(plan, new WindowInteropHelper(Window.GetWindow(this)).Handle, token);
            HistoryList.ItemsSource = null; StatusText.Text = result.Message;
        });
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_work is not null || _lifetime.IsCancellationRequested) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _work = work;
        InspectButton.IsEnabled = ToolPicker.IsEnabled = AgeBox.IsEnabled = HomeRow.IsEnabled = ReviewButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        _operation = CoreAsync();
        try { await _operation; }
        finally
        {
            _operation = null; _work = null; _recycling = false;
            InspectButton.IsEnabled = ToolPicker.IsEnabled = AgeBox.IsEnabled = HomeRow.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed; UpdateSelection();
            if (!IsLoaded) DetachOwner();
        }
        async Task CoreAsync()
        {
            try { await action(work.Token); }
            catch (Exception error) when (AgentHistoryCleanup.Expected(error))
            {
                DiagnosticText.Text = error.Message;
                StatusText.Text = _recycling ? "Recycling stopped or could not be confirmed. Inspect the files again before retrying."
                    : error is OperationCanceledException ? "Cancelled. No cleanup started." : error.Message;
            }
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void UpdateSelection()
    {
        if (DetailsText is null || ReviewButton is null) return;
        var selected = HistoryList.SelectedItems.Cast<AgentHistoryItem>().ToArray();
        ReviewButton.IsEnabled = _work is null && selected.Length is > 0 and <= 500;
        DetailsText.Text = selected.Length == 1 ? selected[0].Path : selected.Length > 0
            ? $"{selected.Length:N0} files selected · {AgentHistoryCleanup.Size(selected.Sum(item => item.Identity.Length))} observed · Review at most 500 at once"
            : "Choose files to review. Age identifies candidates; it does not prove they are unused.";
    }
    internal async Task ShutdownAsync()
    { _work?.Cancel(); if (_operation is { } operation) await operation; DetachOwner(); }
}
