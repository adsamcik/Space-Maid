using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class DeletionQueueWindow : Window
{
    private readonly Action<EntryDeletionJob> _cancel;
    private readonly Func<EntryDeletionJob, Task> _review;
    private readonly Action _clear;

    internal DeletionQueueWindow(ObservableCollection<EntryDeletionJob> jobs, Action<EntryDeletionJob> cancel,
        Func<EntryDeletionJob, Task> review, Action clear)
    {
        InitializeComponent();
        _cancel = cancel;
        _review = review;
        _clear = clear;
        Jobs.ItemsSource = jobs;
    }

    internal void UpdateSummary(int waiting, bool running, int preparing, bool canClear)
    {
        var parts = new List<string>();
        if (running) parts.Add("1 in progress");
        if (waiting > 0) parts.Add($"{waiting:N0} waiting");
        if (preparing > 0) parts.Add($"{preparing:N0} checking");
        Summary.Text = parts.Count > 0 ? string.Join(" · ", parts)
            : Jobs.Items.Count > 0 ? "Recent results" : "No deletions queued.";
        ClearResults.IsEnabled = canClear;
    }

    private async void Job_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EntryDeletionJob job) return;
        if (job.State == EntryDeletionState.RecycleRefused) await _review(job);
        else _cancel(job);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _clear();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal enum EntryDeletionState { Queued, Running, Stopping, Completed, Cancelled, RecycleRefused, Reviewing, Failed }

internal sealed class EntryDeletionJob(string path, bool permanent) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public bool Permanent { get; } = permanent;
    public string Action => Permanent ? "Permanent deletion" : "Recycle Bin";
    public string Status => State switch
    {
        EntryDeletionState.Queued => "Waiting",
        EntryDeletionState.Running => "In progress",
        EntryDeletionState.Stopping => "Stopping",
        EntryDeletionState.Completed => "Complete",
        EntryDeletionState.Cancelled => "Cancelled",
        EntryDeletionState.RecycleRefused => "Review needed",
        EntryDeletionState.Reviewing => "Checking",
        _ => "Couldn’t finish"
    };
    public string Detail { get; private set; } = "";
    public string AccessibleName => $"{Path}, {Action}, {Status}. {Detail}";
    public bool HasCommand => State is EntryDeletionState.Queued or EntryDeletionState.Running or EntryDeletionState.RecycleRefused;
    public string CommandLabel => State switch
    {
        EntryDeletionState.Queued => "Cancel",
        EntryDeletionState.Running => "Stop",
        EntryDeletionState.RecycleRefused => "Review…",
        _ => ""
    };
    public string AccessibleCommand => $"{CommandLabel} {Name}";
    public string? CommandHelp => State == EntryDeletionState.Running ? "Changes already made will stay in place."
        : State == EntryDeletionState.RecycleRefused ? "Review permanent deletion for this item" : null;
    internal bool CanClear => State is EntryDeletionState.Completed or EntryDeletionState.Cancelled
        or EntryDeletionState.RecycleRefused or EntryDeletionState.Failed;
    internal EntryDeletionState State { get; private set; }
    internal DeletionTarget? Target { get; init; }
    internal WeakReference<ScanSnapshot>? Source { get; init; }
    internal FileIdentity? InventoryIdentity { get; init; }
    internal TaskCompletionSource<RecycleResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetState(EntryDeletionState state, string detail = "")
    {
        State = state;
        Detail = detail;
        PropertyChanged?.Invoke(this, new(null));
    }
}
