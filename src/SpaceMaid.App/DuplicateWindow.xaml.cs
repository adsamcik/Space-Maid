using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Core.Deduplication;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class DuplicateWindow : Window
{
    private readonly ScanSnapshot _scan;
    private readonly Action<string[]> _changed;
    private readonly Action<string> _reveal;
    private readonly CancellationToken _lifetime;
    private readonly FileOperations _operations;
    private readonly Action<string, object> _diagnostic;
    private CancellationTokenSource? _work;
    private Task? _operation;
    private bool _closeAfterWork;
    private DuplicateSearchSession? _session;
    private DuplicateSearchPolicy? _previousPolicy;
    private readonly List<GroupRow> _groups = [];
    private StorageCapabilities? _capabilities;

    internal DuplicateWindow(ScanSnapshot scan, IReadOnlyList<string> excludedPaths, Action<string[]> changed,
        Action<string> reveal, CancellationToken lifetime, FileOperations operations, Action<string, object> diagnostic)
    {
        InitializeComponent(); _scan = scan; _changed = changed; _reveal = reveal; _lifetime = lifetime;
        _operations = operations; _diagnostic = diagnostic;
        ScopeText.Text = scan.RootPath; ExclusionsBox.Text = string.Join(Environment.NewLine, excludedPaths);
    }

    internal Task StopAsync() { _work?.Cancel(); return _operation ?? Task.CompletedTask; }
    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { InitialDirectory = _scan.RootPath, CheckFileExists = true,
            Filter = "Build resources (*.pdb;*.obj;*.lib;*.rlib;*.dll)|*.pdb;*.obj;*.lib;*.rlib;*.dll" };
        if (dialog.ShowDialog(this) == true) await InspectFileAsync(dialog.FileName);
    }
    internal async Task InspectFileAsync(string path)
    {
        if (_work is not null) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _work = work; SetWorking(true);
        try
        {
            path = OptimizationPolicy.NormalizePath(path);
            if (!OptimizationPolicy.Covers(OptimizationPolicy.NormalizePath(_scan.RootPath), path))
                throw new ArgumentException("Choose a file inside this scanned location.");
            var operation = _operations.InspectBuildStorageAsync(path, work.Token); _operation = operation;
            var file = await operation;
            var row = new GroupRow(new(file.Hash, file.Revision.Length, [file])); _groups.Add(row);
            GroupList.ItemsSource = _groups.ToArray(); GroupList.SelectedItem = row;
            SummaryText.Text = "Inspected build resource · Original file retained";
            StatusText.Text = "Select a storage action. The file's complete contents and identity are verified again.";
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = error.Message; }
        finally { _work = null; _operation = null; SetWorking(false); if (_closeAfterWork) Close(); }
    }
    private async void Find_Click(object sender, RoutedEventArgs e) => await StartSearchAsync(false);
    private async void Continue_Click(object sender, RoutedEventArgs e) => await StartSearchAsync(true);
    private async Task StartSearchAsync(bool next)
    {
        if (_work is not null) return;
        try
        {
            if (!long.TryParse(MinimumBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long kib) || kib is < 1 or > 1024L * 1024 * 1024
                || !long.TryParse(ReadLimitBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long mib) || mib is < 1 or > 65536)
                throw new ArgumentException("Enter a positive minimum size and a read limit of 1–65536 MiB.");
            var policy = new DuplicateSearchPolicy { MinimumBytes = kib * 1024, MaximumReadBytes = mib * 1024 * 1024,
                ExcludedPaths = ExclusionsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
            if (next && (_previousPolicy is null || policy.MinimumBytes != _previousPolicy.MinimumBytes
                || policy.MaximumReadBytes != _previousPolicy.MaximumReadBytes || !policy.ExcludedPaths.SequenceEqual(_previousPolicy.ExcludedPaths)))
                throw new ArgumentException("Search options changed. Start a new comparison.");
            if (!next)
            {
                _session?.Dispose(); _session = new(_scan, policy); _groups.Clear(); GroupList.ItemsSource = null; FileList.ItemsSource = null;
            }
            using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _work = work; SetWorking(true);
            try
            {
                var progress = new Progress<DuplicateSearchProgress>(p => { if (_work is not null) StatusText.Text = $"{p.Phase} · {p.ExaminedFiles:N0} files · {SizeText.Format(p.ReadBytes)} read"; });
                var selectedGroup = (GroupList.SelectedItem as GroupRow)?.Group;
                string? selectedPath = (FileList.SelectedItem as FileRow)?.Path;
                var operation = _operations.ContinueDuplicatesAsync(_session ?? throw new InvalidOperationException("Start a new comparison."), progress, work.Token); _operation = operation;
                var result = await operation; _previousPolicy = policy;
                _groups.Clear(); _groups.AddRange(result.Groups.Select(g => new GroupRow(g)));
                GroupList.ItemsSource = _groups.OrderByDescending(g => g.Group.RedundantLogicalBytes).ToArray();
                GroupList.SelectedItem = selectedGroup is null ? null : _groups.FirstOrDefault(g => g.Group.Hash == selectedGroup.Hash && g.Group.FileBytes == selectedGroup.FileBytes);
                if (GroupList.SelectedIndex < 0 && GroupList.Items.Count > 0) GroupList.SelectedIndex = 0;
                if (selectedPath is not null && FileList.Items.Cast<FileRow>().FirstOrDefault(f => f.Path == selectedPath) is { } selected)
                { FileList.SelectedItem = selected; FileList.ScrollIntoView(selected); }
                SummaryText.Text = $"{_groups.Count:N0} {(_groups.Count == 1 ? "group" : "groups")} · {SizeText.Format(_groups.Sum(g => g.Group.RedundantLogicalBytes))} redundant content · {result.ExistingAliases:N0} existing aliases";
                StatusText.Text = result.Note + (result.Issues.Count > 0 ? $" · {result.Issues.Count:N0} unavailable files" : "");
                StatusText.ToolTip = result.Issues.Count > 0 ? string.Join("\n", result.Issues.Take(12).Select(i => i.Path + ": " + i.Reason)) : null;
            }
            finally { _work = null; _operation = null; SetWorking(false); if (_closeAfterWork) Close(); }
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = error.Message; }
    }

    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupRow group) return;
        string? selected = (FileList.SelectedItem as FileRow)?.Path;
        FileList.ItemsSource = group.Group.Files.Select(f => new FileRow(f, Path.GetRelativePath(_scan.RootPath, f.Path))).ToArray();
        FileList.SelectedItem = FileList.Items.Cast<FileRow>().FirstOrDefault(f => f.Path == selected) ?? FileList.Items.Cast<FileRow>().FirstOrDefault();
    }
    private async void File_Changed(object sender, SelectionChangedEventArgs e)
    {
        _capabilities = null; UpdateActions();
        if (FileList.SelectedItem is not FileRow row) return;
        try
        {
            var capability = await Task.Run(() => StoragePreservation.Capabilities(row.Path));
            if (!ReferenceEquals(FileList.SelectedItem, row)) return;
            _capabilities = capability;
            DetailText.Text = row.File.Revision.Links > 1 ? "Already shared by multiple file names. Storage changes are unavailable."
                : row.File.HasAdditionalData ? "Only the default stream matches. Additional data is retained and storage actions are unavailable."
                : capability.Note + " File names, permissions and related files stay intact.";
            UpdateActions();
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { if (ReferenceEquals(FileList.SelectedItem, row)) DetailText.Text = error.Message; }
    }
    private async void Compress_Click(object sender, RoutedEventArgs e) => await PreserveAsync(StoragePreservationAction.Compress);
    private async void Decompress_Click(object sender, RoutedEventArgs e) => await PreserveAsync(StoragePreservationAction.Decompress);
    private async void Share_Click(object sender, RoutedEventArgs e) => await PreserveAsync(StoragePreservationAction.ShareBlocks);
    private async Task PreserveAsync(StoragePreservationAction action)
    {
        if (_work is not null || FileList.SelectedItem is not FileRow row || GroupList.SelectedItem is not GroupRow group) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _work = work; SetWorking(true);
        try
        {
            StatusText.Text = "Checking exact identities and complete contents…";
            var keeper = group.Group.Files.FirstOrDefault(f => f.Revision.ObjectKey != row.File.Revision.ObjectKey
                && f.Revision.Volume == row.File.Revision.Volume && !f.SharesFileObject && !f.HasAdditionalData);
            var operation = _operations.PreserveStorageAsync(row.File, action, keeper, work.Token, _diagnostic);
            _operation = operation; var result = await operation;
            _changed([row.Path]);
            StatusText.Text = result.Message + (result.Action != StoragePreservationAction.ShareBlocks
                ? " Observed allocation: " + SizeText.Format(result.AllocatedAfter) + " (was " + SizeText.Format(result.AllocatedBefore) + ")." : "")
                + " Compare or inspect again to refresh this file.";
            // The native change invalidates the old revision. Explicitly refresh
            // this result rather than allowing another action on stale authority.
            FileList.SelectedItem = null; FileList.ItemsSource = null; _capabilities = null;
            _groups.Remove(group); GroupList.ItemsSource = _groups.ToArray();
            _session?.Dispose(); _session = null; _previousPolicy = null;
            SummaryText.Text = $"{_groups.Count:N0} remaining {(_groups.Count == 1 ? "group" : "groups")} · {SizeText.Format(_groups.Sum(g => g.Group.RedundantLogicalBytes))} redundant content";
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        {
            _session?.Dispose(); _session = null; _previousPolicy = null;
            _changed([row.Path]); StatusText.Text = error is OperationCanceledException ? "Cancelled. Completed storage changes remain valid; compare again to refresh." : error.Message;
        }
        finally { _work = null; _operation = null; SetWorking(false); if (_closeAfterWork) Close(); }
    }
    private void UpdateActions()
    {
        bool idle = _work is null; var row = FileList.SelectedItem as FileRow;
        bool ordinary = row is not null && !row.File.HasAdditionalData && !row.File.SharesFileObject;
        RevealButton.IsEnabled = idle && row is not null;
        CompressButton.IsEnabled = idle && ordinary && _capabilities?.Compression == true && StoragePreservation.IsColdBuildCandidate(row!.File)
            && (row.File.Revision.Attributes & FileAttributes.Compressed) == 0;
        DecompressButton.IsEnabled = idle && ordinary && _capabilities?.Compression == true && (row!.File.Revision.Attributes & FileAttributes.Compressed) != 0;
        ShareButton.IsEnabled = idle && ordinary && _capabilities?.BlockSharing == true && (GroupList.SelectedItem as GroupRow)?.Group.Files.Any(f =>
            f.Revision.Volume == row!.File.Revision.Volume && f.Revision.Identity != row.File.Revision.Identity && !f.HasAdditionalData && !f.SharesFileObject) == true;
    }
    private void SetWorking(bool busy)
    {
        FindButton.IsEnabled = InspectButton.IsEnabled = OptionsPanel.IsEnabled = GroupList.IsEnabled = FileList.IsEnabled = !busy;
        ContinueButton.IsEnabled = !busy && _session?.CanContinue == true;
        WorkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Content = busy ? "Cancel" : "Close"; UpdateActions();
    }
    private void Reveal_Click(object sender, RoutedEventArgs e) { if (FileList.SelectedItem is FileRow row) _reveal(row.Path); }
    private void Close_Click(object sender, RoutedEventArgs e) { if (_work is not null) _work.Cancel(); else Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    { if (_work is not null) { e.Cancel = true; _closeAfterWork = true; _work.Cancel(); } else _session?.Dispose(); }
    private sealed record GroupRow(DuplicateGroup Group)
    { public int Count => Group.Files.Count; public string Size => SizeText.Format(Group.FileBytes); public string Redundancy => SizeText.Format(Group.RedundantLogicalBytes); }
    private sealed record FileRow(DuplicateFile File, string RelativePath)
    { public string Path => File.Path; public string DataStatus => File.HasAdditionalData ? "Additional streams" : File.SharesFileObject ? "Already shared" : "Default stream verified"; }
}
