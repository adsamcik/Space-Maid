using System.ComponentModel;
using System.Globalization;
using System.Windows;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

public partial class ReviewWindow
{
    private IReadOnlyList<OpportunityGroup>? _reviewGroups;
    private IReadOnlyList<CleanupBlockedLocation> _reviewExclusions = [];
    private readonly List<CleanupRuleReviewRow> _rules = [];
    private readonly HashSet<string> _enabledRules = new(StringComparer.Ordinal);
    private readonly HashSet<(string Provider, string Rule, string Path)> _ruleExclusions = [];
    private CleanupPlan? _ruleBasis;
    private OpportunityDiscovery? _ruleDiscover;
    private bool _ruleReview, _ruleOptionsDirty;
    private string _ruleDaysText = "3";
    private BuildCleanupAction _buildAction;
    private string? _inspectedRule;

    internal static string RuleKey(Opportunity location) => location.IsBuildOutput ? "build-output"
        : $"{location.ProviderId}/{location.RuleId}/{location.Title}/{location.RequiresManualReview}";

    internal void EnableRuleReview(CleanupPlan basis, OpportunityDiscovery discover, bool selectedScope)
    {
        _ruleReview = true; _ruleBasis = basis; _ruleDiscover = discover;
        _ruleDaysText = _plan!.BuildPolicy.KeepRecentDays.ToString(CultureInfo.InvariantCulture);
        _buildAction = _plan.BuildAction;
        foreach (var row in _locations.Where(row => row.Selected)) _enabledRules.Add(RuleKey(row.Location));
        ScopeText.Text = (selectedScope ? "Selected locations · " : "All discovered locations · ") + basis.Snapshot.RootPath;
        ScopeText.ToolTip = ScopeText.Text; ScopeText.Visibility = Visibility.Visible;
        InspectFoldersButton.Visibility = Visibility.Visible;
        foreach (var group in _locations.GroupBy(row => RuleKey(row.Location))
            .OrderByDescending(group => group.Key == "build-output")
            .ThenByDescending(group => _enabledRules.Contains(group.Key))
            .ThenBy(group => group.First().Title, StringComparer.OrdinalIgnoreCase))
            _rules.Add(new(group.Key, group.ToArray(), _enabledRules.Contains(group.Key), _ruleDaysText,
                _buildAction, RuleSelectionChanged, RuleOptionsChanged));
        RuleList.ItemsSource = _rules;
        ShowRules(); UpdateRuleSummary(); UpdatePermanentWarning();
        ScheduleEstimate();
    }

    private void PopulatePlanRows(CleanupPlan plan)
    {
        _locations.Clear();
        foreach (var location in plan.Preview.Locations)
        {
            var assessment = plan.BuildAssessments.GetValueOrDefault(location.DirectoryIndex)
                ?? new BuildCleanupAssessment(CleanupReadiness.NeedsReview, null, "Review contents");
            bool selected = _ruleReview ? _enabledRules.Contains(RuleKey(location))
                && !_ruleExclusions.Contains((location.ProviderId, location.RuleId, location.Path))
                : assessment.State == CleanupReadiness.Ready;
            _locations.Add(new(location, !plan.Preview.UsesLogicalSize, assessment, selected, true, RowSelection_Changed));
        }
        var present = _locations.Select(row => row.Identity).ToHashSet();
        var excluded = _reviewExclusions.ToHashSet();
        foreach (var blocked in plan.BlockedLocations.Concat(_reviewExclusions))
        {
            if (!present.Add((blocked.Opportunity.ProviderId, blocked.Opportunity.RuleId, blocked.Opportunity.Path))) continue;
            DateTime? activity = !excluded.Contains(blocked)
                && plan.BuildAssessments.TryGetValue(blocked.Opportunity.DirectoryIndex, out var assessment) ? assessment.LastActivityUtc : null;
            _locations.Add(new(blocked.Opportunity, !plan.Preview.UsesLogicalSize,
                new(CleanupReadiness.Protected, activity, blocked.Reason), false, false));
        }
    }

    private async Task<CleanupPlan> CalculateSelectionAsync(CancellationToken token, int version)
    {
        if (_ruleReview && _ruleOptionsDirty)
        {
            if (!int.TryParse(_ruleDaysText, NumberStyles.None, CultureInfo.InvariantCulture, out int days) || days is < 1 or > 365)
                throw new ArgumentException("Enter an inactivity period of 1–365 days.");
            var reviewed = await _operations.ReviewCleanupRulesAsync(_ruleBasis!, new(days), _buildAction, _ruleDiscover!, token);
            token.ThrowIfCancellationRequested();
            if (_closed || version != _selectionVersion) throw new OperationCanceledException(token);
            _plan = reviewed; _ruleOptionsDirty = false;
            PopulatePlanRows(reviewed); ApplyRuleExclusions(); BuildGroups(_reviewGroups);
            foreach (var rule in _rules) rule.ReplaceRows(_locations.Where(row => RuleKey(row.Location) == rule.Key).ToArray());
            ApplyFilters(false); UpdateRuleSummary();
        }
        var choices = _locations.Where(row => row.Selected && row.CanSelect).Select(row => row.Location).ToArray();
        return await _operations.RestrictCleanupAsync(_plan!, choices, token);
    }

    private void ApplyRuleExclusions()
    {
        if (!_ruleReview) return;
        var excludedPaths = _ruleExclusions.Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in excludedPaths)
            for (string? current = path; current is not null; current = System.IO.Path.GetDirectoryName(current)) ancestors.Add(current);
        _selectionBatch = true;
        try
        {
            foreach (var row in _locations.Where(row => row.CanSelect))
            {
                bool excluded = ancestors.Contains(row.Path);
                for (string? current = row.Path; !excluded && current is not null; current = System.IO.Path.GetDirectoryName(current))
                    excluded = excludedPaths.Contains(current);
                if (excluded) row.Selected = false;
                row.Status = excluded ? "Kept · Excluded folder or contains an exclusion"
                    : row.Assessment.State == CleanupReadiness.Ready ? "Ready" : "Needs review";
            }
        }
        finally { _selectionBatch = false; }
    }

    private void RuleSelectionChanged(CleanupRuleReviewRow rule, bool selected)
    {
        if (_hasResults || Execution is not null) return;
        if (selected) _enabledRules.Add(rule.Key); else _enabledRules.Remove(rule.Key);
        _selectionBatch = true;
        try
        {
            foreach (var row in _locations.Where(row => RuleKey(row.Location) == rule.Key))
                row.Selected = selected && !_ruleExclusions.Contains(row.Identity);
        }
        finally { _selectionBatch = false; }
        ApplyRuleExclusions();
        ApplyFilters(false); UpdateRuleSummary(); ScheduleEstimate();
    }

    private void RuleOptionsChanged(CleanupRuleReviewRow rule)
    {
        if (_hasResults || Execution is not null) return;
        _ruleDaysText = rule.KeepDays; _buildAction = (BuildCleanupAction)rule.BuildActionIndex;
        _ruleOptionsDirty = true; UpdatePermanentWarning(); ScheduleEstimate();
    }

    private void UpdatePermanentWarning()
    {
        if (!_ruleReview) return;
        bool permanent = _buildAction == BuildCleanupAction.PermanentlyDelete
            && _locations.Any(row => row.Selected && row.CanSelect && row.Location.IsBuildOutput);
        PermanentWarning.Visibility = permanent ? Visibility.Visible : Visibility.Collapsed;
        PermanentWarning.Text = "Included verified builds will be permanently deleted and cannot be restored from the Recycle Bin. Other rules recycle their folders.";
        CleanupButton.Content = permanent ? "Run cleanup · delete builds permanently" : "Run cleanup";
    }

    private void UpdateRuleSummary()
    {
        if (!_ruleReview) return;
        if (Execution is null && !_hasResults)
        {
            var selected = _locations.Where(row => row.Selected && row.CanSelect).Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var row in _locations)
            {
                row.IncludedByParent = false;
                for (string? parent = System.IO.Path.GetDirectoryName(row.Path); parent is not null; parent = System.IO.Path.GetDirectoryName(parent))
                    if (selected.Contains(parent)) { row.IncludedByParent = true; break; }
                if (row.IncludedByParent && !row.Selected) row.Status = "Included through a selected parent folder";
            }
        }
        foreach (var rule in _rules) rule.Update(_enabledRules.Contains(rule.Key), _hasResults || Execution is not null);
        UpdatePermanentWarning();
    }

    private void ShowFolderBrowser()
    {
        RulesScroll.Visibility = Visibility.Collapsed;
        FolderBrowser.Visibility = SelectionControls.Visibility = Visibility.Visible;
        BackToRulesButton.Visibility = Visibility.Visible;
        InspectFoldersButton.Visibility = Visibility.Collapsed;
    }
    private void ShowRules()
    {
        RulesScroll.Visibility = Visibility.Visible;
        FolderBrowser.Visibility = SelectionControls.Visibility = Visibility.Collapsed;
        BackToRulesButton.Visibility = Visibility.Collapsed;
        InspectFoldersButton.Visibility = Visibility.Visible;
    }
    private void BackToRules_Click(object sender, RoutedEventArgs e) => ShowRules();
    private void InspectFolders_Click(object sender, RoutedEventArgs e)
    { _inspectedRule = null; ShowFolderBrowser(); ApplyFilters(false); }
    private void InspectRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CleanupRuleReviewRow rule }) return;
        _inspectedRule = rule.Key; ShowFolderBrowser(); ApplyFilters(true);
    }
}

internal sealed class CleanupRuleReviewRow : INotifyPropertyChanged
{
    private IReadOnlyList<CleanupReviewRow> _rows;
    private readonly Action<CleanupRuleReviewRow, bool> _selection;
    private readonly Action<CleanupRuleReviewRow> _options;
    private bool _checked, _finished;
    private string _keepDays;
    private int _action;
    public string Key { get; }
    public bool IsBuildRule => Key == "build-output";
    public Visibility BuildOptionsVisibility => IsBuildRule ? Visibility.Visible : Visibility.Collapsed;
    public string Title => IsBuildRule ? "Verified rebuildable build output" : _rows[0].Location.Title;
    public string Description => IsBuildRule ? "Keep builds with recent output or project changes. Unverified builds stay kept."
        : _rows[0].Location.Reason;
    public string Consequence => IsBuildRule ? "" : _rows[0].Location.Caution;
    public bool CanSelect => !_finished && _rows.Any(row => row.CanSelect);
    public bool CanEdit => !_finished;
    public bool Checked { get => _checked; set { if (_checked == value) return; _checked = value; _selection(this, value); } }
    public string KeepDays { get => _keepDays; set { if (_keepDays == value) return; _keepDays = value; _options(this); } }
    public int BuildActionIndex { get => _action; set { if (value is < 0 or > 1 || _action == value) return; _action = value; _options(this); } }
    public IReadOnlyList<string> BuildActions { get; } = ["Recycle", "Permanently delete verified builds"];
    public string Size
    {
        get
        {
            var included = _rows.Where(row => row.Selected && row.CanSelect || row.IncludedByParent).ToArray();
            var paths = included.Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
            long bytes = 0;
            foreach (var row in included)
            {
                bool covered = false;
                for (string? parent = System.IO.Path.GetDirectoryName(row.Path); parent is not null; parent = System.IO.Path.GetDirectoryName(parent))
                    if (paths.Contains(parent)) { covered = true; break; }
                if (!covered) bytes = checked(bytes + row.Bytes);
            }
            return SizeText.Format(bytes);
        }
    }
    public string Effect => _finished
        ? $"{_rows.Count(row => row.IsRecycled):N0} recycled · {_rows.Count(row => row.Result?.Outcome == CleanupOutcome.PermanentlyDeleted):N0} deleted · {_rows.Count(row => row.Result?.Outcome is CleanupOutcome.Skipped or CleanupOutcome.Failed or CleanupOutcome.Cancelled or CleanupOutcome.NotProcessed):N0} not completed"
        : $"{_rows.Count(row => row.Selected && row.CanSelect):N0} included"
            + (_rows.Any(row => !row.Selected && row.IncludedByParent) ? $" · {_rows.Count(row => !row.Selected && row.IncludedByParent):N0} included through another rule" : "")
            + $" · {_rows.Count(row => (!row.Selected || !row.CanSelect) && !row.IncludedByParent):N0} kept";
    public string InspectLabel => $"Inspect {_rows.Count:N0} {(_rows.Count == 1 ? "folder" : "folders")}";
    public string SelectionHint => $"Include rule: {Title}";
    public CleanupRuleReviewRow(string key, IReadOnlyList<CleanupReviewRow> rows, bool selected, string days,
        BuildCleanupAction action, Action<CleanupRuleReviewRow, bool> selection, Action<CleanupRuleReviewRow> options)
    { Key = key; _rows = rows; _checked = selected; _keepDays = days; _action = (int)action; _selection = selection; _options = options; }
    public void ReplaceRows(IReadOnlyList<CleanupReviewRow> rows) => _rows = rows;
    public void Update(bool selected, bool finished)
    {
        _checked = selected; _finished = finished;
        foreach (string name in new[] { nameof(Checked), nameof(CanSelect), nameof(CanEdit), nameof(Size), nameof(Effect), nameof(InspectLabel) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
