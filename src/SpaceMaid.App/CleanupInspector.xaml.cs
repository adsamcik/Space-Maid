using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

internal sealed partial class CleanupInspector : UserControl
{
    private const int PageSize = 100;
    private CleanupPlan? _plan;
    private CleanupDefinition? _definition;
    private CleanupRunRecord? _record;
    private IReadOnlyList<CleanupLocationResult>? _results;
    private IReadOnlyList<CleanupInspectionEntry> _entries = [], _filtered = [];
    private readonly HashSet<string> _omitted = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _projects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private Guid _context;
    private string? _drill, _selected;
    private HashSet<string>? _undo;
    private int _page, _presentationVersion;
    private bool _updating, _busy, _canExclude = true;
    private bool ReadOnly => _record is not null || _results is not null;
    internal event Action<Opportunity, bool>? InclusionChanged;
    internal event Action<Opportunity>? ExcludeRequested;
    internal event Action<Opportunity>? RevealRequested;
    internal IReadOnlyList<Opportunity> SelectedLocations => _entries.Where(e => e.CanChoose && e.Included).Select(e => e.Location).ToArray();
    internal IReadOnlyList<CleanupInspectionEntry> Entries => _entries;

    internal CleanupInspector()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "Cleanup folder inspection");
    }

    internal void SetContext(Guid context)
    {
        if (_context == context) return;
        _presentationVersion++; _context = context; _omitted.Clear(); _selected = _drill = null; _undo = null; _page = 0;
        _updating = true;
        try { SearchBox.Text = ""; OutcomePicker.SelectedIndex = GroupPicker.SelectedIndex = 0; SummaryTab.IsChecked = true; }
        finally { _updating = false; }
    }

    internal void SetProjects(IReadOnlyDictionary<string, string> projects) => _projects = projects;

    internal CleanupInspectorPresentation CapturePresentation()
    {
        string? focus = (Keyboard.FocusedElement as FrameworkElement)?.Name;
        if (!IsKeyboardFocusWithin) focus = null;
        else if (string.IsNullOrEmpty(focus))
            focus = FolderList.IsKeyboardFocusWithin ? nameof(FolderList) : SummaryList.IsKeyboardFocusWithin ? nameof(SummaryList) : null;
        return new(_context, ListTab.IsChecked == true, GroupPicker.SelectedIndex, OutcomePicker.SelectedIndex,
            SearchBox.Text, _page, _drill, _selected, _omitted.ToArray(), _undo?.ToArray(), UndoText.Text,
            FindScrollViewer(SummaryList)?.VerticalOffset ?? 0, FindScrollViewer(FolderList)?.VerticalOffset ?? 0,
            (SummaryList.SelectedItem as CleanupInspectionGroup)?.Name, focus, SearchBox.SelectionStart, SearchBox.SelectionLength);
    }

    internal void RestorePresentation(CleanupInspectorPresentation presentation)
    {
        if (_context != presentation.ContextId) return;
        int version = ++_presentationVersion;
        _updating = true;
        try
        {
            _omitted.Clear(); _omitted.UnionWith(presentation.Omitted);
            _undo = presentation.Undo is null ? null : new(presentation.Undo, StringComparer.Ordinal);
            _page = Math.Max(0, presentation.Page); _drill = presentation.Drill; _selected = presentation.Selected;
            GroupPicker.SelectedIndex = Math.Clamp(presentation.Group, 0, GroupPicker.Items.Count - 1);
            OutcomePicker.SelectedIndex = Math.Clamp(presentation.Outcome, 0, OutcomePicker.Items.Count - 1);
            SearchBox.Text = presentation.Search; UndoText.Text = presentation.UndoText;
            ListTab.IsChecked = presentation.List; SummaryTab.IsChecked = !presentation.List;
            if (_plan is not null) _entries = CleanupInspectionData.Build(_plan, _omitted, _results, _projects);
        }
        finally { _updating = false; }
        ApplyFilters(false);
        SummaryList.SelectedItem = SummaryList.Items.Cast<CleanupInspectionGroup>().FirstOrDefault(group => group.Name == presentation.SummarySelection);
        SearchBox.Select(Math.Clamp(presentation.SearchSelectionStart, 0, SearchBox.Text.Length),
            Math.Clamp(presentation.SearchSelectionLength, 0, SearchBox.Text.Length - Math.Clamp(presentation.SearchSelectionStart, 0, SearchBox.Text.Length)));
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_context != presentation.ContextId || version != _presentationVersion) return;
            FindScrollViewer(SummaryList)?.ScrollToVerticalOffset(presentation.SummaryOffset);
            FindScrollViewer(FolderList)?.ScrollToVerticalOffset(presentation.ListOffset);
            if (presentation.FocusName is { Length: > 0 } name && FindName(name) is FrameworkElement { IsVisible: true, IsEnabled: true } focus)
            {
                if (focus is ListBox { SelectedItem: not null } list && list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item) item.Focus();
                else focus.Focus();
            }
        }));
    }

    internal void Show(CleanupPlan plan, CleanupDefinition definition, IReadOnlyList<CleanupLocationResult>? results = null)
    {
        _plan = plan; _definition = definition; _results = results; _record = null;
        _entries = CleanupInspectionData.Build(plan, _omitted, results, _projects);
        ContextText.Text = $"{_entries.Count:N0} folders · {(plan.Preview.UsesLogicalSize ? "file contents" : "observed disk allocation")}";
        ApplyFilters(false);
    }

    internal void ShowRecord(CleanupRunRecord record)
    {
        SetContext(record.Id);
        _plan = null; _definition = record.Definition; _results = null; _record = record;
        _entries = CleanupInspectionData.BuildRecord(record);
        string provenance = record.UsesLogicalSize switch { true => "file contents", false => "observed disk allocation", _ => "captured estimate" };
        string date = record.ScannedAtUtc == default ? "" : " · Scan from " + record.ScannedAtUtc.ToLocalTime().ToString("g");
        ContextText.Text = $"{_entries.Count:N0} folders · {provenance}{date}";
        ApplyFilters(false);
    }

    internal void SetIncluded(IReadOnlyList<Opportunity> included)
    {
        var paths = included.Select(item => Path.TrimEndingDirectorySeparator(item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries.Where(e => e.CanChoose))
        {
            bool covered = false;
            for (string? path = Path.TrimEndingDirectorySeparator(entry.Location.Path); path is not null; path = Path.GetDirectoryName(path))
                if (paths.Contains(path)) { covered = true; break; }
            if (covered) _omitted.Remove(entry.Key); else _omitted.Add(entry.Key);
        }
        var byPath = _entries.ToDictionary(entry => Path.TrimEndingDirectorySeparator(entry.Location.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var omitted in _entries.Where(entry => _omitted.Contains(entry.Key)).ToArray())
            for (string? path = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(omitted.Location.Path)); path is not null; path = Path.GetDirectoryName(path))
                if (byPath.TryGetValue(path, out var ancestor)) _omitted.Add(ancestor.Key);
        Rebuild();
    }

    internal void SetBusy(bool busy) { _busy = busy; UpdateDetails(); }
    internal void SetCanExclude(bool canExclude) { _canExclude = canExclude; UpdateDetails(); }
    internal void ShowKept() { OutcomePicker.SelectedIndex = 2; SummaryTab.IsChecked = true; ApplyFilters(true); }

    private void Rebuild()
    {
        if (_plan is not null && _definition is not null) Show(_plan, _definition, _results);
    }

    private void ApplyFilters(bool resetPage)
    {
        if (_updating || SearchBox is null || SummaryList is null || FolderList is null) return;
        if (resetPage) _page = 0;
        string search = SearchBox.Text.Trim(); int selectedOutcome = OutcomePicker.SelectedIndex;
        var candidates = _entries.Where(e => (selectedOutcome == 0 || (int)e.Decision == selectedOutcome - 1)
            && (search.Length == 0 || e.Location.Path.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Location.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Reason.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        int grouping = GroupPicker.SelectedIndex;
        SummaryList.ItemsSource = candidates.GroupBy(e => GroupName(e, grouping)).Select(g => new CleanupInspectionGroup(g.Key,
            g.Count(), g.Sum(e => e.AccountedBytes))).OrderByDescending(g => g.Bytes).ThenBy(g => g.Name).ToArray();
        _filtered = candidates.Where(e => _drill is null || GroupName(e, grouping) == _drill)
            .OrderByDescending(e => e.SourceBytes).ThenBy(e => e.Location.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        _page = Math.Min(_page, Math.Max(0, (_filtered.Count - 1) / PageSize));
        bool list = ListTab.IsChecked == true;
        SummaryList.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        FolderList.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        Pagination.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        DrillPanel.Visibility = _drill is not null && list ? Visibility.Visible : Visibility.Collapsed; DrillText.Text = _drill ?? "";
        EmptyText.Visibility = (list ? _filtered.Count : candidates.Length) == 0 ? Visibility.Visible : Visibility.Collapsed;
        _updating = true;
        try
        {
            FolderList.ItemsSource = _filtered.Skip(_page * PageSize).Take(PageSize).ToArray();
            FolderList.SelectedItem = FolderList.Items.Cast<CleanupInspectionEntry>().FirstOrDefault(e => e.Key == _selected);
        }
        finally { _updating = false; }
        int start = _filtered.Count == 0 ? 0 : _page * PageSize + 1, end = Math.Min((_page + 1) * PageSize, _filtered.Count);
        PageText.Text = $"{start:N0}–{end:N0} of {_filtered.Count:N0}"; PreviousButton.IsEnabled = _page > 0; NextButton.IsEnabled = end < _filtered.Count;
        UndoPanel.Visibility = _undo is null || ReadOnly ? Visibility.Collapsed : Visibility.Visible;
        UpdateDetails();
    }

    private static string GroupName(CleanupInspectionEntry entry, int grouping) => grouping switch
    { 1 => entry.Project, 2 => entry.Location.Title, 3 => entry.Inactivity, _ => entry.DecisionText };

    private void UpdateDetails()
    {
        if (FolderDetail is null) return;
        var entry = FolderList.SelectedItem as CleanupInspectionEntry;
        FolderDetail.Visibility = entry is null || ListTab.IsChecked != true ? Visibility.Collapsed : Visibility.Visible;
        if (entry is null) return;
        ExactPath.Text = entry.Location.Path; DecisionText.Text = entry.DecisionText; ReasonText.Text = entry.Reason;
        AssociationText.Text = entry.Project + " · " + entry.Location.Title;
        CautionText.Text = string.Join(" ", new[] { entry.Location.Reason, entry.Location.Caution, entry.Location.SuggestedAction }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal));
        CautionText.Visibility = CautionText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ActivityText.Text = entry.Assessment.LastActivityUtc is { } activity
            ? "Latest file or project change: " + activity.ToLocalTime().ToString("g") + ". Actual last use is not tracked."
            : "No verified activity time.";
        bool skippedContents = _entries.Any(item => item.Key != entry.Key && _omitted.Contains(item.Key) && Covers(entry.Location.Path, item.Location.Path));
        IncludeButton.Content = entry.Included ? "Skip this run" : skippedContents ? "Include folder and skipped contents" : "Include this run";
        IncludeButton.Visibility = !ReadOnly && entry.CanChoose ? Visibility.Visible : Visibility.Collapsed;
        IncludeButton.IsEnabled = !_busy; ExcludeButton.IsEnabled = !_busy && !ReadOnly && _canExclude;
        ExcludeButton.Visibility = ReadOnly || !_canExclude ? Visibility.Collapsed : Visibility.Visible;
        RevealButton.Visibility = RevealRequested is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChangeInclusion(CleanupInspectionEntry entry, bool included)
    {
        if (_busy || !entry.CanChoose || ReadOnly) return;
        _undo = new(_omitted, StringComparer.Ordinal);
        var changed = CleanupInspectionData.ChangeSelection(_entries, _omitted, entry.Location, included);
        _omitted.Clear(); _omitted.UnionWith(changed);
        UndoText.Text = included ? "Folder included in this run." : "Folder skipped for this run.";
        Rebuild(); InclusionChanged?.Invoke(entry.Location, included); LiveText.Text = UndoText.Text;
    }

    private void Tab_Changed(object sender, RoutedEventArgs e) { if (!_updating) ApplyFilters(false); }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    { if (!_updating) { if (ReferenceEquals(sender, GroupPicker)) _drill = null; ApplyFilters(true); } }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (!_updating) ApplyFilters(true); }
    private void ViewGroup_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: CleanupInspectionGroup group }) { _drill = group.Name; _page = 0; ListTab.IsChecked = true; ApplyFilters(false); FolderList.Focus(); } }
    private void ClearGroup_Click(object sender, RoutedEventArgs e) { _drill = null; ApplyFilters(true); }
    private void Folder_Changed(object sender, SelectionChangedEventArgs e)
    { if (!_updating) { _selected = (FolderList.SelectedItem as CleanupInspectionEntry)?.Key; UpdateDetails(); } }
    private void Previous_Click(object sender, RoutedEventArgs e) { if (_page > 0) { _page--; ApplyFilters(false); } }
    private void Next_Click(object sender, RoutedEventArgs e) { if ((_page + 1) * PageSize < _filtered.Count) { _page++; ApplyFilters(false); } }
    private void Include_Click(object sender, RoutedEventArgs e)
    { if (_entries.FirstOrDefault(entry => entry.Key == _selected) is { } entry) ChangeInclusion(entry, !entry.Included); }
    private void Exclude_Click(object sender, RoutedEventArgs e)
    { if (!_busy && !ReadOnly && _canExclude && _entries.FirstOrDefault(entry => entry.Key == _selected) is { } entry) ExcludeRequested?.Invoke(entry.Location); }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    { if (_entries.FirstOrDefault(entry => entry.Key == _selected) is { } entry) RevealRequested?.Invoke(entry.Location); }
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ReadOnly || _undo is not { } undo || _entries.FirstOrDefault(entry => entry.Key == _selected) is not { } entry) return;
        _undo = null; _omitted.Clear(); _omitted.UnionWith(undo);
        Rebuild(); InclusionChanged?.Invoke(entry.Location, !_omitted.Contains(entry.Key)); LiveText.Text = "Run selection restored.";
    }
    private static bool Covers(string root, string path) => root.Equals(path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
}

internal sealed record CleanupInspectorPresentation(Guid ContextId, bool List, int Group, int Outcome, string Search,
    int Page, string? Drill, string? Selected, string[] Omitted, string[]? Undo, string UndoText, double SummaryOffset,
    double ListOffset, string? SummarySelection, string? FocusName, int SearchSelectionStart, int SearchSelectionLength);

internal enum CleanupInspectionDecision { Ready, Kept, Removed, Skipped, NotStarted, NeedsChecking }
internal sealed record CleanupInspectionEntry(Opportunity Location, BuildCleanupAssessment Assessment,
    CleanupInspectionDecision Decision, string Reason, bool Included, bool CanChoose, long SourceBytes, long AccountedBytes, string Project, DateTime ObservedUtc)
{
    internal string Key => CleanupInspectionData.Key(Location);
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Location.Path));
    public string DecisionText => CleanupInspectionData.Label(Decision);
    public string Size => SizeText.Format(SourceBytes);
    public string AutomationName => Location.Path + ". " + DecisionText + ". " + Size;
    public string Inactivity => Assessment.LastActivityUtc is not { } activity ? "Unknown / not applicable"
        : (ObservedUtc - activity.ToUniversalTime()).TotalDays switch { < 7 => "Under 7 days", < 30 => "7–29 days", < 90 => "30–89 days", _ => "90 days or more" };
}
internal sealed record CleanupInspectionGroup(string Name, int Count, long Bytes)
{
    public string Summary => $"{Count:N0} {(Count == 1 ? "folder" : "folders")} · {SizeText.Format(Bytes)}";
    public string ActionName => "View folders: " + Name;
    public string AutomationName => Name + ". " + Summary;
}

internal static class CleanupInspectionData
{
    internal static string Key(Opportunity location) => location.ProviderId + "\0" + location.RuleId + "\0" + location.Path.ToUpperInvariant();
    internal static string Label(CleanupInspectionDecision decision) => decision switch
    { CleanupInspectionDecision.Ready => "Ready", CleanupInspectionDecision.Kept => "Kept", CleanupInspectionDecision.Removed => "Removed", CleanupInspectionDecision.Skipped => "Skipped", CleanupInspectionDecision.NotStarted => "Not started", _ => "Needs checking" };

    internal static HashSet<string> ChangeSelection(IReadOnlyList<CleanupInspectionEntry> entries, IReadOnlySet<string> omitted, Opportunity location, bool included)
    {
        var result = new HashSet<string>(omitted, StringComparer.Ordinal);
        foreach (var item in entries.Where(item => item.CanChoose))
            if (included && Covers(location.Path, item.Location.Path)) result.Remove(item.Key);
            else if (!included && (Covers(location.Path, item.Location.Path) || Covers(item.Location.Path, location.Path))) result.Add(item.Key);
        return result;
    }

    internal static IReadOnlyList<CleanupInspectionEntry> Build(CleanupPlan plan, IReadOnlySet<string>? omitted = null,
        IReadOnlyList<CleanupLocationResult>? results = null, IReadOnlyDictionary<string, string>? projects = null)
    {
        var ready = plan.Preview.Locations.ToDictionary(Key, StringComparer.Ordinal);
        var blocked = plan.BlockedLocations.GroupBy(item => Key(item.Opportunity), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Reason, StringComparer.Ordinal);
        var outcomes = results?.GroupBy(item => Key(item.Location), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var outcomesByPath = results?.GroupBy(item => item.Location.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var locations = plan.Preview.Locations.Concat(plan.BlockedLocations.Select(item => item.Opportunity))
            .Concat(results?.Select(item => item.Location) ?? []).DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = new List<CleanupInspectionEntry>(locations.Length);
        foreach (var location in locations)
        {
            string key = Key(location); bool canChoose = ready.ContainsKey(key), included = canChoose && omitted?.Contains(key) != true;
            var assessment = plan.BuildAssessments.GetValueOrDefault(location.DirectoryIndex) ?? new(CleanupReadiness.NeedsReview, null, "Review contents");
            CleanupLocationResult? result = outcomes?.GetValueOrDefault(key);
            if (result is null && results is not null && canChoose)
                for (string? path = location.Path; path is not null; path = Path.GetDirectoryName(path))
                    if (outcomesByPath?.TryGetValue(path, out result) == true) break;
            var decision = result is not null ? Classify(result, blocked.ContainsKey(key))
                : results is not null && canChoose && included ? CleanupInspectionDecision.NotStarted
                : canChoose ? included ? CleanupInspectionDecision.Ready : CleanupInspectionDecision.Skipped : CleanupInspectionDecision.Kept;
            string reason = result?.Message ?? (blocked.TryGetValue(key, out string? why) ? why
                : !included && canChoose ? "Skipped for this run; the saved rule is unchanged." : assessment.Reason);
            string project = projects?.GetValueOrDefault(location.Path) ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(location.Path)) ?? location.Path;
            long bytes = Math.Max(0, location.DisplayBytes(!plan.Preview.UsesLogicalSize));
            entries.Add(new(location, assessment, decision, reason, included, canChoose && results is null, bytes, bytes, project, plan.Snapshot.ScannedAtUtc));
        }
        return AttributeBytes(entries);
    }

    // History is a receipt, not a request to inspect the current filesystem.
    // These entries remain available while their original drive is offline.
    internal static IReadOnlyList<CleanupInspectionEntry> BuildRecord(CleanupRunRecord record,
        IReadOnlyDictionary<string, string>? projects = null)
    {
        var receipts = record.Receipts.GroupBy(item => item.ProviderId + "\0" + item.RuleId + "\0" + item.Path.ToUpperInvariant(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var entries = new List<CleanupInspectionEntry>();
        foreach (var target in record.Targets.DistinctBy(item => item.Location.Path, StringComparer.OrdinalIgnoreCase))
        {
            var location = target.Location; string key = Key(location);
            var receipt = receipts.GetValueOrDefault(key);
            var assessment = target.Assessment ?? new(target.Decision == "Kept" ? CleanupReadiness.Protected : CleanupReadiness.Ready, null, target.Reason);
            var decision = receipt is not null ? Classify(new(location, receipt.Outcome, receipt.Message, receipt.Attempted), target.Decision == "Kept")
                : target.Decision == "Kept" ? CleanupInspectionDecision.Kept
                : target.Decision == "Skipped" ? CleanupInspectionDecision.Skipped
                : record.State == CleanupRunState.Review ? CleanupInspectionDecision.Ready : CleanupInspectionDecision.NotStarted;
            bool hasAllocation = record.UsesLogicalSize is { } logical ? !logical : target.Identity?.HasAllocationData == true;
            long bytes = Math.Max(0, target.Identity is { } identity ? hasAllocation ? identity.AllocatedBytes : identity.LogicalBytes : location.DisplayBytes(hasAllocation));
            string project = projects?.GetValueOrDefault(location.Path) ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(location.Path)) ?? location.Path;
            entries.Add(new(location, assessment, decision, receipt?.Message ?? target.Reason, false, false, bytes, bytes, project,
                record.ScannedAtUtc == default ? record.CreatedUtc.UtcDateTime : record.ScannedAtUtc.UtcDateTime));
        }
        return AttributeBytes(entries);
    }

    private static IReadOnlyList<CleanupInspectionEntry> AttributeBytes(IReadOnlyList<CleanupInspectionEntry> entries)
    {
        // Attribute nested folder data once across all groups: each parent gets
        // its residual data after its nearest visible children are accounted for.
        var byPath = entries.Select((entry, index) => (entry.Location.Path, Index: index)).ToDictionary(e => Path.TrimEndingDirectorySeparator(e.Path), e => e.Index, StringComparer.OrdinalIgnoreCase);
        var accounted = entries.Select(entry => entry.SourceBytes).ToArray();
        for (int i = 0; i < entries.Count; i++)
            for (string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(entries[i].Location.Path)); parent is not null; parent = Path.GetDirectoryName(parent))
                if (byPath.TryGetValue(parent, out int index)) { accounted[index] = Math.Max(0, accounted[index] - entries[i].SourceBytes); break; }
        return entries.Select((entry, index) => entry with { AccountedBytes = accounted[index] }).ToArray();
    }

    internal static CleanupInspectionDecision Classify(CleanupLocationResult result, bool keptBeforeRun = false) => result.Outcome switch
    {
        CleanupOutcome.Recycled or CleanupOutcome.PermanentlyDeleted => CleanupInspectionDecision.Removed,
        CleanupOutcome.Uncertain or CleanupOutcome.Partial => CleanupInspectionDecision.NeedsChecking,
        CleanupOutcome.NotProcessed => CleanupInspectionDecision.NotStarted,
        CleanupOutcome.Cancelled when result.Attempted => CleanupInspectionDecision.NeedsChecking,
        CleanupOutcome.Cancelled => CleanupInspectionDecision.NotStarted,
        CleanupOutcome.Failed when result.Attempted => CleanupInspectionDecision.NeedsChecking,
        CleanupOutcome.Skipped when keptBeforeRun => CleanupInspectionDecision.Kept,
        _ => CleanupInspectionDecision.Skipped
    };
    private static bool Covers(string root, string path) => root.Equals(path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
