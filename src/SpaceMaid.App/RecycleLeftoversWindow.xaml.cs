using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.IO;

namespace SpaceMaid.App;

public partial class RecycleLeftoversWindow : Window
{
    private const int ContentsPageSize = 500;
    private readonly FileOperations _operations;
    private readonly string _drive;
    private readonly CancellationToken _lifetime;
    private readonly Func<CancellationToken, DiskAccess.RecycleLeftoverReview> _read;
    private DiskAccess.RecycleLeftoverReview? _review;
    private EntryRow[] _rows = [];
    private CancellationTokenSource? _work;
    private string? _destination;
    private string _message = "";
    private int _contentsOffset;

    internal bool Changed { get; private set; }

    internal RecycleLeftoversWindow(FileOperations operations, string drive, CancellationToken lifetime)
        : this(operations, drive, lifetime, token => operations.Files.ReviewRecycleLeftovers(drive, token)) { }

    internal RecycleLeftoversWindow(FileOperations operations, string drive, CancellationToken lifetime,
        Func<CancellationToken, DiskAccess.RecycleLeftoverReview> read)
    {
        _operations = operations;
        _drive = drive;
        _lifetime = lifetime;
        _read = read;
        InitializeComponent();
        ScopeText.Text = $"Recycle Bin leftovers on {drive} · Your Windows account";
    }

    private async void Load_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        _review = null;
        _rows = [];
        EntryList.ItemsSource = null;
        StatusText.Text = "Checking the Recycle Bin…";
        var review = await Task.Run(() => _read(token), token);
        token.ThrowIfCancellationRequested();
        _review = review;
        _rows = review.Entries.OrderByDescending(entry => entry.AllocatedBytes).Select(entry => new EntryRow(entry)).ToArray();
        EntryList.ItemsSource = CollectionViewSource.GetDefaultView(_rows);
        ApplyFilter();
        _message = _rows.Length == 0 ? "No unmatched entries found." : $"{_rows.Length:N0} unmatched entries. Original names and locations may be unavailable.";
    });

    private async void Recover_Click(object sender, RoutedEventArgs e) => await ApplyAsync(delete: false);
    private async void Delete_Click(object sender, RoutedEventArgs e) => await ApplyAsync(delete: true);

    private async Task ApplyAsync(bool delete)
    {
        if (_work is not null || _review is not { } review) return;
        var selected = _rows.Where(row => row.Picked && row.CanPick).ToArray();
        if (selected.Length == 0 || delete && selected.Any(row => !row.Entry.CanDelete)) return;
        var picker = new OpenFolderDialog { Title = delete ? "Choose where to keep any entries that cannot be deleted" : "Choose a recovery folder on " + _drive,
            InitialDirectory = _destination ?? _drive };
        if (picker.ShowDialog(this) != true) return;
        string destination = Path.TrimEndingDirectorySeparator(picker.FolderName);
        if (delete && MessageBox.Show(this,
            $"Permanently delete {selected.Length:N0} checked entries and their folder contents ({SizeText.Format(selected.Sum(row => row.Bytes))} observed on disk)?\n\n"
            + $"Space Maid first moves them to {destination}. Entries it cannot finish deleting may stay there. Permanent deletion cannot be undone.",
            "Delete selected Recycle Bin leftovers?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        _destination = destination;
        var byName = selected.ToDictionary(row => row.Name, StringComparer.Ordinal);
        int processed = 0, completed = 0;
        var progress = new Progress<DiskAccess.RecycleLeftoverResult>(result =>
        {
            processed++;
            if (result.Completed) completed++;
            Changed |= result.Completed || result.RecoveryPath is not null;
            byName[result.Name].Finish(result);
            if (processed % 32 == 0) StatusText.Text = $"Processed {processed:N0} of {selected.Length:N0}…";
        });
        await RunAsync(async token =>
        {
            StatusText.Text = delete ? "Checking and deleting approved entries…" : "Recovering checked entries…";
            try
            {
                var approved = selected.Select(row => row.Entry).ToArray();
                await CleanupMutationLease.RunAsync(() => Task.Run(() => review.Apply(approved, destination, delete, progress, token), token), token);
            }
            finally
            {
                _message = $"{completed:N0} {(delete ? "deleted" : "recovered")} · {processed - completed:N0} need attention"
                    + (token.IsCancellationRequested ? " · Stopped" : "");
                ShowDetails();
            }
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_work is not null || _lifetime.IsCancellationRequested) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _work = work;
        UpdateActions();
        try { await action(work.Token); }
        catch (OperationCanceledException) { if (_review is null) _message = "Review stopped. Refresh to try again."; }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is OverflowException)
        { _message = error.Message; }
        finally { _work = null; UpdateActions(); }
    }

    private void UpdateActions()
    {
        if (EntryList is null || CloseButton is null) return;
        bool idle = _work is null;
        EntryList.IsEnabled = FilterBox.IsEnabled = RefreshButton.IsEnabled = idle;
        var selected = _rows.Where(row => row.Picked && row.CanPick).ToArray();
        SelectButton.IsEnabled = idle && _rows.Length > 0;
        ClearButton.IsEnabled = idle && selected.Length > 0;
        RecoverButton.IsEnabled = idle && selected.Length > 0;
        DeleteButton.IsEnabled = RecoverButton.IsEnabled && selected.All(row => row.Entry.CanDelete);
        OpenButton.IsEnabled = idle && _destination is not null;
        CloseButton.Content = idle ? "Close" : "Stop";
        if (idle) StatusText.Text = selected.Length == 0 ? _message : $"{selected.Length:N0} checked"
            + (selected.Any(row => !row.Entry.CanDelete) ? " · Some entries are recovery only." : "") + " · " + _message;
    }

    private void Pick_Changed(object sender, DataTransferEventArgs e) => UpdateActions();
    private void Select_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in EntryList.Items.OfType<EntryRow>().Where(row => row.CanPick && row.Entry.CanDelete)) row.Picked = true;
        UpdateActions();
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Picked = false;
        UpdateActions();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void ApplyFilter()
    {
        if (EntryList?.ItemsSource is not ICollectionView view) return;
        string query = FilterBox.Text.Trim();
        view.Filter = item => item is EntryRow row && row.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        foreach (var row in _rows.Where(row => row.Picked && !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase))) row.Picked = false;
        UpdateActions();
    }
    private void Entry_Changed(object sender, SelectionChangedEventArgs e)
    {
        _contentsOffset = 0;
        ShowDetails();
    }
    private void PreviousPage_Click(object sender, RoutedEventArgs e) { _contentsOffset -= ContentsPageSize; ShowDetails(); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { _contentsOffset += ContentsPageSize; ShowDetails(); }
    private void ShowDetails()
    {
        if (ContentsList is null || DetailsText is null) return;
        var row = EntryList.SelectedItem as EntryRow;
        ContentsList.ItemsSource = row?.Entry.ReviewContents(_contentsOffset, ContentsPageSize);
        int total = row?.Entry.ContentCount ?? 0;
        ContentsPages.Visibility = total > ContentsPageSize ? Visibility.Visible : Visibility.Collapsed;
        ContentsRange.Text = $"{_contentsOffset + 1:N0}–{Math.Min(_contentsOffset + ContentsPageSize, total):N0}";
        PreviousPage.IsEnabled = _contentsOffset > 0;
        NextPage.IsEnabled = _contentsOffset + ContentsPageSize < total;
        DetailsText.Text = row is null ? "Select an entry to see details." :
            row.Detail ?? row.Entry.Blocked ?? (row.Entry.ContentCount is { } count
                ? $"{count:N0} {(count == 1 ? "entry" : "entries")} · Checking a folder includes all its contents." : "Original name and location unavailable.");
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_destination is null) return;
        try { _operations.OpenInExplorer(_destination); }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = error.Message; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) { if (_work is { } work) work.Cancel(); else Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    { if (_work is { } work) { e.Cancel = true; work.Cancel(); } }

    private sealed class EntryRow(DiskAccess.RecycleLeftover entry) : INotifyPropertyChanged
    {
        private bool _picked, _finished;
        public event PropertyChangedEventHandler? PropertyChanged;
        public DiskAccess.RecycleLeftover Entry { get; } = entry;
        public string Name => Entry.Name;
        public long Bytes => Entry.AllocatedBytes ?? 0;
        public string Size => Entry.AllocatedBytes is { } size ? SizeText.Format(size) : "—";
        public long Written => Entry.Written;
        public string Modified => ExplorerRow.FormatDate(Written);
        public bool CanPick => Entry.CanRecover && !_finished;
        public string Status { get; private set; } = entry.CanDelete ? "Ready" : entry.CanRecover ? "Recovery only" : "Kept";
        public string? Detail { get; private set; }
        public bool Picked
        {
            get => _picked;
            set { if (_picked != value) { _picked = value; Changed(nameof(Picked)); } }
        }
        public void Finish(DiskAccess.RecycleLeftoverResult result)
        {
            _finished = true;
            Picked = false;
            Status = result.Completed ? result.RecoveryPath is null ? "Deleted" : "Recovered" : "Check details";
            Detail = string.Join("\n", new[] { result.RecoveryPath, result.Issue }.OfType<string>());
            Changed(nameof(CanPick)); Changed(nameof(Status));
        }
        private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    }
}
