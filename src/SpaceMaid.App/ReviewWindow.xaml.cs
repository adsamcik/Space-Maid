using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class ReviewWindow : Window
{
    private const int PageSize = 100;
    private readonly List<CleanupReviewRow> _locations = [];
    private readonly List<CleanupReviewGroup> _groups = [];
    private readonly DispatcherTimer _estimateTimer;
    private FileOperations _operations = new();
    private Func<CleanupPlan, IProgress<CleanupProgress>, CancellationToken, Task<CleanupBatchResult>>? _execute;
    private CleanupPlan? _plan, _selectedPlan;
    private CancellationTokenSource? _cleanup, _estimateCancellation;
    private IReadOnlyList<CleanupReviewRow> _filtered = [];
    private CleanupReadiness _filter = CleanupReadiness.Ready;
    private CleanupReviewGroup? _group;
    private int _page, _selectionVersion;
    private bool _initialized, _updating, _selectionBatch, _closed, _closeWhenFinished, _hasResults;
    internal CleanupBatchResult? Result { get; private set; }
    internal Task? Execution { get; private set; }
    internal Task? SelectionCalculation { get; private set; }

    public ReviewWindow(CleanupPreview preview, string? validationNote = null)
    {
        InitializeComponent();
        _estimateTimer = new(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, EstimateTimer_Tick, Dispatcher);
        _estimateTimer.Stop();
        SetPreview(preview);
        ValidationText.Text = validationNote;
        ValidationDetails.Visibility = string.IsNullOrEmpty(validationNote) ? Visibility.Collapsed : Visibility.Visible;
        _locations.AddRange(preview.Locations.Select(location => new CleanupReviewRow(location, !preview.UsesLogicalSize,
            new(location.RequiresManualReview ? CleanupReadiness.NeedsReview : CleanupReadiness.Ready, null,
                location.RequiresManualReview ? "Review contents" : "Ready"), true, false)));
        BuildGroups(null); _initialized = true; ApplyFilters(true);
        ResultText.Text = "Preview only";
    }

    internal ReviewWindow(CleanupPlan plan, string validationNote,
        Func<CleanupPlan, IProgress<CleanupProgress>, CancellationToken, Task<CleanupBatchResult>> execute,
        IReadOnlyList<OpportunityGroup>? groups = null, FileOperations? operations = null) : this(plan.Preview, validationNote)
    {
        _operations = operations ?? _operations;
        _plan = plan; _execute = execute; _locations.Clear();
        foreach (var location in plan.Preview.Locations)
        {
            var assessment = plan.BuildAssessments.TryGetValue(location.DirectoryIndex, out var checkedAssessment)
                ? checkedAssessment : new BuildCleanupAssessment(CleanupReadiness.NeedsReview, null, "Review contents");
            _locations.Add(new(location, !plan.Preview.UsesLogicalSize, assessment,
                assessment.State == CleanupReadiness.Ready, true, RowSelection_Changed));
        }
        var present = _locations.Select(row => row.Identity).ToHashSet();
        foreach (var blocked in plan.BlockedLocations)
        {
            if (!present.Add((blocked.Opportunity.ProviderId, blocked.Opportunity.RuleId, blocked.Opportunity.Path))) continue;
            DateTime? activity = plan.BuildAssessments.TryGetValue(blocked.Opportunity.DirectoryIndex, out var assessment) ? assessment.LastActivityUtc : null;
            _locations.Add(new(blocked.Opportunity, !plan.Preview.UsesLogicalSize,
                new(CleanupReadiness.Protected, activity, blocked.Reason), false, false));
        }
        BuildGroups(groups); ApplyFilters(true); UpdateCounts();
        ResultText.Text = "Empty the Recycle Bin to free space. Synced folders also affect cloud copies.";
        if (_locations.Count(row => row.Selected) == plan.Preview.Locations.Count)
        { _selectedPlan = plan; CleanupButton.IsEnabled = plan.CanExecute; SetPreview(plan.Preview); }
        else ScheduleEstimate();
    }

    private void BuildGroups(IReadOnlyList<OpportunityGroup>? supplied)
    {
        _groups.Clear();
        _groups.Add(new("all", "All folders", _locations.ToArray(), GroupSelection_Changed));
        var assigned = new HashSet<(string, string, string)>();
        var lookup = _locations.ToDictionary(row => row.Identity);
        foreach (var group in supplied ?? [])
        {
            var members = group.Locations.Select(item => (item.ProviderId, item.RuleId, item.Path))
                .Distinct().Where(key => lookup.ContainsKey(key) && assigned.Add(key)).Select(key => lookup[key]).ToArray();
            if (members.Length > 0) _groups.Add(new(group.Key, group.Name, members, GroupSelection_Changed));
        }
        foreach (var group in _locations.Where(row => !assigned.Contains(row.Identity)).GroupBy(row => (row.Location.ProviderId, row.Location.Category)))
            _groups.Add(new($"{group.Key.ProviderId}/{group.Key.Category}", group.Key.Category, group.ToArray(), GroupSelection_Changed));
        _group = _groups[0];
        // A single category needs no sidebar; the folders gain the full width.
        bool multiple = _groups.Count > 2;
        GroupColumn.Width = new(multiple ? 220 : 0);
        GroupList.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyFilters(bool resetPage)
    {
        if (!_initialized) return;
        string query = SearchBox.Text.Trim();
        bool Matches(CleanupReviewRow row) => row.DisplayState == _filter && (query.Length == 0
            || row.Path.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Location.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
        var matching = _locations.Where(Matches).ToHashSet();
        var sets = _groups.Select(group => (Group: group, Matches: group.Rows.Where(matching.Contains).ToArray())).ToArray();
        long largest = sets.Skip(1).Select(item => item.Matches.Sum(row => row.Bytes)).DefaultIfEmpty().Max();
        foreach (var item in sets) item.Group.SetMatches(item.Matches, largest);
        var visibleGroups = _groups.Where(group => group.Key == "all" || group.Matches.Count > 0).ToArray();
        if (_group is null || !visibleGroups.Contains(_group)) _group = _groups[0];
        _updating = true;
        GroupList.ItemsSource = visibleGroups; GroupList.SelectedItem = _group;
        _updating = false;
        _filtered = _group.Matches.OrderByDescending(row => row.Bytes).ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (resetPage) _page = 0;
        _page = Math.Clamp(_page, 0, Math.Max(0, (_filtered.Count - 1) / PageSize));
        ShowPage(); UpdateSelectionIndicators(); UpdateCounts();
    }

    private void ShowPage()
    {
        var selected = LocationList.SelectedItem as CleanupReviewRow;
        var rows = _filtered.Skip(_page * PageSize).Take(PageSize).ToArray();
        LocationList.ItemsSource = rows;
        if (selected is not null && rows.Contains(selected)) LocationList.SelectedItem = selected;
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        int start = _page * PageSize;
        PageText.Text = rows.Length == 0 ? "0 folders" : $"{start + 1:N0}–{start + rows.Length:N0} of {_filtered.Count:N0}";
        PreviousPage.IsEnabled = _page > 0; NextPage.IsEnabled = start + rows.Length < _filtered.Count;
        PreviousPage.Visibility = NextPage.Visibility = _filtered.Count > PageSize ? Visibility.Visible : Visibility.Collapsed;
        GroupCaption.Text = _group?.Name ?? "Folders";
    }

    private void UpdateCounts()
    {
        ReadyFilter.Content = $"{(_hasResults ? "Moved" : "Ready")} · {_locations.Count(row => row.DisplayState == CleanupReadiness.Ready):N0}";
        ManualFilter.Content = $"{(_hasResults ? "Not completed" : "Needs review")} · {_locations.Count(row => row.DisplayState == CleanupReadiness.NeedsReview):N0}";
        KeptFilter.Content = $"Kept · {_locations.Count(row => row.DisplayState == CleanupReadiness.Protected):N0}";
    }

    private void UpdateSelectionIndicators()
    {
        foreach (var group in _groups) group.NotifySelection();
        var selectable = _filtered.Where(row => row.CanSelect).ToArray();
        int selected = selectable.Count(row => row.Selected);
        VisibleSelection.IsChecked = selected == 0 ? false : selected == selectable.Length ? true : null;
        VisibleSelection.IsEnabled = selectable.Length > 0 && Execution is null;
        VisibleSelection.ToolTip = $"Select {_filtered.Count:N0} matching folders across all pages";
    }

    private void RowSelection_Changed(CleanupReviewRow row)
    { if (_selectionBatch) return; UpdateSelectionIndicators(); ScheduleEstimate(); }
    private void GroupSelection_Changed(CleanupReviewGroup group, bool selected) => SetSelection(group.Matches, selected);
    private void SetSelection(IEnumerable<CleanupReviewRow> rows, bool selected)
    {
        if (_execute is null || Execution is not null) return;
        _selectionBatch = true;
        try { foreach (var row in rows) row.Selected = selected; }
        finally { _selectionBatch = false; }
        UpdateSelectionIndicators(); ScheduleEstimate();
    }

    private void ScheduleEstimate()
    {
        if (_plan is null) return;
        _estimateCancellation?.Cancel(); _selectionVersion++;
        _selectedPlan = null; CleanupButton.IsEnabled = false;
        int count = _locations.Count(row => row.Selected && row.CanSelect);
        EstimateCaption.Text = $"{count:N0} {(count == 1 ? "folder" : "folders")} selected";
        EstimateText.Text = "…";
        _estimateTimer.Stop(); _estimateTimer.Start();
    }

    private async void EstimateTimer_Tick(object? sender, EventArgs e)
    {
        _estimateTimer.Stop();
        if (_plan is null || _closed) return;
        using var cancellation = new CancellationTokenSource();
        _estimateCancellation = cancellation;
        int version = _selectionVersion;
        var choices = _locations.Where(row => row.Selected && row.CanSelect).Select(row => row.Location).ToArray();
        var calculation = _operations.RestrictCleanupAsync(_plan, choices, cancellation.Token);
        SelectionCalculation = calculation;
        try
        {
            var selected = await calculation;
            if (_closed || cancellation.IsCancellationRequested || version != _selectionVersion) return;
            _selectedPlan = selected; SetPreview(selected.Preview); CleanupButton.IsEnabled = selected.CanExecute && Execution is null;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        { if (!_closed && version == _selectionVersion) { EstimateText.Text = "—"; ResultText.Text = "Could not update the estimate."; ResultDetailText.Text = error.Message; } }
        finally { if (ReferenceEquals(_estimateCancellation, cancellation)) _estimateCancellation = null; }
    }

    private void SetPreview(CleanupPreview preview)
    {
        EstimateText.Text = SizeText.Format(preview.EstimatedBytes);
        EstimateCaption.Text = $"{preview.Locations.Count:N0} {(preview.Locations.Count == 1 ? "folder" : "folders")} selected · {(preview.UsesLogicalSize ? "Logical size" : "Estimated space")}";
        EstimateCaption.ToolTip = $"{preview.FileCount:N0} file objects" + (preview.UsesLogicalSize ? " · Physical saving unknown" : "")
            + (preview.ExcludedSharedBytes > 0 ? $" · {SizeText.Format(preview.ExcludedSharedBytes)} shared allocation excluded" : "");
        EstimateText.ToolTip = preview.UsesLogicalSize ? "Logical size; physical saving unknown." : "Selected estimate; shared files may remain in use.";
        CautionText.Text = string.Join("\n\n", preview.Cautions);
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string state } || !Enum.TryParse(state, out CleanupReadiness parsed)) return;
        _filter = parsed; ApplyFilters(true);
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilters(true);
    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    { if (_updating || GroupList.SelectedItem is not CleanupReviewGroup group) return; _group = group; ApplyFilters(true); }
    private void Location_Changed(object sender, SelectionChangedEventArgs e)
    { FolderDetails.DataContext = LocationList.SelectedItem; FolderDetails.Visibility = LocationList.SelectedItem is null ? Visibility.Collapsed : Visibility.Visible; }
    private void VisibleSelection_Click(object sender, RoutedEventArgs e) => SetSelection(_filtered,
        !_filtered.Where(row => row.CanSelect).All(row => row.Selected));
    private void Previous_Click(object sender, RoutedEventArgs e) { _page--; ShowPage(); }
    private void Next_Click(object sender, RoutedEventArgs e) { _page++; ShowPage(); }

    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (_execute is null || _selectedPlan is null || !CleanupButton.IsEnabled || Execution is not null) return;
        var chosen = _selectedPlan;
        using var cleanup = new CancellationTokenSource(); _cleanup = cleanup;
        CleanupButton.IsEnabled = false; SelectionControls.IsEnabled = GroupList.IsEnabled = LocationList.IsEnabled = VisibleSelection.IsEnabled = false;
        foreach (var row in _locations) row.DisableSelection();
        CloseButton.Content = "Cancel cleanup";
        CleanupProgressBar.Visibility = Visibility.Visible; CleanupProgressBar.IsIndeterminate = true;
        var progress = new Progress<CleanupProgress>(update =>
        {
            if (_cleanup is null) return;
            CleanupProgressBar.IsIndeterminate = update.Path.Length == 0;
            CleanupProgressBar.Maximum = Math.Max(1, update.Total); CleanupProgressBar.Value = update.Completed;
            ResultText.Text = $"{update.Completed:N0}/{update.Total:N0} · {update.Message}";
            ProgressPath.Text = update.Path; ProgressPath.ToolTip = update.Path;
            ProgressPath.Visibility = update.Path.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        });
        try
        {
            var execution = _execute(chosen, progress, cleanup.Token); Execution = execution;
            Result = await execution;
            var outcomes = Result.Locations.GroupBy(item => (item.Location.ProviderId, item.Location.RuleId, item.Location.Path)).ToDictionary(group => group.Key, group => group.Last());
            foreach (var row in _locations)
                row.SetResult(outcomes.GetValueOrDefault(row.Identity));
            ShowResults();
            ResultText.Text = Result.Summary;
            ResultDetailText.Text = string.Join("\n", new[] { Result.ValidationError, Result.RefreshNote }.Where(text => !string.IsNullOrEmpty(text)));
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        {
            foreach (var row in _locations) row.SetResult(row.Selected ? new(row.Location, CleanupOutcome.Failed,
                "Recheck before trying again; some contents may already have moved.") : null);
            ShowResults();
            ResultText.Text = "Cleanup could not finish. Recheck before trying again.";
            ResultDetailText.Text = error.Message + " Some contents may already have moved.";
        }
        finally
        {
            _cleanup = null; CleanupProgressBar.Visibility = ProgressPath.Visibility = Visibility.Collapsed;
            SelectionControls.IsEnabled = GroupList.IsEnabled = LocationList.IsEnabled = true;
            UpdateSelectionIndicators(); CloseButton.Content = "Close";
            if (_closeWhenFinished) Close();
        }
    }

    private void ShowResults()
    {
        _hasResults = true;
        if (_filter == CleanupReadiness.Ready && !_locations.Any(row => row.DisplayState == CleanupReadiness.Ready)
            && _locations.Any(row => row.DisplayState == CleanupReadiness.NeedsReview))
        { _filter = CleanupReadiness.NeedsReview; ManualFilter.IsChecked = true; }
        ApplyFilters(false);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    { if (_cleanup is not null) { _cleanup.Cancel(); ResultText.Text = "Cancelling…"; } else Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    { if (_cleanup is null) return; e.Cancel = true; _closeWhenFinished = true; _cleanup.Cancel(); }
    private void Window_Closed(object? sender, EventArgs e)
    { _closed = true; _estimateTimer.Stop(); _estimateCancellation?.Cancel(); }
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key != System.Windows.Input.Key.Escape) return; e.Handled = true; Close_Click(sender, e); }
}
