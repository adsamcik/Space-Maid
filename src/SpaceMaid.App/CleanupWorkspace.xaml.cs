using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal partial class CleanupWorkspace : UserControl
{
    private readonly FileOperations _operations;
    private readonly CleanupRunStore _store;
    private readonly CleanupRunCoordinator _coordinator;
    private readonly CleanupScheduleService _schedules;
    private readonly OpportunityDiscovery _discover;
    private readonly string _draftPath;
    private readonly CleanupInspector _inspector = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private ScanSnapshot? _scan;
    private ScanSnapshot? _latestScan;
    private IReadOnlyList<Opportunity> _latestCandidates = [];
    private IReadOnlyList<Opportunity> _candidates = [];
    private CleanupDefinition _definition = new();
    private CleanupDefinition? _savedDefinition;
    private CleanupPreparedRun? _prepared;
    private CleanupBatchResult? _result;
    private CleanupRunRecord? _shownRecord;
    private CleanupRunHeader? _activeBackgroundRun;
    private DraftSession? _draftSession;
    private bool _refreshingStatus;
    private CancellationTokenSource? _previewWork, _runWork;
    private Task? _runTask;
    private bool _startingRun;
    private bool ForegroundBusy => _startingRun || _runTask is not null;
    private bool _writing = true, _stopped, _loading, _savedView, _historyView, _inspecting, _snapshotChanged;
    private int _previewVersion;
    private string? _matchingSignature;
    private readonly HashSet<(string Provider, string Rule, string Path)> _omitted = [];
    private IReadOnlyList<string> _selectedScopes = [];
    private readonly Dictionary<string, bool> _manualChoices = new(StringComparer.Ordinal);

    internal event Action? BrowseRequested;
    internal event Action<string>? RevealRequested;
    internal event Action? AvailabilityChanged;
    internal Func<CleanupPreparedRun, IProgress<CleanupProgress>, CancellationToken, Task<CleanupBatchResult>>? ExecuteRun { get; set; }
    internal CleanupPreparedRun? Prepared => _prepared;
    internal CleanupBatchResult? Result => _result;
    internal Task? RunTask => _runTask;
    internal Task? PreviewTask { get; private set; }
    internal CleanupRunStore Store => _store;

    internal CleanupWorkspace(FileOperations operations, CleanupRunStore store, OpportunityDiscovery discover, string dataDirectory)
    {
        _operations = operations; _store = store; _discover = discover;
        _coordinator = new(operations, store, discover); _schedules = new(operations, store);
        _draftPath = Path.Join(dataDirectory, "workspace-draft.json");
        InitializeComponent(); InspectorHost.Child = _inspector;
        WeekdayBox.ItemsSource = Enum.GetValues<DayOfWeek>().OrderBy(day => ((int)day + 6) % 7).ToArray();
        WeekdayBox.SelectedItem = DayOfWeek.Monday; TimeZoneText.Text = TimeZoneInfo.Local.DisplayName;
        _inspector.InclusionChanged += (location, include) =>
        {
            var included = _inspector.SelectedLocations.Select(item => (item.ProviderId, item.RuleId, item.Path)).ToHashSet();
            _omitted.Clear();
            foreach (var item in _prepared?.Plan.Preview.Locations ?? [])
                if (!included.Contains((item.ProviderId, item.RuleId, item.Path))) _omitted.Add((item.ProviderId, item.RuleId, item.Path));
            UpdateImpact();
        };
        _inspector.ExcludeRequested += location =>
        {
            if (_runTask is not null) return;
            string before = ExclusionsBox.Text;
            var paths = before.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (!paths.Contains(location.Path, StringComparer.OrdinalIgnoreCase)) paths.Add(location.Path);
            ExclusionsBox.Text = string.Join(Environment.NewLine, paths);
            ShowNotice("Folder exclusion added. Save the cleanup to use it in future runs.");
            AddUndo("Undo exclusion", () => ExclusionsBox.Text = before);
        };
        _inspector.RevealRequested += location => RevealRequested?.Invoke(location.Path);
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); PreviewTask = RefreshPreviewAsync(false); };
        _draftTimer.Tick += (_, _) => { _draftTimer.Stop(); SaveDraft(); };
        _statusTimer.Tick += async (_, _) => await RefreshBackgroundStatusAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible)
            {
                await RefreshBackgroundStatusAsync();
                if (_prepared is null && _scan is not null && !_loading && !_savedView && !_historyView && _shownRecord is null)
                    PreviewTask = RefreshPreviewAsync(false);
            }
            else _statusTimer.Stop();
        };
        _writing = false; LoadDraft(); UpdateFormState();
    }

    internal void SetSource(ScanSnapshot? scan, IReadOnlyList<Opportunity> candidates, int initialDays = 30)
    {
        if (_stopped) return;
        if (scan is null)
        {
            if (_prepared is not null) { _snapshotChanged = true; UpdateEstimateButton.Visibility = Visibility.Visible; }
            return;
        }
        _latestScan = scan; _latestCandidates = candidates;
        bool first = _definition.DiscoveryRootPath.Length == 0;
        if (first)
        {
            _definition = _definition with { DiscoveryRootPath = scan.RootPath, ScopePaths = [scan.RootPath], InactiveDays = initialDays };
            WriteForm();
        }
        UseCurrentScanButton.Visibility = string.Equals(scan.RootPath, _definition.DiscoveryRootPath, StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.Equals(scan.RootPath, _definition.DiscoveryRootPath, StringComparison.OrdinalIgnoreCase)) return;
        if (_scan is not null && !ReferenceEquals(_scan, scan) && _prepared is not null) _snapshotChanged = true;
        _scan = scan; _candidates = candidates; UpdateManualChoices();
        UpdateScopes();
        if (IsVisible && _prepared is null && !_loading && !_savedView && !_historyView && _shownRecord is null)
            PreviewTask = RefreshPreviewAsync(false);
        else UpdateImpact();
    }

    internal void ReviewSelection(ScanSnapshot scan, IReadOnlyList<Opportunity> candidates, IReadOnlyList<string> scopes)
    {
        if (ForegroundBusy) { ShowNotice("Stop the current cleanup before changing its scope."); return; }
        var selectedPaths = scopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Selected(Opportunity item)
        {
            for (string? path = item.Path; path is not null; path = Path.GetDirectoryName(path)) if (selectedPaths.Contains(path)) return true;
            return false;
        }
        var selected = scopes.Count > 0 ? candidates.Where(Selected).ToArray() : [];
        var scopePaths = scopes.Count == 0 ? new[] { scan.RootPath } : scopes.ToArray();
        _definition = new() { DiscoveryRootPath = scan.RootPath, ScopePaths = scopePaths, InactiveDays = _definition.InactiveDays,
            Build = scopes.Count == 0 || selected.Any(item => item.IsBuildOutput), Cache = selected.Any(item => !item.IsBuildOutput && !item.IsWorktree && item.Appearance == OpportunityAppearance.Cache),
            Worktrees = selected.Any(item => item.IsWorktree), ManualRuleKeys = selected.Where(IsOtherRule).Select(ManualKey).Distinct(StringComparer.Ordinal).ToArray() };
        _scan = scan; _candidates = candidates; _selectedScopes = scopes; _savedDefinition = null;
        ResetPreview(); WriteForm(); ShowPlan(); UpdateScopes(); PreviewTask = RefreshPreviewAsync(false);
    }

    private static bool IsOtherRule(Opportunity item) => !item.IsBuildOutput && !item.IsWorktree && item.Appearance != OpportunityAppearance.Cache
        && item.ProviderId.Length > 0 && item.RuleId.Length > 0;
    private static string ManualKey(Opportunity item) => item.ProviderId + "/" + item.RuleId;
    private void UpdateManualChoices()
    {
        if (OtherRulesPanel is null) return;
        var scopes = _definition.ScopePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = _candidates.Where(IsOtherRule).Where(item => CleanupDefinition.InScopes(item.Path, scopes)).GroupBy(ManualKey, StringComparer.Ordinal).OrderBy(group => group.First().Title).ToArray();
        var existing = OtherRulesPanel.Children.OfType<CheckBox>().ToArray();
        if (existing.Select(check => (string)check.Tag).SequenceEqual(available.Select(group => group.Key), StringComparer.Ordinal))
        {
            foreach (var check in existing) check.IsChecked = _manualChoices.GetValueOrDefault((string)check.Tag);
            OtherRulesGroup.Visibility = available.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        OtherRulesPanel.Children.Clear();
        foreach (var group in available)
        {
            if (!_manualChoices.ContainsKey(group.Key)) _manualChoices[group.Key] = _definition.ManualRuleKeys.Contains(group.Key, StringComparer.Ordinal);
            var check = new CheckBox { Tag = group.Key, Content = group.First().Title, IsChecked = _manualChoices[group.Key], Margin = new(0, 5, 0, 5), ToolTip = group.First().Caution };
            AutomationProperties.SetName(check, "Include rule: " + group.First().Title);
            check.Checked += (_, _) => { _manualChoices[group.Key] = true; Conditions_Changed(check, new RoutedEventArgs()); };
            check.Unchecked += (_, _) => { _manualChoices[group.Key] = false; Conditions_Changed(check, new RoutedEventArgs()); };
            OtherRulesPanel.Children.Add(check);
        }
        OtherRulesGroup.Visibility = available.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateScopes()
    {
        _writing = true;
        try
        {
            var options = new List<ScopeChoice>();
            if (_definition.DiscoveryRootPath.Length > 0) options.Add(new("All locations · " + _definition.DiscoveryRootPath, [_definition.DiscoveryRootPath]));
            if (_definition.ScopePaths.Length > 0 && !_definition.ScopePaths.SequenceEqual([_definition.DiscoveryRootPath], StringComparer.OrdinalIgnoreCase))
                options.Add(new($"Selected locations · {_definition.ScopePaths.Length:N0}", _definition.ScopePaths));
            if (_selectedScopes.Count > 0 && !options.Any(option => option.Paths.SequenceEqual(_selectedScopes, StringComparer.OrdinalIgnoreCase)))
                options.Add(new($"Selected locations · {_selectedScopes.Count:N0}", _selectedScopes.ToArray()));
            ScopeBox.ItemsSource = options;
            ScopeBox.SelectedItem = options.FirstOrDefault(option => option.Paths.SequenceEqual(_definition.ScopePaths, StringComparer.OrdinalIgnoreCase)) ?? options.FirstOrDefault();
            ScopeBox.ToolTip = string.Join(Environment.NewLine, _definition.ScopePaths);
        }
        finally { _writing = false; }
    }

    private CleanupDefinition ReadForm(bool scheduling)
    {
        bool validDays = int.TryParse(DaysBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int days) && days is >= 1 and <= 365;
        if (BuildCheck.IsChecked == true && !validDays)
            throw new ArgumentException("Enter 1–365 days for build inactivity.");
        bool validGitDays = int.TryParse(WorktreeDaysBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int gitDays) && gitDays is >= 1 and <= 365;
        if (WorktreeCheck.IsChecked == true && WorktreeRuleBox.SelectedIndex != 2 && !validGitDays)
            throw new ArgumentException("Enter 1–365 days for worktree inactivity.");
        var definition = _definition with
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Build cleanup" : NameBox.Text.Trim(),
            Build = BuildCheck.IsChecked == true, Cache = CacheCheck.IsChecked == true, Worktrees = WorktreeCheck.IsChecked == true,
            InactiveDays = validDays ? days : _definition.InactiveDays, BuildAction = ActionBox.SelectedIndex == 1 ? BuildCleanupAction.PermanentlyDelete : BuildCleanupAction.Recycle,
            WorktreePolicy = new((WorktreeCleanupMode)Math.Max(0, WorktreeRuleBox.SelectedIndex), validGitDays ? gitDays : _definition.WorktreePolicy.InactiveDays),
            ExcludedPaths = ExclusionsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ManualRuleKeys = _manualChoices.Where(pair => pair.Value).Select(pair => pair.Key).ToArray(),
            Schedule = null, Enabled = false
        };
        if (scheduling && FrequencyBox.SelectedIndex == 1)
        {
            if (!TimeOnly.TryParseExact(TimeBox.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new ArgumentException("Enter the time as HH:mm, for example 09:00.");
            if (!decimal.TryParse(LimitGiBBox.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal gib) || gib <= 0 || gib > 1024
                || !int.TryParse(LimitTargetsBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                throw new ArgumentException("Enter a valid size and folder limit.");
            definition = definition with
            {
                Schedule = new((DayOfWeek)(WeekdayBox.SelectedItem ?? DayOfWeek.Monday), time, _definition.Schedule?.TimeZoneId ?? TimeZoneInfo.Local.Id, AutomaticRadio.IsChecked == true),
                Limits = _definition.Limits with { MaxBytes = checked((long)(gib * 1024 * 1024 * 1024)), MaxTargets = count }
            };
        }
        return definition.Validate();
    }

    private void WriteForm()
    {
        _writing = true;
        try
        {
            var d = _definition;
            _manualChoices.Clear(); foreach (string key in d.ManualRuleKeys) _manualChoices[key] = true;
            NameBox.Text = d.Name; BuildCheck.IsChecked = d.Build; CacheCheck.IsChecked = d.Cache; WorktreeCheck.IsChecked = d.Worktrees;
            DaysBox.Text = d.InactiveDays.ToString(CultureInfo.InvariantCulture); ActionBox.SelectedIndex = (int)d.BuildAction;
            WorktreeDaysBox.Text = d.WorktreePolicy.InactiveDays.ToString(CultureInfo.InvariantCulture); WorktreeRuleBox.SelectedIndex = (int)d.WorktreePolicy.Mode;
            ExclusionsBox.Text = string.Join(Environment.NewLine, d.ExcludedPaths);
            FrequencyBox.SelectedIndex = d.Schedule is null ? 0 : 1; WeekdayBox.SelectedItem = d.Schedule?.Day ?? DayOfWeek.Monday;
            TimeBox.Text = (d.Schedule?.Time ?? new TimeOnly(9, 0)).ToString("HH:mm", CultureInfo.InvariantCulture);
            AutomaticRadio.IsChecked = d.Schedule?.Automatic == true; AskFirstRadio.IsChecked = d.Schedule?.Automatic != true;
            LimitGiBBox.Text = ((decimal)d.Limits.MaxBytes / (1024 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture);
            LimitTargetsBox.Text = d.Limits.MaxTargets.ToString(CultureInfo.InvariantCulture);
            SaveOptions.IsExpanded = false; MoreOptions.IsExpanded = d.Cache || d.Worktrees || d.ExcludedPaths.Length > 0;
        }
        finally { _writing = false; }
        UpdateScopes(); UpdateManualChoices(); UpdateFormState();
    }

    private void Conditions_Changed(object sender, RoutedEventArgs e)
    {
        if (_writing || _stopped) return;
        _result = null; _shownRecord = null; UpdateFormState(); ScheduleDraftSave();
        _previewTimer.Stop(); _previewTimer.Start();
    }
    private void SaveFields_Changed(object sender, RoutedEventArgs e)
    { if (!_writing && !_stopped) { UpdateFormState(); ScheduleDraftSave(); } }
    private void Scope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_writing || ScopeBox.SelectedItem is not ScopeChoice choice) return;
        _definition = _definition with { ScopePaths = choice.Paths, RootIdentity = null, ScopeIdentities = [] }; Conditions_Changed(sender, e);
    }
    private void SaveExpansion_Changed(object sender, RoutedEventArgs e) { if (!_writing) UpdateFormState(); }

    private static string MatchingSignature(CleanupDefinition d) => JsonSerializer.Serialize(new { d.DiscoveryRootPath, d.ScopePaths, d.Build, d.Cache, d.Worktrees, d.ManualRuleKeys, d.InactiveDays, d.BuildAction, d.WorktreePolicy, d.ExcludedPaths });

    private async Task RefreshPreviewAsync(bool refreshScan)
    {
        if (_stopped || _runTask is not null || _shownRecord is not null) return;
        _previewWork?.Cancel(); _previewWork?.Dispose(); _previewWork = new();
        var work = _previewWork; int version = ++_previewVersion;
        try
        {
            var input = ReadForm(false); string signature = MatchingSignature(input);
            if (!refreshScan && signature == _matchingSignature && _prepared is not null) { UpdateImpact(); return; }
            _loading = true; CleanupButton.IsEnabled = false;
            FreshnessText.Text = refreshScan ? "Updating estimate…" : "Preparing estimate…";
            if (_scan is null || refreshScan || !string.Equals(_scan.RootPath, input.DiscoveryRootPath, StringComparison.OrdinalIgnoreCase))
            {
                if (input.DiscoveryRootPath.Length == 0) { EstimateText.Text = "Scan a location first"; return; }
                _scan = await _operations.MeasureFolderAsync(input.DiscoveryRootPath, work.Token);
                _candidates = await Task.Run(() => _discover(_scan, work.Token), work.Token);
            }
            var prepared = await _coordinator.PrepareAsync(input, _scan, _candidates, work.Token);
            if (_stopped || work.IsCancellationRequested || version != _previewVersion) return;
            _prepared = prepared; _definition = prepared.Definition with { Schedule = _definition.Schedule };
            AvailabilityChanged?.Invoke();
            _matchingSignature = signature; _result = null; _snapshotChanged = false;
            UpdateManualChoices(); UpdateInspector(); NoticeText.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is JsonException)
        { if (version == _previewVersion) ShowNotice(error.Message); }
        finally { if (version == _previewVersion) { _loading = false; UpdateImpact(); UpdateFormState(); } }
    }

    private IReadOnlyList<Opportunity> IncludedLocations() => _prepared?.Plan.Preview.Locations.Where(item => !_omitted.Contains((item.ProviderId, item.RuleId, item.Path))).ToArray() ?? [];
    private void UpdateImpact()
    {
        if (_prepared is null) { CleanupButton.IsEnabled = false; return; }
        var locations = IncludedLocations();
        try
        {
            var preview = locations.Count == _prepared.Plan.Preview.Locations.Count ? _prepared.Plan.Preview
                : _operations.EstimateCleanup(_prepared.Plan.Snapshot, locations);
            bool permanent = _prepared.Definition.Build && _prepared.Definition.BuildAction == BuildCleanupAction.PermanentlyDelete;
            EstimateText.Text = locations.Count == 0 ? "Nothing ready" : SizeText.Format(preview.EstimatedBytes);
            EstimateCaption.Text = permanent ? "Estimated data to clean" : "Estimated data to recycle";
            ReadyText.Text = $"{locations.Count:N0} {(locations.Count == 1 ? "folder" : "folders")} ready";
            int kept = _prepared.Plan.BlockedLocations.Count + _prepared.Plan.Preview.Locations.Count - locations.Count;
            KeptButton.Content = $"{kept:N0} kept"; KeptButton.Visibility = kept > 0 ? Visibility.Visible : Visibility.Collapsed;
            string folders = locations.Count == 1 ? "folder" : "folders";
            CleanupButton.Content = permanent ? $"Delete {locations.Count:N0} {folders} · permanent builds" : $"Recycle {locations.Count:N0} {folders}";
            CleanupButton.IsEnabled = !_loading && _runTask is null && locations.Count > 0 && TryReadForm(out _);
            FreshnessText.Text = _snapshotChanged ? "Scan changed · Update the estimate to include new matches." : $"Scan from {_prepared.Plan.Snapshot.ScannedAtUtc.ToLocalTime():g}";
            UpdateEstimateButton.Visibility = _snapshotChanged || locations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_shownRecord?.State == CleanupRunState.Review) UpdatePendingImpact();
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { CleanupButton.IsEnabled = false; ShowNotice(error.Message); }
    }

    private bool TryReadForm(out CleanupDefinition? definition)
    {
        try { definition = ReadForm(false); return true; }
        catch (Exception error) when (error is ArgumentException or OverflowException) { definition = null; return false; }
    }
    private void UpdateFormState()
    {
        if (BuildFields is null || OptionsSummary is null || WeeklyFields is null) return;
        BuildFields.Visibility = BuildCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UseCurrentScanButton.Visibility = _latestScan is not null && !string.Equals(_latestScan.RootPath, _definition.DiscoveryRootPath, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        WorktreeFields.Visibility = WorktreeCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        WorktreeDaysBox.IsEnabled = WorktreeRuleBox.SelectedIndex != 2;
        int exclusions = ExclusionsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        OptionsSummary.Text = $"Caches {(CacheCheck.IsChecked == true ? "on" : "off")} · Worktrees {(WorktreeCheck.IsChecked == true ? "on" : "off")}" + (exclusions > 0 ? $" · {exclusions:N0} exclusions" : "");
        int otherRules = _manualChoices.Values.Count(value => value); if (otherRules > 0) OptionsSummary.Text += $" · {otherRules:N0} other rules";
        bool automaticEligible = BuildCheck.IsChecked == true && CacheCheck.IsChecked != true && WorktreeCheck.IsChecked != true && !_manualChoices.Values.Any(selected => selected);
        AutomaticRadio.IsEnabled = automaticEligible;
        if (!automaticEligible && AutomaticRadio.IsChecked == true) { _writing = true; AskFirstRadio.IsChecked = true; _writing = false; }
        AutomaticHint.Text = automaticEligible ? "Only verified builds. Changed or protected folders stay kept." : "Other selected rules need review. Use Ask me first for this cleanup.";
        WeeklyFields.Visibility = FrequencyBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        bool weekly = FrequencyBox.SelectedIndex == 1, automatic = weekly && AutomaticRadio.IsChecked == true;
        ScheduleSummary.Text = weekly ? $"Every {WeekdayBox.SelectedItem} {TimeBox.Text} · {(automatic ? "Automatic" : "Ask first")}" + (IsSavedUnchanged() ? _savedDefinition!.Enabled ? "" : " · Paused" : " · Not saved") : IsSavedUnchanged() ? "Saved · Run manually" : "Not saved · Run manually";
        DraftText.Visibility = _savedDefinition is null ? Visibility.Collapsed : Visibility.Visible;
        NewButton.Visibility = _savedDefinition is not null || _savedView || _historyView ? Visibility.Visible : Visibility.Collapsed;
        DraftText.Text = _savedDefinition?.Name + (IsSavedUnchanged() ? "" : " · Unsaved changes");
        PermanentText.Visibility = BuildCheck.IsChecked == true && ActionBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        CleanupButton.Visibility = SaveOptions.IsExpanded ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.Content = automatic ? "Save and enable automatic cleanup" : weekly ? "Save weekly review" : "Save cleanup";
        if (weekly && _savedDefinition?.Schedule is not null && !_savedDefinition.Enabled) SaveButton.Content = "Save changes · schedule paused";
        string zone = _definition.Schedule?.TimeZoneId ?? TimeZoneInfo.Local.Id;
        TimeZoneText.Text = zone; UsePcZoneButton.Visibility = zone == TimeZoneInfo.Local.Id ? Visibility.Collapsed : Visibility.Visible;
        ScheduleConsent.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
        ScheduleConsent.Text = $"Every {WeekdayBox.SelectedItem}, clean verified builds in the selected scope, including future matching folders. Keep builds active within {DaysBox.Text} days. "
            + (ActionBox.SelectedIndex == 1 ? "Builds are permanently deleted, with no recovery. " : "Recycle only; permanent deletion is never a fallback. ")
            + $"Up to {LimitGiBBox.Text} GiB and {LimitTargetsBox.Text} folders per run, for at most {_definition.Limits.MaxMinutes} minutes. Exclusions remain protected.";
        if (!_loading) CleanupButton.IsEnabled = _prepared is not null && IncludedLocations().Count > 0 && TryReadForm(out var value) && MatchingSignature(value!) == _matchingSignature && _runTask is null;
    }

    private bool IsSavedUnchanged()
    {
        if (_savedDefinition is null) return false;
        try
        {
            var current = ReadForm(true);
            return MatchingSignature(current) == MatchingSignature(_savedDefinition) && current.Name == _savedDefinition.Name
                && current.Schedule == _savedDefinition.Schedule && current.Limits == _savedDefinition.Limits;
        }
        catch (Exception error) when (error is ArgumentException or OverflowException) { return false; }
    }

    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (ForegroundBusy || _prepared is null || _loading) return;
        _previewTimer.Stop(); _previewWork?.Cancel();
        try
        {
            if (_shownRecord is null && MatchingSignature(ReadForm(false)) != _matchingSignature) { await RefreshPreviewAsync(false); return; }
            var chosen = _prepared;
            _startingRun = true; _runWork = new(); _shownRecord = chosen.Record; _result = null;
            CleanupButton.IsEnabled = ApprovePendingButton.IsEnabled = false;
            _inspector.SetBusy(true); PlanPanel.Visibility = LibraryPanel.Visibility = ResultPanel.Visibility = Visibility.Collapsed;
            RunningPanel.Visibility = Visibility.Visible; RunningText.Text = "Preparing " + chosen.Definition.Name + "…";
            ViewRunningButton.Visibility = Visibility.Collapsed; RunProgress.IsIndeterminate = true; StopButton.IsEnabled = true;
            var selected = await _operations.RestrictCleanupAsync(chosen.Plan, IncludedLocations(), _runWork.Token);
            chosen = chosen with { Plan = selected };
            if (!selected.CanExecute) { ShowPlan(); ShowNotice("No folders are ready. Update the estimate to check again."); return; }
            _prepared = chosen; _shownRecord = chosen.Record; _result = null;
            _inspector.SetBusy(true); PlanPanel.Visibility = Visibility.Collapsed;
            LibraryPanel.Visibility = ResultPanel.Visibility = Visibility.Collapsed; RunningPanel.Visibility = Visibility.Visible;
            RunningText.Text = "Preparing " + chosen.Definition.Name + "…"; ViewRunningButton.Visibility = Visibility.Collapsed;
            RunProgress.IsIndeterminate = true;
            BackButton.Visibility = Visibility.Visible; StopButton.IsEnabled = true;
            PauseRunningButton.Visibility = chosen.Definition.Schedule is null ? Visibility.Collapsed : Visibility.Visible;
            var progress = new Progress<CleanupProgress>(update =>
            {
                RunningText.Text = update.Path.Length == 0 ? update.Message : $"{update.Completed:N0} of {update.Total:N0} · {update.Message}";
                RunProgress.IsIndeterminate = update.Path.Length == 0; RunProgress.Maximum = Math.Max(1, update.Total); RunProgress.Value = update.Completed;
            });
            var task = (ExecuteRun is null ? _coordinator.RunAsync(chosen, 0, progress, _runWork.Token) : ExecuteRun(chosen, progress, _runWork.Token));
            _runTask = task; _result = await task;
            _shownRecord = await Task.Run(() => _store.LoadRun(chosen.Record.Id));
            ShowResult(); SaveDraft();
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is JsonException)
        {
            ShowNotice("Cleanup stopped. " + error.Message);
            if (_shownRecord is { } attempted)
            {
                try
                {
                    _shownRecord = await Task.Run(() => _store.LoadRun(attempted.Id));
                    if (_shownRecord is null) _shownRecord = attempted with { State = _runTask is null ? CleanupRunState.Stopped : CleanupRunState.NeedsAttention,
                        Message = _runTask is null ? "Cleanup stopped before a run was admitted." : "The stored run receipt is unavailable. Outcomes need checking." };
                }
                catch (Exception readError) when (CleanupBatch.IsExpected(readError) || readError is JsonException)
                {
                    var receipts = _result?.Locations.Select(item => new CleanupTargetReceipt(item.Location.Path, item.Location.ProviderId, item.Location.RuleId,
                        item.Outcome, item.Message, item.Attempted, DateTimeOffset.UtcNow)).ToArray()
                        ?? attempted.Targets.Where(item => item.Decision == "Ready").Select(item => new CleanupTargetReceipt(item.Location.Path,
                            item.Location.ProviderId, item.Location.RuleId, CleanupOutcome.Uncertain, "The run receipt could not be read; its outcome is unconfirmed.", false, DateTimeOffset.UtcNow)).ToArray();
                    _shownRecord = attempted with { State = CleanupRunState.NeedsAttention, Receipts = receipts, Message = "Stored results could not be read. Keep the original journal for recovery. " + readError.Message };
                }
                ShowResult();
            }
            else ShowPlan();
        }
        finally { _startingRun = false; _runTask = null; _runWork?.Dispose(); _runWork = null; RunningPanel.Visibility = Visibility.Collapsed; _inspector.SetBusy(false); UpdateFormState(); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _runWork?.Cancel();
        Guid? id = ForegroundBusy ? _shownRecord?.Id : _activeBackgroundRun?.Id;
        try
        {
            if (id is not null) _store.RequestStop(id.Value);
            StopButton.IsEnabled = false; RunningText.Text = "Stopping after the current operation…";
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { ShowNotice("Cleanup could not receive Stop. " + error.Message); }
    }
    private async void PauseRunning_Click(object sender, RoutedEventArgs e)
    {
        Guid? id = ForegroundBusy ? _shownRecord?.Definition.Id : _activeBackgroundRun?.DefinitionId;
        if (id is null) return;
        try { await _schedules.PauseAsync(id.Value, true, CancellationToken.None); ShowNotice("Schedule paused. The current run continues until you stop it."); PauseRunningButton.Visibility = Visibility.Collapsed; }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { ShowNotice(error.Message); }
    }
    private async void UpdateEstimate_Click(object sender, RoutedEventArgs e) => await RefreshPreviewAsync(true);
    private void Inspect_Click(object sender, RoutedEventArgs e) { _inspecting = !_inspecting; UpdateInspector(); }
    private void Kept_Click(object sender, RoutedEventArgs e) { _inspecting = true; UpdateInspector(); _inspector.ShowKept(); }
    private void Browse_Click(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke();

    private void UpdateInspector()
    {
        InspectorHost.Visibility = _inspecting && (_prepared is not null || _shownRecord is not null) ? Visibility.Visible : Visibility.Collapsed;
        InspectButton.Content = InspectResultsButton.Content = _inspecting ? "Hide folder details" : _result is null && _shownRecord is null ? "Inspect folders" : "Inspect results";
        if (_inspecting && _shownRecord is not null && _shownRecord.State != CleanupRunState.Review) _inspector.ShowRecord(_shownRecord);
        else if (_inspecting && _prepared is not null)
        {
            _inspector.SetContext(_prepared.Record.Id); _inspector.Show(_prepared.Plan, _prepared.Definition, _result?.Locations ?? RecordResults(_shownRecord));
            if (_shownRecord is null && _result is null) _inspector.SetIncluded(IncludedLocations());
            _inspector.SetBusy(ForegroundBusy || _shownRecord?.State is not (null or CleanupRunState.Review));
        }
        else if (_inspecting && _shownRecord is not null) _inspector.ShowRecord(_shownRecord);
        DiagnosticsPanel.Visibility = _inspecting && _shownRecord is not null ? Visibility.Visible : Visibility.Collapsed; UpdateReport();
    }
    private static IReadOnlyList<CleanupLocationResult>? RecordResults(CleanupRunRecord? record)
    {
        if (record is null || record.State == CleanupRunState.Review) return null;
        return record.Targets.Select(target =>
        {
            var receipt = record.Receipts.LastOrDefault(item => item.Path == target.Location.Path && item.ProviderId == target.Location.ProviderId && item.RuleId == target.Location.RuleId);
            return receipt is null ? new(target.Location, CleanupOutcome.NotProcessed, target.Reason) : new CleanupLocationResult(target.Location, receipt.Outcome, receipt.Message, receipt.Attempted);
        }).ToArray();
    }
    private void ShowResult()
    {
        PlanPanel.Visibility = LibraryPanel.Visibility = Visibility.Collapsed; ResultPanel.Visibility = Visibility.Visible; BackButton.Visibility = Visibility.Visible;
        var record = _shownRecord; bool pending = record?.State == CleanupRunState.Review;
        var results = _result?.Locations ?? RecordResults(record) ?? [];
        int recycled = results.Count(item => item.Outcome == CleanupOutcome.Recycled), deleted = results.Count(item => item.Outcome == CleanupOutcome.PermanentlyDeleted);
        ResultTitle.Text = pending ? record!.Definition.Name : record?.State == CleanupRunState.NeedsAttention ? "Cleanup needs checking" : record?.State == CleanupRunState.Stopped ? "Cleanup stopped" : record?.State == CleanupRunState.Missed ? "Scheduled run missed" : "Cleanup finished";
        ResultPolicy.Text = Describe(record?.Definition ?? _definition);
        ResultHeadline.Text = pending ? _prepared is null ? "Locations need checking" : SizeText.Format(_prepared.Plan.Preview.EstimatedBytes)
            : record?.State == CleanupRunState.Missed ? "No cleanup ran"
            : record?.State == CleanupRunState.Dismissed ? "Review dismissed"
            : record?.State == CleanupRunState.Superseded ? "Review superseded"
            : record?.State is CleanupRunState.Running or CleanupRunState.Queued ? "Cleanup " + record.State.ToString().ToLowerInvariant()
            : deleted + recycled == 0 ? "No folders cleaned" : deleted > 0 && recycled == 0 ? $"{deleted:N0} {(deleted == 1 ? "build" : "builds")} deleted permanently" : deleted == 0 ? $"{recycled:N0} {(recycled == 1 ? "folder" : "folders")} recycled" : $"{deleted + recycled:N0} folders cleaned";
        ResultSummary.Text = pending ? $"{_prepared?.Plan.Preview.Locations.Count ?? 0:N0} ready · {record!.Targets.Count(item => item.Decision != "Ready"):N0} kept" : record?.Message ?? _result?.Summary ?? "Open the results for exact outcomes.";
        if (!pending && record is not null)
        {
            var decisions = CleanupInspectionData.BuildRecord(record).GroupBy(item => item.Decision)
                .Where(group => group.Key != CleanupInspectionDecision.Removed)
                .Select(group => $"{group.Count():N0} {CleanupInspectionData.Label(group.Key).ToLowerInvariant()}");
            ResultSummary.Text = string.Join(" · ", decisions) + (recycled > 0 ? "\nRecycled data still uses drive space until the bin is emptied." : "");
            if (record.State == CleanupRunState.NeedsAttention) ResultSummary.Text += "\nUncertain or partial outcomes need inspection and are not retried automatically.";
            if (record.Targets.Length == 0 || record.State is CleanupRunState.Missed or CleanupRunState.Dismissed or CleanupRunState.Superseded)
                ResultSummary.Text = record.Message ?? ResultSummary.Text;
        }
        if (pending && _prepared is null) ResultSummary.Text = "Reconnect the location, then reopen this review. Its captured folders remain unchanged.";
        DoneButton.Content = pending ? "Back" : "Done";
        ApprovePendingButton.Visibility = DismissPendingButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        ApprovePendingButton.IsEnabled = pending && _prepared?.Plan.CanExecute == true;
        ApprovePendingButton.Content = _prepared?.Definition.BuildAction == BuildCleanupAction.PermanentlyDelete ? "Delete approved builds permanently" : "Recycle approved folders";
        ReviewRemainingButton.Visibility = !pending && record is not null && RemainingTargets(record).Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateInspector();
        if (pending && _prepared is not null) UpdatePendingImpact();
    }

    private void UpdatePendingImpact()
    {
        if (_shownRecord?.State != CleanupRunState.Review || _prepared is null) return;
        var omitted = _prepared.Plan.Preview.Locations.Where(item => _omitted.Contains((item.ProviderId, item.RuleId, item.Path)))
            .Select(CleanupInspectionData.Key).ToHashSet(StringComparer.Ordinal);
        var entries = CleanupInspectionData.Build(_prepared.Plan, omitted);
        var selected = IncludedLocations();
        ResultHeadline.Text = SizeText.Format(selected.Count == _prepared.Plan.Preview.Locations.Count ? _prepared.Plan.Preview.EstimatedBytes
            : _operations.EstimateCleanup(_prepared.Plan.Snapshot, selected).EstimatedBytes);
        int kept = entries.Count(item => item.Decision == CleanupInspectionDecision.Kept), skipped = entries.Count(item => item.Decision == CleanupInspectionDecision.Skipped);
        ResultSummary.Text = $"{selected.Count:N0} ready · {kept:N0} kept" + (skipped > 0 ? $" · {skipped:N0} skipped for this run" : "");
        ApprovePendingButton.IsEnabled = selected.Count > 0 && _runTask is null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ForegroundBusy) return;
        try
        {
            var definition = ReadForm(true);
            if (_prepared is null || MatchingSignature(definition) != _matchingSignature) { await RefreshPreviewAsync(false); if (_prepared is null || MatchingSignature(definition) != _matchingSignature) return; }
            definition = definition with { RootIdentity = _prepared.Definition.RootIdentity, ScopeIdentities = _prepared.Definition.ScopeIdentities,
                Enabled = definition.Schedule is not null && (_savedDefinition?.Schedule is null || _savedDefinition.Enabled),
                Revision = _savedDefinition?.Revision ?? 1 };
            if (definition.Schedule?.Automatic == true)
            {
                var consent = await _coordinator.PrepareAsync(definition with { Enabled = false, AutomaticRuleConsents = [] }, _prepared.Plan.Snapshot, _candidates, CancellationToken.None);
                definition = definition with { AutomaticRuleConsents = consent.Definition.AutomaticRuleConsents };
            }
            _definition = await _schedules.SaveAsync(definition, CancellationToken.None); _savedDefinition = _definition;
            WriteForm(); SaveDraft(); await ShowLibraryAsync(false);
            ShowNotice(_definition.ScheduleError is { Length: > 0 } error ? "Saved, but the schedule is inactive. " + error : _definition.Schedule is null ? "Cleanup saved." : "Cleanup saved and scheduled.");
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is JsonException) { ShowNotice(error.Message); }
    }

    private async void Saved_Click(object sender, RoutedEventArgs e) => await ShowLibraryAsync(false);
    private async void History_Click(object sender, RoutedEventArgs e) => await ShowLibraryAsync(true);
    private int _historyLimit = 12;
    private async Task ShowLibraryAsync(bool history)
    {
        CaptureDraftSession();
        _savedView = !history; _historyView = history; PlanPanel.Visibility = ResultPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = BackButton.Visibility = Visibility.Visible; InspectorHost.Visibility = DiagnosticsPanel.Visibility = Visibility.Collapsed;
        NewButton.Visibility = Visibility.Visible;
        try
        {
            var data = await Task.Run(() => (Definitions: _store.LoadDefinitions(), Runs: _store.LoadRunHeaders()));
            if (_stopped) return; LibraryPanel.Children.Clear();
            AddLibraryHeading(history ? "History" : "Saved cleanups");
            if (!history)
            {
                foreach (var run in data.Runs.Where(run => run.State == CleanupRunState.Review))
                    AddLibraryRow(run.Name, "Review ready · " + run.CreatedUtc.ToLocalTime().ToString("g"),
                        ("Review", async () => await OpenRecordByIdAsync(run.Id)), ("Dismiss", async () => { _store.TryTransitionReview(run.Id, CleanupRunState.Dismissed); await ShowLibraryAsync(false); }));
                foreach (var definition in data.Definitions)
                {
                    string status = definition.Schedule is null ? "Run manually" : (definition.Enabled ? "Every " : "Paused · ") + definition.Schedule.Day + " " + definition.Schedule.Time.ToString("HH:mm") + (definition.Schedule.Automatic ? " · Automatic" : " · Ask first");
                    var actions = new List<(string, Func<Task>)> { ("Use cleanup", async () => await UseDefinitionAsync(definition)) };
                    if (definition.Schedule is not null) actions.Add((definition.Enabled ? "Pause" : "Resume", async () => { await _schedules.PauseAsync(definition.Id, definition.Enabled, CancellationToken.None); await ShowLibraryAsync(false); }));
                    actions.Add(("Remove", async () => { await _schedules.DeleteAsync(definition.Id, CancellationToken.None); await ShowLibraryAsync(false); }));
                    AddLibraryRow(definition.Name, status + (definition.ScheduleError is { Length: > 0 } error ? " · " + error : "") + Environment.NewLine + Describe(definition), actions.ToArray());
                }
                if (data.Definitions.Count == 0 && !data.Runs.Any(run => run.State == CleanupRunState.Review)) AddLibraryText("No saved cleanups yet.");
            }
            else
            {
                foreach (var run in data.Runs.OrderByDescending(run => run.CreatedUtc).Take(_historyLimit))
                    AddLibraryRow(run.Name, run.CreatedUtc.ToLocalTime().ToString("g") + " · " + run.Source + " · " + run.State + Environment.NewLine + run.Message,
                        ("View run", async () => await OpenRecordByIdAsync(run.Id)));
                if (data.Runs.Count == 0) AddLibraryText("No cleanup runs yet.");
                if (data.Runs.Count > _historyLimit) AddLibraryButton("Show older runs", async () => { _historyLimit += 12; await ShowLibraryAsync(true); });
            }
            await RefreshBackgroundStatusAsync();
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is JsonException) { ShowNotice("Saved cleanups could not load. " + error.Message); }
    }
    private void AddLibraryHeading(string text) => LibraryPanel.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) });
    private void AddLibraryText(string text) => LibraryPanel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) });
    private void AddLibraryButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 6, 12, 6) };
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => { try { button.IsEnabled = false; await action(); } catch (Exception error) when (CleanupBatch.IsExpected(error) || error is JsonException) { ShowNotice(error.Message); } finally { button.IsEnabled = true; } };
        LibraryPanel.Children.Add(button);
    }
    private void AddLibraryRow(string title, string description, params (string Label, Func<Task> Action)[] actions)
    {
        var stack = new StackPanel { Margin = new(0, 8, 0, 12) };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .8, Margin = new(0, 5, 0, 6) });
        var buttons = new WrapPanel();
        foreach (var action in actions)
        {
            var button = new Button { Content = action.Label, Margin = new(0, 4, 12, 4) }; AutomationProperties.SetName(button, action.Label + ": " + title);
            button.Click += async (_, _) => { try { button.IsEnabled = false; await action.Action(); } catch (Exception error) when (CleanupBatch.IsExpected(error) || error is JsonException) { ShowNotice(error.Message); } finally { button.IsEnabled = true; } };
            buttons.Children.Add(button);
        }
        stack.Children.Add(buttons); LibraryPanel.Children.Add(new Border { Child = stack, BorderThickness = new(0, 0, 0, 1) });
    }

    private async Task UseDefinitionAsync(CleanupDefinition definition)
    {
        if (ForegroundBusy) { ShowNotice("Stop the current cleanup before editing another cleanup."); return; }
        _draftSession = null; _definition = definition; _savedDefinition = definition; ResetPreview(); WriteForm(); ShowPlan();
        await RefreshPreviewAsync(_scan is null || !string.Equals(_scan.RootPath, definition.DiscoveryRootPath, StringComparison.OrdinalIgnoreCase));
        SaveDraft();
    }
    private async Task OpenRecordAsync(CleanupRunRecord record)
    {
        if (ForegroundBusy) { ShowNotice("The current cleanup is still running."); return; }
        CaptureDraftSession();
        _savedView = _historyView = false; _shownRecord = record; _result = null; _inspecting = false; _omitted.Clear();
        try { _prepared = record.State == CleanupRunState.Review ? await _coordinator.RestorePreparedAsync(record, CancellationToken.None) : null; }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { _prepared = null; ShowNotice("Some recorded locations are unavailable. " + error.Message); }
        ShowResult();
    }
    private async Task OpenRecordByIdAsync(Guid id)
    {
        var record = await Task.Run(() => _store.LoadRun(id));
        if (record is null) { ShowNotice("This cleanup run is no longer available."); return; }
        await OpenRecordAsync(record);
    }
    private async void ViewRunning_Click(object sender, RoutedEventArgs e)
    {
        if (_activeBackgroundRun is null) return;
        try { await OpenRecordByIdAsync(_activeBackgroundRun.Id); }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { ShowNotice(error.Message); }
    }
    private async void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (_shownRecord?.State != CleanupRunState.Review) return;
        try { _store.TryTransitionReview(_shownRecord.Id, CleanupRunState.Dismissed); await ShowLibraryAsync(false); }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is JsonException) { ShowNotice(error.Message); }
    }
    private async void Remaining_Click(object sender, RoutedEventArgs e)
    {
        if (_shownRecord is null) return;
        var record = _shownRecord;
        var next = record with { Id = Guid.NewGuid(), CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow, State = CleanupRunState.Review,
            Source = "Remaining", OccurrenceId = null, ApprovedUtc = null, ApprovalClaimId = null, Receipts = [], Targets = RemainingTargets(record) };
        await OpenRecordAsync(next);
    }
    internal static CleanupRunTarget[] RemainingTargets(CleanupRunRecord record)
    {
        var receipts = record.Receipts.ToDictionary(item => (item.ProviderId, item.RuleId, item.Path));
        return record.Targets.Where(target => target.Decision == "Ready" &&
            (receipts.TryGetValue((target.Location.ProviderId, target.Location.RuleId, target.Location.Path), out var receipt)
                ? !receipt.Attempted && receipt.Outcome is CleanupOutcome.NotProcessed or CleanupOutcome.Skipped or CleanupOutcome.Cancelled
                : record.State is CleanupRunState.Stopped or CleanupRunState.NeedsAttention)).ToArray();
    }
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (ForegroundBusy) { ShowNotice("Use Stop cleanup to end the current run."); return; }
        if (_draftSession is { } session)
        {
            _definition = session.Definition; _savedDefinition = session.SavedDefinition; _prepared = session.Prepared;
            _scan = session.Scan; _candidates = session.Candidates; _matchingSignature = session.Signature; _snapshotChanged = session.SnapshotChanged;
            _omitted.Clear(); foreach (var key in session.Omitted) _omitted.Add(key);
            _draftSession = null; ShowPlan(); _inspecting = session.Inspecting; UpdateImpact(); UpdateInspector();
            _inspector.RestorePresentation(session.InspectorPresentation); WorkspaceScroll.ScrollToVerticalOffset(session.Scroll);
            if (TryReadForm(out var pending) && MatchingSignature(pending!) != _matchingSignature) _previewTimer.Start();
            return;
        }
        ResetPreview(); ShowPlan(); PreviewTask = RefreshPreviewAsync(false);
    }
    private void CaptureDraftSession()
    {
        if (_draftSession is not null || PlanPanel.Visibility != Visibility.Visible || _runTask is not null) return;
        _previewTimer.Stop(); _previewWork?.Cancel(); _previewVersion++; _loading = false;
        _draftSession = new(_definition, _savedDefinition, _prepared, _scan, _candidates, _matchingSignature,
            _omitted.ToArray(), _snapshotChanged, _inspecting, WorkspaceScroll.VerticalOffset, _inspector.CapturePresentation());
    }
    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (ForegroundBusy) return;
        _draftSession = null; _savedDefinition = null;
        var scan = _latestScan ?? _scan;
        _definition = new() { DiscoveryRootPath = scan?.RootPath ?? "", ScopePaths = scan is null ? [] : [scan.RootPath] };
        _scan = scan; _candidates = _latestScan is null ? _candidates : _latestCandidates;
        ResetPreview(); WriteForm(); ShowPlan(); PreviewTask = RefreshPreviewAsync(false); SaveDraft();
    }
    private void ShowPlan()
    {
        _savedView = _historyView = false; PlanPanel.Visibility = Visibility.Visible;
        ResultPanel.Visibility = LibraryPanel.Visibility = BackButton.Visibility = Visibility.Collapsed;
        _shownRecord = null; _result = null; _inspecting = false; UpdateInspector(); UpdateFormState();
    }
    private void ResetPreview()
    { _prepared = null; _matchingSignature = null; _shownRecord = null; _result = null; _omitted.Clear(); _snapshotChanged = false; _inspecting = false; }

    private async Task RefreshBackgroundStatusAsync()
    {
        if (_stopped || !IsVisible || _loading || _refreshingStatus) return;
        _refreshingStatus = true;
        try
        {
            var data = await Task.Run(() => (Definitions: _store.LoadDefinitions(), Runs: _store.LoadRunHeaders()));
            int pending = data.Runs.Count(run => run.State == CleanupRunState.Review && run.Source != "Manual");
            PendingButton.Content = $"{pending:N0} {(pending == 1 ? "review ready" : "reviews ready")}"; PendingButton.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
            var active = data.Runs.FirstOrDefault(run => run.State is CleanupRunState.Queued or CleanupRunState.Running);
            if (!ForegroundBusy && active is not null)
            {
                _activeBackgroundRun = active; RunningPanel.Visibility = Visibility.Visible; RunningText.Text = active.Name + " · " + active.State;
                ViewRunningButton.Visibility = Visibility.Visible; RunProgress.IsIndeterminate = true; StopButton.IsEnabled = true;
                PauseRunningButton.Visibility = data.Definitions.Any(definition => definition.Id == active.DefinitionId && definition.Schedule is not null) ? Visibility.Visible : Visibility.Collapsed;
                CleanupButton.IsEnabled = false;
            }
            else if (!ForegroundBusy && RunningPanel.Visibility == Visibility.Visible)
            {
                RunningPanel.Visibility = Visibility.Collapsed;
                if (_activeBackgroundRun is { } previous && _shownRecord?.Id == previous.Id) { _shownRecord = await Task.Run(() => _store.LoadRun(previous.Id)); if (_shownRecord is not null) ShowResult(); }
                _activeBackgroundRun = null; ViewRunningButton.Visibility = Visibility.Collapsed;
            }
            if (active is not null || data.Definitions.Any(definition => definition.Enabled)) _statusTimer.Start(); else _statusTimer.Stop();
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is JsonException) { _statusTimer.Stop(); ShowNotice("Cleanup history could not refresh. " + error.Message); }
        finally { _refreshingStatus = false; }
    }

    private static string Describe(CleanupDefinition definition)
    {
        var parts = new List<string>();
        if (definition.Build) parts.Add((definition.BuildAction == BuildCleanupAction.PermanentlyDelete ? "Permanently delete" : "Recycle") + $" verified builds inactive for {definition.InactiveDays} days");
        if (definition.Cache) parts.Add("Recycle eligible cache folders");
        if (definition.Worktrees) parts.Add("Recycle clean worktrees matching " + definition.WorktreePolicy.Mode);
        if (definition.ManualRuleKeys.Length > 0) parts.Add($"{definition.ManualRuleKeys.Length:N0} other review-only rules");
        return string.Join(". ", parts) + ". " + string.Join("; ", definition.ScopePaths);
    }
    private void ShowNotice(string text) { NoticeText.Text = text; NoticeText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible; }
    private Button? _undoButton;
    private void AddUndo(string text, Action undo)
    {
        if (_undoButton is not null && _undoButton.Parent is Panel parent) parent.Children.Remove(_undoButton);
        _undoButton = new() { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 8) };
        _undoButton.Click += (_, _) => { undo(); if (_undoButton?.Parent is Panel p) p.Children.Remove(_undoButton); _undoButton = null; };
        ((StackPanel)NoticeText.Parent).Children.Insert(1, _undoButton);
    }
    private void UpdateReport()
    {
        if (_shownRecord is not { } record) return;
        bool paths = IncludePathsCheck.IsChecked == true;
        var text = new StringBuilder().AppendLine($"Run {record.Id} · {record.State} · {record.Source}").AppendLine($"Rule revision {record.Definition.Revision} · {record.CreatedUtc:O}");
        foreach (var receipt in record.Receipts) text.AppendLine($"{receipt.Outcome}: {(paths ? receipt.Path : "[folder]")} · {(paths ? receipt.Message : receipt.Outcome.ToString())}");
        ReportBox.Text = text.ToString();
    }
    private void ReportOptions_Changed(object sender, RoutedEventArgs e) => UpdateReport();
    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ReportBox.Text); ShowNotice("Report copied."); }
        catch (System.Runtime.InteropServices.COMException) { ShowNotice("The clipboard is busy. Select the report text to copy it."); }
    }

    private void SaveDraft()
    {
        if (_writing || _stopped) return;
        try
        {
            var draft = new WorkspaceDraft(_definition with { ManualRuleKeys = _manualChoices.Where(pair => pair.Value).Select(pair => pair.Key).ToArray() }, NameBox.Text, DaysBox.Text, WorktreeDaysBox.Text, ExclusionsBox.Text, TimeBox.Text, LimitGiBBox.Text, LimitTargetsBox.Text,
                BuildCheck.IsChecked == true, CacheCheck.IsChecked == true, WorktreeCheck.IsChecked == true, ActionBox.SelectedIndex, WorktreeRuleBox.SelectedIndex,
                FrequencyBox.SelectedIndex, (DayOfWeek)(WeekdayBox.SelectedItem ?? DayOfWeek.Monday), AutomaticRadio.IsChecked == true);
            string json = JsonSerializer.Serialize(draft);
            if (Encoding.UTF8.GetByteCount(json) > MaximumDraftBytes) throw new InvalidDataException("Choose project scopes to save this large selection as a draft.");
            _operations.Files.CreateDirectory(Path.GetDirectoryName(_draftPath)!);
            string temporary = _draftPath + ".tmp";
            _operations.Files.WriteAllText(temporary, json); _operations.Files.MoveFile(temporary, _draftPath, overwrite: true);
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is JsonException) { ShowNotice("Draft could not be saved. " + error.Message); }
    }
    private void ScheduleDraftSave() { _draftTimer.Stop(); _draftTimer.Start(); }
    private void UseCurrentScan_Click(object sender, RoutedEventArgs e)
    { if (_latestScan is not null) ReviewSelection(_latestScan, _latestCandidates, []); }
    private void UsePcZone_Click(object sender, RoutedEventArgs e)
    {
        if (_definition.Schedule is { } schedule) _definition = _definition with { Schedule = schedule with { TimeZoneId = TimeZoneInfo.Local.Id } };
        UpdateFormState(); ScheduleDraftSave();
    }
    private void LoadDraft()
    {
        try
        {
            if (!_operations.Files.FileExists(_draftPath)) return;
            using var input = _operations.Files.OpenRead(_draftPath); if (input.Length > MaximumDraftBytes) throw new InvalidDataException("Saved cleanup draft is too large.");
            var draft = JsonSerializer.Deserialize<WorkspaceDraft>(input) ?? throw new InvalidDataException("Saved cleanup draft is empty.");
            if (draft.Definition is null || draft.Definition.ScopePaths is null || draft.Definition.ExcludedPaths is null
                || draft.Definition.ManualRuleKeys is null || draft.Definition.ManualRuleKeys.Any(string.IsNullOrWhiteSpace)
                || draft.Definition.ScopeIdentities is null || draft.Definition.ScopeIdentities.Any(identity => identity is null)
                || draft.Definition.AutomaticRuleConsents is null || draft.Definition.AutomaticRuleConsents.Any(string.IsNullOrWhiteSpace)
                || draft.Definition.DiscoveryRootPath is null || draft.Definition.ScopePaths.Any(string.IsNullOrWhiteSpace)
                || draft.Definition.ExcludedPaths.Any(string.IsNullOrWhiteSpace) || draft.Definition.WorktreePolicy is null || draft.Definition.Limits is null
                || draft.Name is null || draft.Days is null || draft.WorktreeDays is null || draft.Exclusions is null || draft.Time is null
                || draft.LimitGiB is null || draft.LimitTargets is null || draft.Name.Length > 80 || draft.Exclusions.Length > 65536
                || draft.Definition.ScopePaths.Length > CleanupDefinition.MaximumScopes || draft.Definition.ScopeIdentities.Length > CleanupDefinition.MaximumScopes
                || draft.Definition.ExcludedPaths.Length > 1000 || draft.Definition.ManualRuleKeys.Length > 256
                || draft.Action is < 0 or > 1 || draft.WorktreeRule is < 0 or > 2 || draft.Frequency is < 0 or > 1 || !Enum.IsDefined(draft.Day))
                throw new InvalidDataException("Saved cleanup draft is incomplete or invalid.");
            _definition = draft.Definition; WriteForm(); _writing = true;
            NameBox.Text = draft.Name; DaysBox.Text = draft.Days; WorktreeDaysBox.Text = draft.WorktreeDays; ExclusionsBox.Text = draft.Exclusions;
            TimeBox.Text = draft.Time; LimitGiBBox.Text = draft.LimitGiB; LimitTargetsBox.Text = draft.LimitTargets;
            BuildCheck.IsChecked = draft.Build; CacheCheck.IsChecked = draft.Cache; WorktreeCheck.IsChecked = draft.Worktrees;
            ActionBox.SelectedIndex = draft.Action; WorktreeRuleBox.SelectedIndex = draft.WorktreeRule; FrequencyBox.SelectedIndex = draft.Frequency;
            WeekdayBox.SelectedItem = draft.Day; AutomaticRadio.IsChecked = draft.Automatic; AskFirstRadio.IsChecked = !draft.Automatic;
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is JsonException) { ShowNotice("Saved draft could not load. " + error.Message); }
        finally { _writing = false; }
    }
    internal async Task StopAsync()
    {
        bool dirty = _draftTimer.IsEnabled; _draftTimer.Stop(); if (dirty) SaveDraft(); _stopped = true; _statusTimer.Stop(); _previewTimer.Stop(); _previewWork?.Cancel(); _runWork?.Cancel();
        if (_runTask is not null) await _runTask;
    }
    internal Task RefreshStatusAsync() => RefreshBackgroundStatusAsync();
    private sealed record ScopeChoice(string Label, string[] Paths);
    private const int MaximumDraftBytes = 16 * 1024 * 1024;
    private sealed record DraftSession(CleanupDefinition Definition, CleanupDefinition? SavedDefinition, CleanupPreparedRun? Prepared,
        ScanSnapshot? Scan, IReadOnlyList<Opportunity> Candidates, string? Signature, (string Provider, string Rule, string Path)[] Omitted,
        bool SnapshotChanged, bool Inspecting, double Scroll, CleanupInspectorPresentation InspectorPresentation);
    private sealed record WorkspaceDraft(CleanupDefinition Definition, string Name, string Days, string WorktreeDays, string Exclusions, string Time,
        string LimitGiB, string LimitTargets, bool Build, bool Cache, bool Worktrees, int Action, int WorktreeRule, int Frequency, DayOfWeek Day, bool Automatic);
}
