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
    private bool _showAllNames;

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
        var closeOnStop = lifetime.Register(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsVisible) Close();
        })));
        Closed += (_, _) => closeOnStop.Dispose();
    }

    private async void Load_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        _review = null;
        _rows = [];
        GroupList.ItemsSource = null;
        EntryList.ItemsSource = null;
        StatusText.Text = "Checking the Recycle Bin…";
        var (review, rows) = await Task.Run(() =>
        {
            var capture = _read(token);
            var classified = capture.Entries.OrderByDescending(entry => entry.AllocatedBytes).Select(entry =>
            {
                token.ThrowIfCancellationRequested();
                return new EntryRow(entry, RecycleLeftoverAnalysis.Classify(entry, token));
            }).ToArray();
            return (capture, classified);
        }, token);
        token.ThrowIfCancellationRequested();
        _review = review;
        _rows = rows;
        EntryList.ItemsSource = CollectionViewSource.GetDefaultView(_rows);
        GroupList.ItemsSource = _rows.GroupBy(row => row.Kind)
            .Select(group => new EntryGroup(group.Key, group.Count(), group.Sum(row => row.Bytes),
                group.Count(row => row.Entry.AllocatedBytes is null)))
            .OrderByDescending(group => group.Kind == RecycleLeftoverKind.NuGetPackages)
            .ThenByDescending(group => group.KnownTool).ThenByDescending(group => group.Bytes).ToArray();
        GroupList.SelectedIndex = _rows.Length == 0 ? -1 : 0;
        _message = _rows.Length == 0 ? "No leftover entries found." : $"{_rows.Length:N0} leftover entries on this drive.";
    });

    private async void Recover_Click(object sender, RoutedEventArgs e) => await ApplyAsync(delete: false);
    private async void Delete_Click(object sender, RoutedEventArgs e) => await ApplyAsync(delete: true);

    private async Task ApplyAsync(bool delete)
    {
        if (_work is not null || _review is not { } review) return;
        var selected = _rows.Where(row => row.Picked && row.CanPick).ToArray();
        if (selected.Length == 0 || delete && selected.Any(row => !row.Entry.CanDelete)) return;
        string? destination = null;
        if (delete)
        {
            string groups = string.Join("\n", selected.GroupBy(row => row.Kind).Select(group =>
                $"• {Describe(group.Key).Title} · {group.Count():N0} checked · {SizeText.Format(group.Sum(row => row.Bytes))} observed"));
            if (MessageBox.Show(this,
                $"Permanently delete {selected.Length:N0} checked entries and their folder contents?\n\n{groups}\n\n"
                + "This cannot be undone. Files that cannot be deleted will stay in the Recycle Bin.",
                "Delete selected Recycle Bin leftovers?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        else
        {
            var picker = new OpenFolderDialog { Title = "Choose a recovery folder on " + _drive, InitialDirectory = _destination ?? _drive };
            if (picker.ShowDialog(this) != true) return;
            destination = Path.TrimEndingDirectorySeparator(picker.FolderName);
            _destination = destination;
        }

        var byName = selected.ToDictionary(row => row.Name, StringComparer.Ordinal);
        int processed = 0, completed = 0, partial = 0;
        var progress = new Progress<DiskAccess.RecycleLeftoverResult>(result =>
        {
            processed++;
            if (result.Completed) completed++;
            else if (result.MutationAttempted) partial++;
            Changed |= result.Completed || result.RecoveryPath is not null || result.MutationAttempted;
            byName[result.Name].Finish(result);
            if (processed % 32 == 0) StatusText.Text = $"Processed {processed:N0} of {selected.Length:N0}…";
        });
        await RunAsync(async token =>
        {
            StatusText.Text = delete ? "Checking and deleting approved entries…" : "Recovering checked entries…";
            try
            {
                var approved = selected.Select(row => row.Entry).ToArray();
                await CleanupMutationLease.RunAsync(() => Task.Run(() =>
                {
                    if (delete) review.Delete(approved, progress, token);
                    else review.Apply(approved, destination!, false, progress, token);
                }, token), token);
            }
            finally
            {
                _message = $"{completed:N0} {(delete ? "deleted" : "recovered")} · {processed - completed:N0} need attention"
                    + (token.IsCancellationRequested ? " · Stopped" : "")
                    + (partial > 0 ? $" · {partial:N0} could not be fully deleted; some contents may have been removed." : "");
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
        finally
        {
            _work = null;
            if (_lifetime.IsCancellationRequested && IsVisible) Close();
            else UpdateActions();
        }
    }

    private void UpdateActions()
    {
        if (EntryList is null || CloseButton is null) return;
        bool idle = _work is null;
        EntryList.IsEnabled = GroupList.IsEnabled = FilterBox.IsEnabled = RefreshButton.IsEnabled = idle;
        var selected = _rows.Where(row => row.Picked && row.CanPick).ToArray();
        var checkedByKind = selected.CountBy(row => row.Kind).ToDictionary();
        foreach (var category in GroupList.Items.OfType<EntryGroup>())
            category.SetChecked(checkedByKind.GetValueOrDefault(category.Kind));
        var group = GroupList.SelectedItem as EntryGroup;
        SelectButton.Visibility = group?.KnownTool == true ? Visibility.Visible : Visibility.Collapsed;
        SelectButton.Content = FilterBox.Text.Trim().Length > 0 ? "Check shown"
            : group?.Kind == RecycleLeftoverKind.NuGetPackages ? "Check suggested" : "Check group";
        SelectButton.IsEnabled = idle && group?.KnownTool == true
            && EntryList.Items.OfType<EntryRow>().Any(row => row.CanPick && row.Entry.CanDelete && !row.Picked);
        ClearButton.IsEnabled = idle && selected.Length > 0;
        RecoverButton.IsEnabled = idle && selected.Length > 0;
        DeleteButton.IsEnabled = RecoverButton.IsEnabled && selected.All(row => row.Entry.CanDelete);
        OpenButton.IsEnabled = idle && _destination is not null;
        CloseButton.Content = idle ? "Close" : "Stop";
        string groupScope = checkedByKind.Count > 1 ? $" across {checkedByKind.Count:N0} groups" : "";
        if (idle) StatusText.Text = selected.Length == 0 ? _message
            : $"{selected.Length:N0} checked{groupScope} · "
            + $"{SizeText.Format(selected.Sum(row => row.Bytes))} observed on disk"
            + (selected.Any(row => !row.Entry.CanDelete) ? " · Some entries are recovery only." : "") + " · " + _message;
    }

    private void Pick_Changed(object sender, DataTransferEventArgs e) => UpdateActions();
    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not EntryGroup { KnownTool: true }) return;
        foreach (var row in EntryList.Items.OfType<EntryRow>().Where(row => row.CanPick && row.Entry.CanDelete)) row.Picked = true;
        UpdateActions();
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Picked = false;
        UpdateActions();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GroupHint is null) return;
        var group = GroupList.SelectedItem as EntryGroup;
        GroupHint.Text = group is null ? "" : Describe(group.Kind).Explanation;
        FilterBox.Clear();
        ApplyFilter();
        EntryList.SelectedIndex = EntryList.Items.Count == 0 ? -1 : 0;
    }
    private void ApplyFilter()
    {
        if (EntryList?.ItemsSource is not ICollectionView view) return;
        var group = GroupList.SelectedItem as EntryGroup;
        string query = FilterBox.Text.Trim();
        bool Matches(EntryRow row) => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Label.Contains(query, StringComparison.OrdinalIgnoreCase);
        view.Filter = item => item is EntryRow row && row.Kind == group?.Kind && Matches(row);
        foreach (var row in _rows.Where(row => row.Kind == group?.Kind && row.Picked && !Matches(row))) row.Picked = false;
        UpdateActions();
    }
    private void Entry_Changed(object sender, SelectionChangedEventArgs e)
    {
        _contentsOffset = 0;
        _showAllNames = false;
        ShowDetails();
    }
    private void ContentsToggle_Click(object sender, RoutedEventArgs e)
    {
        _showAllNames = !_showAllNames;
        _contentsOffset = 0;
        ShowDetails();
    }
    private void PreviousPage_Click(object sender, RoutedEventArgs e) { _contentsOffset -= ContentsPageSize; ShowDetails(); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { _contentsOffset += ContentsPageSize; ShowDetails(); }
    private void ShowDetails()
    {
        if (ContentsList is null || DetailsText is null) return;
        var row = EntryList.SelectedItem as EntryRow;
        ContentsList.ItemsSource = _showAllNames ? row?.Entry.ReviewContents(_contentsOffset, ContentsPageSize) : row?.LargestContents;
        int total = row?.Entry.ContentCount ?? 0;
        ContentsToggle.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        ContentsToggle.Content = _showAllNames ? "Show largest folders and files" : "Browse all names";
        ContentsPages.Visibility = _showAllNames && total > ContentsPageSize ? Visibility.Visible : Visibility.Collapsed;
        ContentsRange.Text = $"{_contentsOffset + 1:N0}–{Math.Min(_contentsOffset + ContentsPageSize, total):N0}";
        PreviousPage.IsEnabled = _contentsOffset > 0;
        NextPage.IsEnabled = _contentsOffset + ContentsPageSize < total;
        DetailsText.Text = row is null ? "Select an entry to see what it contains." :
            $"{row.Name}\nModified {row.Modified}" + (total > 0 ? $"\n{total:N0} names · Whole folder" : "")
            + (row.Entry.HasCorruptedRecord ? "\nCorrupted Recycle Bin record" : "")
            + ((row.Detail ?? row.Entry.Blocked) is { } issue ? "\n" + issue : "");
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

    private static (string Title, string Explanation, bool KnownTool) Describe(RecycleLeftoverKind kind) => kind switch
    {
        RecycleLeftoverKind.NuGetPackages => ("NuGet package cache", "Suggested cleanup based on package layout. Restore requires the original package feeds. Recover any local changes you need to keep.", true),
        RecycleLeftoverKind.NuGetData => ("NuGet storage", "Package folders or other NuGet data. The whole folder includes files or layout Space Maid could not verify as cache data.", true),
        RecycleLeftoverKind.Rustup => ("Rustup installations", "Rust toolchains and Rustup data. Recover the folder if you need its settings or custom toolchains.", true),
        RecycleLeftoverKind.IdeData => ("JetBrains-based IDE data", "IDE indexes and caches. These folders can also contain Local History, which may hold versions of your work.", true),
        RecycleLeftoverKind.AndroidImages => ("Android emulator images", "Android SDK system images identified by their package files. Reinstallation depends on the image still being available.", true),
        RecycleLeftoverKind.Documents => ("Documents", "Grouped by extension. Original names and locations may be unavailable.", false),
        RecycleLeftoverKind.Media => ("Photos, video and audio", "Grouped by extension. Recover files you want to inspect or keep.", false),
        RecycleLeftoverKind.Archives => ("Archives", "Grouped by extension. The archive contents are unknown.", false),
        RecycleLeftoverKind.Logs => ("Logs and temporary files", "Grouped by extension. Check the individual files you want to act on.", false),
        _ => ("Unidentified items", "Space Maid could not identify these items from their names or folder structure.", false)
    };

    private sealed class EntryGroup(RecycleLeftoverKind kind, int count, long bytes, int unmeasured) : INotifyPropertyChanged
    {
        private int _checked;
        public event PropertyChangedEventHandler? PropertyChanged;
        public RecycleLeftoverKind Kind { get; } = kind;
        public long Bytes { get; } = bytes;
        public string Title => Describe(Kind).Title;
        public string Summary => (unmeasured == count ? "Size unknown" : $"{SizeText.Format(Bytes)} on disk")
            + $" · {count:N0} {(count == 1 ? "entry" : "entries")}"
            + (unmeasured > 0 && unmeasured < count ? $" · {unmeasured:N0} unmeasured" : "")
            + (_checked > 0 ? $" · {_checked:N0} checked" : "");
        public bool KnownTool => Describe(Kind).KnownTool;
        public void SetChecked(int value)
        {
            if (_checked == value) return;
            _checked = value;
            PropertyChanged?.Invoke(this, new(nameof(Summary)));
        }
    }

    private sealed class EntryRow(DiskAccess.RecycleLeftover entry, RecycleLeftoverKind kind) : INotifyPropertyChanged
    {
        private bool _picked, _finished;
        private string[]? _largest;
        public event PropertyChangedEventHandler? PropertyChanged;
        public DiskAccess.RecycleLeftover Entry { get; } = entry;
        public RecycleLeftoverKind Kind { get; } = kind;
        public string Name => Entry.Name;
        public string Label => Describe(Kind).KnownTool ? Describe(Kind).Title : Name;
        public string SelectionLabel => Label == Name ? $"Select {Name}" : $"Select {Label} ({Name})";
        public string[] LargestContents => _largest ??= RecycleLeftoverAnalysis.LargestContents(Entry, 10)
            .Select(item => $"{item.Name}{(item.IsDirectory ? "\\" : "")} · {SizeText.Format(item.AttributedBytes(true))}").ToArray();
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
            Status = result.Completed ? result.RecoveryPath is null ? "Deleted" : "Recovered" : result.MutationAttempted ? "Incomplete" : "Check details";
            Detail = string.Join("\n", new[] { result.RecoveryPath,
                !result.Completed && result.MutationAttempted ? "Some contents may have been permanently deleted. Remaining files are still in the Recycle Bin." : null,
                result.Issue }.OfType<string>());
            Changed(nameof(CanPick)); Changed(nameof(Status));
        }
        private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    }
}
