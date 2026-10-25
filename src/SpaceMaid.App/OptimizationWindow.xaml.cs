using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class OptimizationWindow : Window
{
    private readonly ScanSnapshot _scan;
    private readonly IReadOnlyList<Opportunity> _findings;
    private readonly FileOperations _operations;
    private readonly Action<OptimizationPolicy> _save;
    private readonly Func<OptimizationPreview, OptimizationPolicy, CancellationToken, Task<OptimizationResult>> _run;
    private readonly List<RuleRow> _rules;
    private OptimizationPreview? _preview;
    private OptimizationPolicy? _previewPolicy;
    private CancellationTokenSource? _work;
    private bool _closeAfterCancel;

    internal OptimizationWindow(ScanSnapshot scan, IReadOnlyList<Opportunity> findings, FileOperations operations,
        OptimizationSettings settings, Action<OptimizationPolicy> save,
        Func<OptimizationPreview, OptimizationPolicy, CancellationToken, Task<OptimizationResult>> run, string? settingsError = null)
    {
        InitializeComponent();
        _scan = scan; _findings = findings; _operations = operations; _save = save; _run = run;
        var policy = settings.Policy;
        AutomaticBox.IsChecked = policy.Enabled && string.Equals(policy.ScopePath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(scan.RootPath)), StringComparison.OrdinalIgnoreCase);
        ScopeText.Text = scan.RootPath;
        DaysBox.Text = policy.KeepRecentDays.ToString(CultureInfo.InvariantCulture);
        LimitBox.Text = (policy.MaxBytesPerRun / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        ExclusionsBox.Text = string.Join(Environment.NewLine, policy.ExcludedPaths);
        _rules = findings.Where(f => f.AutomaticCleanup is not null)
            .DistinctBy(f => f.AutomaticCleanup!.ConsentKey(f.ProviderId))
            .Select(f => new RuleRow(f.AutomaticCleanup!.ConsentKey(f.ProviderId), f.AutomaticCleanup.Name,
                f.AutomaticCleanup.Regeneration, policy.EnabledRules.Contains(f.AutomaticCleanup.ConsentKey(f.ProviderId), StringComparer.Ordinal)))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        RuleList.ItemsSource = _rules;
        StatusText.Text = settingsError ?? settings.LastSummary ?? (_rules.Count == 0
            ? "No automatic cleanup rules found in this scan. Other opportunities still require review."
            : AutomaticBox.IsChecked == true ? "Automatic optimization is enabled for this location."
            : "Automatic optimization is off for this location. Select only rules you want to use.");
        if (settings.LastRunUtc is { } time && settingsError is null) StatusText.Text += " · " + time.ToLocalTime().ToString("g");
    }

    private OptimizationPolicy ReadPolicy()
    {
        if (!int.TryParse(DaysBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int days) || days is < 1 or > 365)
            throw new ArgumentException("Keep recent activity: enter 1–365 days.");
        if (!int.TryParse(LimitBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int mib) || mib is < 1 or > 10240)
            throw new ArgumentException("Run limit: enter 1–10240 MiB.");
        var rules = _rules.Where(r => r.Enabled).Select(r => r.Key).ToArray();
        if (AutomaticBox.IsChecked == true && rules.Length == 0) throw new ArgumentException("Select at least one rule before enabling automatic optimization.");
        return new OptimizationPolicy { Enabled = AutomaticBox.IsChecked == true, ScopePath = _scan.RootPath, KeepRecentDays = days,
            MaxBytesPerRun = (long)mib * 1024 * 1024, EnabledRules = rules,
            ExcludedPaths = ExclusionsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) }.Validate();
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null) return;
        _preview = null; RunButton.IsEnabled = false;
        using var work = new CancellationTokenSource(); _work = work; SetWorking(true);
        try
        {
            _previewPolicy = ReadPolicy();
            _preview = await _operations.PreviewOptimizationAsync(_scan, _findings, _previewPolicy, work.Token);
            FileList.ItemsSource = _preview.Files.Select(f => new { f.Path, DisplayPath = Path.GetRelativePath(_scan.RootPath, f.Path), Size = SizeText.Format(f.EstimatedBytes), Rule = f.Rule.Name }).ToArray();
            ShowResults(_preview.Skipped);
            PreviewText.Text = $"{_preview.Files.Count:N0} files · {SizeText.Format(_preview.EstimatedBytes)}"
                + (_preview.UsesLogicalSize ? " logical size" : " estimated on disk") + (_preview.Limited ? " · Run limit applied" : "");
            StatusText.Text = "Only these files can be deleted. Changed, open or protected files are checked again and kept.";
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = error.Message; }
        finally { _work = null; SetWorking(false); if (_closeAfterCancel) Close(); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { var policy = ReadPolicy(); _save(policy); StatusText.Text = policy.Enabled ? "Automatic optimization enabled for this location." : "Automatic optimization is off."; }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = "Settings were not saved. " + error.Message; }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || _preview is null || _previewPolicy is null) return;
        using var work = new CancellationTokenSource(); _work = work; SetWorking(true);
        try
        {
            if (JsonSerializer.Serialize(ReadPolicy()) != JsonSerializer.Serialize(_previewPolicy))
                throw new InvalidOperationException("Settings changed. Preview again before deleting files.");
            StatusText.Text = "Rechecking generated files…";
            // This explicit button authorizes this preview even when recurring cleanup is off.
            var result = await _run(_preview, _previewPolicy with { Enabled = true }, work.Token);
            ShowResults(result.Files.Select(f => new OptimizationSkipped(f.Path, f.Outcome + " · " + f.Message)));
            StatusText.Text = $"{result.DeletedCount:N0} files deleted · {SizeText.Format(result.RemovedLogicalBytes)} in file contents removed"
                + (result.Cancelled ? " · Cancelled; completed deletions remain." : ".") + " Free-space changes depend on filesystem allocation.";
            if (result.Note is not null) StatusText.Text += " " + result.Note;
            _preview = null; FileList.ItemsSource = null;
            PreviewText.Text = "Scan again to refresh estimates and find more generated files.";
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = error.Message; _preview = null; }
        finally { _work = null; SetWorking(false); if (_closeAfterCancel) Close(); }
    }

    private void SetWorking(bool busy)
    {
        AutomaticBox.IsEnabled = PolicyControls.IsEnabled = RuleList.IsEnabled = PreviewButton.IsEnabled = SaveButton.IsEnabled = !busy;
        RunButton.IsEnabled = !busy && _preview?.Files.Count > 0;
        CloseButton.Content = busy ? "Cancel" : "Close";
    }
    private void ShowResults(IEnumerable<OptimizationSkipped> results) => ResultList.ItemsSource = results.Select(r =>
        new { r.Path, DisplayPath = Path.GetRelativePath(_scan.RootPath, r.Path), r.Reason }).ToArray();
    private void Close_Click(object sender, RoutedEventArgs e) { if (_work is { } work) work.Cancel(); else Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    { if (_work is { } work) { e.Cancel = true; _closeAfterCancel = true; work.Cancel(); } }

    internal sealed class RuleRow(string key, string name, string regeneration, bool enabled)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public string Regeneration { get; } = regeneration;
        public bool Enabled { get; set; } = enabled;
    }
}
