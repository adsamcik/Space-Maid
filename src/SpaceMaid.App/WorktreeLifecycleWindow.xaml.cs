using System.ComponentModel;
using System.Globalization;
using System.Windows;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class WorktreeLifecycleWindow : Window
{
    private readonly WorktreeRetirement _service;
    private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task> _run;
    private readonly Action<bool> _changed;
    private readonly List<CheckoutRow> _rows;
    private CancellationTokenSource? _work;
    private bool _closeAfterCancel;

    internal WorktreeLifecycleWindow(IEnumerable<string> paths, WorktreeRetirement service,
        Func<Func<CancellationToken, Task>, CancellationToken, Task> run, Action<bool> changed)
    {
        InitializeComponent(); _service = service; _run = run; _changed = changed;
        var state = service.Load();
        _rows = paths.Concat(state.Policies.Select(p => p.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(p => new CheckoutRow(p, "—", state.Policies.Any(s => s.Path == p) ? "Managed · Check current state" : "Not managed")).ToList();
        CheckoutList.ItemsSource = _rows; if (_rows.Count > 0) CheckoutList.SelectedIndex = 0;
        CheckoutList.SelectionChanged += (_, _) => LoadSelectedPolicy();
        RecoveryList.SelectionChanged += (_, _) =>
        { if (RecoveryList.SelectedItem is RecoveryRow row) StatusText.Text = row.Message + " Recovery ref: " + row.Reference; };
        RefreshRecovery(); LoadSelectedPolicy();
    }
    private string SelectedPath => (CheckoutList.SelectedItem as CheckoutRow)?.Path ?? throw new InvalidOperationException("Select a checkout first.");
    private Guid SelectedRecord => (RecoveryList.SelectedItem as RecoveryRow)?.Id ?? throw new InvalidOperationException("Select a recovery record first.");
    private void LoadSelectedPolicy()
    {
        if (CheckoutList.SelectedItem is not CheckoutRow row) return;
        var policy = _service.Load().Policies.FirstOrDefault(p => p.Path.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
        ManagedBox.IsChecked = policy is not null; AutomaticBox.IsChecked = policy?.Automatic ?? false;
        TargetBox.Text = policy?.IntegrationReference ?? "refs/heads/main"; DaysBox.Text = (policy?.InactiveDays ?? 30).ToString(CultureInfo.InvariantCulture);
        GraceBox.Text = (policy?.GraceDays ?? 7).ToString(CultureInfo.InvariantCulture);
    }
    private (int Days, int Grace) Retention()
    {
        if (!int.TryParse(DaysBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int days) || days is < 1 or > 365
            || !int.TryParse(GraceBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int grace) || grace is < 1 or > 365)
            throw new ArgumentException("Use 1–365 days for activity retention and recovery grace.");
        return (days, grace);
    }
    private async void Check_Click(object sender, RoutedEventArgs e) => await Perform(async token =>
    {
        string path = SelectedPath;
        string reference = TargetBox.Text.Trim();
        var result = await _service.AssessAsync(path, reference, token);
        int index = _rows.FindIndex(r => r.Path == path); _rows[index] = new(path, result.Branch ?? "—", result.Status);
        CheckoutList.Items.Refresh(); CheckoutList.SelectedIndex = index; TargetBox.Text = reference;
        StatusText.Text = result.Reason + (result.RetainedOnUpstream ? " HEAD is also retained on " + result.UpstreamReference + "." : "")
            + (result.HasIgnoredFiles ? " Ignored files: " + string.Join(", ", result.IgnoredPaths.Take(4)) : "");
    });
    private async void Enroll_Click(object sender, RoutedEventArgs e) => await Perform(async token =>
    {
        if (ManagedBox.IsChecked != true) throw new InvalidOperationException("Opt this reproducible checkout into management before saving its policy.");
        var retention = Retention();
        await _service.EnrollAsync(SelectedPath, TargetBox.Text.Trim(), retention.Days, retention.Grace, AutomaticBox.IsChecked == true, token);
        _changed(false); StatusText.Text = "Managed policy saved for this exact checkout and branch.";
    });
    private async void Quarantine_Click(object sender, RoutedEventArgs e) => await Perform(async token =>
    {
        var policy = _service.Load().Policies.SingleOrDefault(p => p.Path == SelectedPath) ?? throw new InvalidOperationException("Save a managed policy first.");
        var result = await _service.QuarantineAsync(policy, token); StatusText.Text = result.Message; RefreshRecovery(); _changed(true);
    });
    private async void Restore_Click(object sender, RoutedEventArgs e) => await Perform(async token =>
    { var result = await _service.RestoreAsync(SelectedRecord, token); StatusText.Text = result.Message; RefreshRecovery(); _changed(true); });
    private async void Finalize_Click(object sender, RoutedEventArgs e) => await Perform(async token =>
    { var result = await _service.FinalizeAsync(SelectedRecord, token); StatusText.Text = result.Message; RefreshRecovery(); _changed(true); });
    private async void Pause_Click(object sender, RoutedEventArgs e) => await Perform(async token =>
    {
        string path = RecoveryList.SelectedItem is RecoveryRow recovery ? recovery.Original : SelectedPath;
        await _service.SetAutomaticAsync(path, false, token); _changed(false); StatusText.Text = "Automatic retirement paused for this checkout.";
    });
    private async Task Perform(Func<CancellationToken, Task> operation)
    {
        if (_work is not null) return;
        using var work = new CancellationTokenSource(); _work = work; SetBusy(true);
        try { await _run(operation, work.Token); }
        catch (Exception error) when (CleanupBatch.IsExpected(error) || error is System.Text.Json.JsonException)
        { StatusText.Text = error is OperationCanceledException ? "Cancelled. Completed moves or removals remain recorded in Recovery." : error.Message; }
        finally { _work = null; SetBusy(false); if (_closeAfterCancel) Close(); }
    }
    private void SetBusy(bool busy)
    {
        PolicyControls.IsEnabled = CheckoutList.IsEnabled = RecoveryList.IsEnabled = CheckButton.IsEnabled = EnrollButton.IsEnabled
            = QuarantineButton.IsEnabled = RestoreButton.IsEnabled = FinalizeButton.IsEnabled = PauseButton.IsEnabled = !busy;
        CloseButton.Content = busy ? "Cancel" : "Close";
    }
    private void RefreshRecovery() => RecoveryList.ItemsSource = _service.Load().Records.OrderByDescending(r => r.QuarantinedUtc)
        .Select(r => new RecoveryRow(r.Id, r.Policy.Path, r.State.ToString(), r.FinalizeAfterUtc.ToLocalTime().ToString("g"), r.QuarantinePath, r.RecoveryReference, r.Message)).ToArray();
    private void Close_Click(object sender, RoutedEventArgs e) { if (_work is { } work) work.Cancel(); else Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    { if (_work is { } work) { e.Cancel = true; _closeAfterCancel = true; work.Cancel(); } }
    internal sealed record CheckoutRow(string Path, string Branch, string Status);
    internal sealed record RecoveryRow(Guid Id, string Original, string State, string FinalizeAfter, string Quarantine, string Reference, string Message);
}
