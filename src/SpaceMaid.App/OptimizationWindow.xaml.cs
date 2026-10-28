using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class OptimizationWindow : Window
{
    private readonly ScanSnapshot? _scan;
    private readonly string _scopePath;
    private readonly IReadOnlyList<Opportunity> _findings;
    private readonly FileOperations _operations;
    private readonly Action<OptimizationPolicy> _save;
    private readonly Func<OptimizationPreview, OptimizationPolicy, CancellationToken, Task<OptimizationResult>> _run;
    private readonly List<RuleRow> _rules;
    private readonly ObservableCollection<ExclusionRow> _exclusions;
    private readonly List<OptimizationActionRow> _actions;
    private readonly OptimizationIntegration? _integration;
    private OptimizationPreview? _preview;
    private OptimizationPolicy? _previewPolicy;
    private CancellationTokenSource? _work;
    private bool _closeAfterCancel, _scanChanged;
    private OptimizationPolicy _savedPolicy;
    private int _cursor;
    internal bool RequestedRescan { get; private set; }
    internal Extensions.OptimizationContribution? RequestedWorkflow { get; private set; }

    internal OptimizationWindow(ScanSnapshot? scan, IReadOnlyList<Opportunity> findings, FileOperations operations,
        OptimizationSettings settings, Action<OptimizationPolicy> save,
        Func<OptimizationPreview, OptimizationPolicy, CancellationToken, Task<OptimizationResult>> run, string? settingsError = null,
        OptimizationIntegration? integration = null)
    {
        InitializeComponent();
        _scan = scan; _findings = findings; _operations = operations; _save = save; _run = run; _integration = integration;
        var policy = settings.Policy; _savedPolicy = policy;
        _scopePath = scan?.RootPath ?? integration?.ScopePath ?? policy.ScopePath;
        bool sameSavedScope = scan is not null && string.Equals(policy.ScopePath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(scan.RootPath)), StringComparison.OrdinalIgnoreCase);
        // Manual previews own their cursor and never consume the unattended sweep.
        _cursor = 0;
        AutomaticBox.IsChecked = policy.Enabled && (scan is null || sameSavedScope);
        ScopeText.Text = _scopePath;
        AutomaticScopeText.Text = scan?.RootPath ?? (policy.ScopePath.Length > 0 ? policy.ScopePath : "Scan a location to configure automatic cleanup.");
        UpdateAutomaticStatus();
        DaysBox.Text = policy.KeepRecentDays.ToString(CultureInfo.InvariantCulture);
        LimitBox.Text = (policy.MaxBytesPerRun / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        _exclusions = new(policy.ExcludedPaths.Select(path => new ExclusionRow(path)));
        ExclusionsList.ItemsSource = _exclusions; UpdateExclusionsView();
        string ProviderName(string id) => integration?.ProviderNames.GetValueOrDefault(id) ?? (id.Length == 0 ? "Generated files" : id);
        _rules = findings.Where(f => f.AutomaticCleanup is not null)
            .DistinctBy(f => f.AutomaticCleanup!.ConsentKey(f.ProviderId))
            .Select(f => new RuleRow(f.AutomaticCleanup!.ConsentKey(f.ProviderId), f.AutomaticCleanup.Name, f.AutomaticCleanup.Regeneration,
                sameSavedScope && policy.EnabledRules.Contains(f.AutomaticCleanup.ConsentKey(f.ProviderId), StringComparer.Ordinal), ProviderName(f.ProviderId)))
            .OrderBy(r => r.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        RuleList.ItemsSource = _rules;
        NoRulesText.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _actions = _rules.Select(rule => new OptimizationActionRow(rule, findings.Count(f => f.AutomaticCleanup?.ConsentKey(f.ProviderId) == rule.Key)))
            .Concat((integration?.Discovery.Actions ?? []).Select(action => new OptimizationActionRow(action)))
            .OrderBy(row => row.Rule is not null ? 0 : row.Contribution?.Action.NativeTool is not null ? 1 : 2)
            .ThenBy(row => row.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (integration?.NativeTools is null)
            foreach (var action in _actions.Where(action => action.Contribution?.Action.NativeTool is not null)) action.Status = "Inspection unavailable";
        var view = CollectionViewSource.GetDefaultView(_actions);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OptimizationActionRow.Provider)));
        ActionList.ItemsSource = view;
        ActionList.SelectedItem = _actions.FirstOrDefault();
        if (_actions.Count == 0) ShowSelectedAction();
        StatusText.Text = settingsError ?? "";
        if (integration?.Discovery.Errors.Count > 0)
        {
            IntegrationErrors.Visibility = Visibility.Visible;
            IntegrationErrorText.Text = string.Join(Environment.NewLine, integration.Discovery.Errors);
        }
        SetWorking(false);
    }

    private OptimizationActionRow? SelectedAction => ActionList.SelectedItem as OptimizationActionRow;

    private OptimizationPolicy ReadPolicy(bool manual = false)
    {
        var excluded = _exclusions.Select(row => row.Path).ToArray();
        if (_scan is null)
        {
            if (manual) throw new InvalidOperationException("Scan a location before reviewing generated files.");
            // Browsing native tools must not replace an existing scheduled scope or consent.
            return (_savedPolicy with { Enabled = _savedPolicy.Enabled && AutomaticBox.IsChecked == true, ExcludedPaths = excluded }).Validate();
        }
        if (!int.TryParse(DaysBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int days) || days is < 1 or > 365)
            throw new ArgumentException("Keep recent activity: enter 1–365 days.");
        if (!int.TryParse(LimitBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int mib) || mib is < 1 or > 10240)
            throw new ArgumentException("Run limit: enter 1–10240 MiB.");
        var known = _rules.Select(rule => rule.Key).ToHashSet(StringComparer.Ordinal);
        bool sameScope = string.Equals(_savedPolicy.ScopePath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(_scan.RootPath)), StringComparison.OrdinalIgnoreCase);
        var rules = (sameScope ? _savedPolicy.EnabledRules.Where(key => !known.Contains(key)) : [])
            .Concat(_rules.Where(r => r.Enabled).Select(r => r.Key)).Distinct(StringComparer.Ordinal).ToArray();
        if (manual)
            rules = SelectedAction?.Rule is { } rule ? [rule.Key] : throw new InvalidOperationException("Choose a generated-file action.");
        else if (AutomaticBox.IsChecked == true && rules.Length == 0)
            throw new ArgumentException("Select at least one generated-file rule before enabling automatic cleanup.");
        return (_savedPolicy with { Enabled = manual || AutomaticBox.IsChecked == true, ScopePath = _scan.RootPath, KeepRecentDays = days,
            MaxBytesPerRun = (long)mib * 1024 * 1024, EnabledRules = rules, ExcludedPaths = excluded }).Validate();
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || _scan is null || SelectedAction?.Rule is null) return;
        if (_scanChanged) { RequestedRescan = true; Close(); return; }
        _preview = null; UpdatePreviewActions();
        using var work = new CancellationTokenSource(); _work = work; SetWorking(true);
        try
        {
            var policy = ReadPolicy(manual: true);
            if (_previewPolicy is not null && JsonSerializer.Serialize(policy) != JsonSerializer.Serialize(_previewPolicy)) _cursor = 0;
            _previewPolicy = policy;
            _preview = await _operations.PreviewOptimizationAsync(_scan, _findings, policy, work.Token, cursor: _cursor);
            _cursor = _preview.NextCursor;
            PreviewButton.Content = !_preview.CoverageComplete && _preview.NextCursor > 0 ? "Continue preview" : "Review files";
            FileList.ItemsSource = _preview.Files.Select(f => new DisplayFile(f.Path, Path.GetRelativePath(_scan.RootPath, f.Path),
                SizeText.Format(f.EstimatedBytes), f.Rule.Name, f.ContainerPath)).ToArray();
            ShowResults(_preview.Skipped);
            ActionsTab.IsSelected = GeneratedFilesTab.IsSelected = true;
            PreviewText.Text = $"{_preview.Files.Count:N0} files · {SizeText.Format(_preview.EstimatedBytes)}"
                + (_preview.UsesLogicalSize ? " logical size" : " estimated on disk") + (_preview.CoverageComplete ? "" : " · Partial inspection");
            PreviewEmptyText.Text = _preview.Files.Count == 0 ? "No eligible files in this preview. See Kept / results for details." : "";
            PreviewEmptyText.Visibility = _preview.Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectedAction!.Status = _preview.Files.Count == 0 ? "No eligible files" : $"{_preview.Files.Count:N0} files · {SizeText.Format(_preview.EstimatedBytes)}";
            StatusText.Text = "Only the exact files shown are eligible. Deletion is permanent; their producer recreates them when needed.";
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = error.Message; }
        finally { _work = null; SetWorking(false); if (_closeAfterCancel) Close(); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null) return;
        try
        {
            var policy = AutomaticTab.IsSelected ? ReadPolicy()
                : (_savedPolicy with { ExcludedPaths = _exclusions.Select(row => row.Path).ToArray() }).Validate();
            _save(policy); _savedPolicy = policy; UpdateAutomaticStatus();
            if (_scan is null) AutomaticBox.IsChecked = policy.Enabled;
            SetWorking(false);
            StatusText.Text = AutomaticTab.IsSelected
                ? policy.Enabled ? "Settings saved. Selected generated files will be checked hourly while open." : "Settings saved. Automatic cleanup is off."
                : "Exclusions saved.";
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = "Settings were not saved. " + error.Message; }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || _preview is null || _previewPolicy is null || !ActionsTab.IsSelected || !GeneratedFilesTab.IsSelected) return;
        using var work = new CancellationTokenSource(); _work = work; SetWorking(true);
        try
        {
            if (JsonSerializer.Serialize(ReadPolicy(manual: true)) != JsonSerializer.Serialize(_previewPolicy))
                throw new InvalidOperationException("Settings changed. Review files again before deleting.");
            StatusText.Text = "Rechecking generated files…";
            var result = await _run(_preview, _previewPolicy, work.Token);
            ShowResults(result.Files.Select(f => new OptimizationSkipped(f.Path, f.Outcome + " · " + f.Message)));
            StatusText.Text = $"{result.DeletedCount:N0} files deleted · {SizeText.Format(result.RemovedLogicalBytes)} in file contents removed"
                + (result.Cancelled ? " · Cancelled; completed deletions remain." : ".") + (result.Note is null ? "" : " " + result.Note);
            _scanChanged |= result.DeletedCount > 0;
            InvalidatePreview(); ResultsTab.IsSelected = true;
            PreviewText.Text = _scanChanged ? "Scan again to refresh estimates and review more files." : "Review files again before another run.";
            if (_scanChanged) PreviewButton.Content = "Scan again…";
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = error.Message; InvalidatePreview(); }
        finally { _work = null; SetWorking(false); if (_closeAfterCancel) Close(); }
    }

    private void ActionSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_actions is null || ActionList is null) return;
        string query = ActionSearch.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(_actions);
        view.Filter = item => item is OptimizationActionRow row && (query.Length == 0
            || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Provider.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (SelectedAction is null || !view.Contains(SelectedAction)) ActionList.SelectedItem = view.Cast<OptimizationActionRow>().FirstOrDefault();
        ShowSelectedAction();
    }

    private void Action_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_work is not null || _actions is null) return;
        InvalidatePreview(); ShowSelectedAction(); StatusText.Text = "";
    }

    private void ShowSelectedAction()
    {
        var row = SelectedAction;
        ActionTitle.Text = row?.Name ?? "Choose an optimization action";
        ActionDescription.Text = row?.Description ?? "Actions are contributed by enabled extensions.";
        ActionConsequence.Text = row?.Consequence ?? "";
        bool generated = row?.Rule is not null, native = row?.Contribution?.Action.NativeTool is not null;
        GeneratedReview.Visibility = generated ? Visibility.Visible : Visibility.Collapsed;
        NativeReview.Visibility = native ? Visibility.Visible : Visibility.Collapsed;
        WorkflowText.Visibility = row is not null && !generated && !native ? Visibility.Visible : Visibility.Collapsed;
        WorkflowText.Text = row?.Contribution is { } owner ? $"Opens {owner.OwnerName} for inspection and confirmation, using that workflow's own selection and exclusions." : "";
        NoActionsText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.Visibility = generated ? Visibility.Visible : Visibility.Collapsed;
        InspectToolButton.Visibility = native ? Visibility.Visible : Visibility.Collapsed;
        OpenWorkflowButton.Visibility = row is not null && !generated && !native ? Visibility.Visible : Visibility.Collapsed;
        OpenWorkflowButton.Content = row?.Contribution is { } entry ? "Open " + entry.OwnerName : "Open workflow";
        ShowNativeObservation(row);
        if (!generated) PreviewText.Text = "";
        if (row is not null) ActionList.ScrollIntoView(row);
        UpdatePreviewActions();
    }

    private void AutomaticSettings_Click(object sender, RoutedEventArgs e) => AutomaticTab.IsSelected = true;
    private void UpdateAutomaticStatus()
    {
        bool elsewhere = _savedPolicy.Enabled && _scopePath.Length > 0 && !string.Equals(_savedPolicy.ScopePath,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_scopePath)), StringComparison.OrdinalIgnoreCase);
        AutomaticStatusButton.Content = !_savedPolicy.Enabled ? "Automatic cleanup: off" : elsewhere ? "Automatic cleanup: on elsewhere" : "Automatic cleanup: on";
        AutomaticStatusButton.ToolTip = _savedPolicy.Enabled ? _savedPolicy.ScopePath : "Configure automatic generated-file cleanup";
        SavedScopeHint.Visibility = elsewhere ? Visibility.Visible : Visibility.Collapsed;
        SavedScopeHint.Text = !elsewhere ? "" : _scan is null
            ? $"Configured for {_savedPolicy.ScopePath}. Scan a location to change its rules."
            : $"Currently enabled for {_savedPolicy.ScopePath}. Saving this tab replaces that location.";
        NoRulesText.Text = _scan is null && _savedPolicy.Enabled
            ? "Scan this location to review or change its automatic rules. You can turn automatic cleanup off now."
            : "Scan a location with generated files to configure automatic cleanup.";
    }
    private void OpenWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || SelectedAction?.Contribution is not { Action.Workflow: not null } action) return;
        RequestedWorkflow = action; Close();
    }
    private void SetWorking(bool busy)
    {
        AutomaticBox.IsEnabled = !busy && (_scan is not null || _savedPolicy.Enabled);
        PolicyControls.IsEnabled = RuleList.IsEnabled = !busy && _scan is not null;
        ExclusionControls.IsEnabled = ActionList.IsEnabled = ActionSearch.IsEnabled = SaveButton.IsEnabled = !busy;
        InspectToolButton.IsEnabled = !busy && _integration?.NativeTools is not null;
        OpenWorkflowButton.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy && _scan is not null && SelectedAction?.Rule is not null;
        UpdatePreviewActions();
        CloseButton.Content = busy ? "Cancel" : "Close";
    }
    private void PreviewSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdatePreviewActions();
    private void UpdatePreviewActions()
    {
        if (RunButton is null) return;
        bool generated = SelectedAction?.Rule is not null;
        PreviewText.Visibility = ActionsTab.IsSelected && generated ? Visibility.Visible : Visibility.Collapsed;
        RunButton.Visibility = ActionsTab.IsSelected && generated ? Visibility.Visible : Visibility.Collapsed;
        RunButton.IsEnabled = _work is null && ActionsTab.IsSelected && generated && GeneratedFilesTab.IsSelected && _preview?.Files.Count > 0;
        KeepFileButton.Visibility = KeepFolderButton.Visibility = generated ? Visibility.Visible : Visibility.Collapsed;
        KeepFileButton.IsEnabled = KeepFolderButton.IsEnabled = _work is null && generated && FileList.SelectedItem is DisplayFile;
        SaveButton.Visibility = AutomaticTab.IsSelected || ExclusionsTab.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Content = AutomaticTab.IsSelected ? "Save automatic settings" : "Save exclusions";
        ReviewNativeButton.IsEnabled = _work is null && SelectedAction is { Observation.CanPrepare: true, NeedsInspection: false } && _integration?.ExecuteNative is not null;
    }
    private void InvalidatePreview()
    {
        _preview = null; _cursor = 0;
        FileList.ItemsSource = ResultList.ItemsSource = null;
        PreviewButton.Content = _scanChanged ? "Scan again…" : "Review files";
        PreviewEmptyText.Text = "Review files to check what can be removed.";
        PreviewEmptyText.Visibility = Visibility.Visible;
        PreviewText.Text = "";
        UpdatePreviewActions();
    }
    internal sealed record DisplayFile(string Path, string DisplayPath, string Size, string Rule, string Container);
    private void ShowResults(IEnumerable<OptimizationSkipped> results) => ResultList.ItemsSource = results.Select(r =>
        new { r.Path, DisplayPath = _scan is null ? r.Path : Path.GetRelativePath(_scan.RootPath, r.Path), r.Reason }).ToArray();
    private void Close_Click(object sender, RoutedEventArgs e) { if (_work is { } work) work.Cancel(); else Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    { if (_work is { } work) { e.Cancel = true; _closeAfterCancel = true; work.Cancel(); } }

    internal sealed class RuleRow(string key, string name, string regeneration, bool enabled, string provider = "")
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public string Provider { get; } = provider;
        public string Regeneration { get; } = regeneration;
        public bool Enabled { get; set; } = enabled;
    }
}
