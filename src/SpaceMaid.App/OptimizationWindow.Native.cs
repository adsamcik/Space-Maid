using System.Windows;
using SpaceMaid.Core.Optimization;

namespace SpaceMaid.App;

public partial class OptimizationWindow
{
    internal Func<ToolCachePlan, bool>? ConfirmMaintenance { get; set; }

    private async void InspectTool_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || _integration?.NativeTools is not { } tools || SelectedAction is not { Contribution.Action.NativeTool: { } kind } row) return;
        using var work = new CancellationTokenSource(); _work = work; SetWorking(true);
        try
        {
            StatusText.Text = "Inspecting " + row.Name + "…";
            PreviewText.Text = "";
            row.Observation = await tools.InspectAsync(kind, _scopePath, work.Token);
            row.NeedsInspection = false; row.Status = row.Observation.Status;
            ShowNativeObservation(row);
            StatusText.Text = row.Observation.CanPrepare ? "Review exact affected locations before maintenance." : row.Observation.Status;
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        { row.Observation = null; row.NeedsInspection = true; row.Status = "Inspection stopped · inspect again"; ShowNativeObservation(row); StatusText.Text = error.Message; }
        finally { _work = null; SetWorking(false); if (_closeAfterCancel) Close(); }
    }

    private void ShowNativeObservation(OptimizationActionRow? row)
    {
        var observed = row?.Observation;
        NativeStatusText.Text = _integration?.NativeTools is null ? "Native inspection is unavailable. Check unavailable actions below."
            : observed is null ? row?.NeedsInspection == true ? row.Status : "Inspect to check current availability and cache locations."
            : observed.Name + " " + observed.Version + " · " + (row?.NeedsInspection == true ? row.Status : observed.Status);
        NativeRootText.Text = observed?.Root ?? "";
        NativeDetailsText.Text = observed?.Details ?? "";
        NativeResources.ItemsSource = observed?.Resources;
        CommandText.Text = row?.Command ?? ""; ReportText.Text = row?.Report ?? "";
        InspectToolButton.Content = observed is null && row?.NeedsInspection != true ? "Inspect tool" : "Inspect again";
        ReviewNativeButton.Visibility = observed?.CanPrepare == true && row?.NeedsInspection != true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ReviewNative_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || _integration?.NativeTools is not { } tools || _integration.ExecuteNative is not { } execute || SelectedAction is not { Observation.CanPrepare: true, NeedsInspection: false } row) return;
        using var work = new CancellationTokenSource(); _work = work; SetWorking(true);
        try
        {
            StatusText.Text = "Rechecking the native tool and exact affected locations…";
            PreviewText.Text = "";
            var plan = await tools.PrepareAsync(row.Observation!, work.Token);
            if (_exclusions.Any(excluded => plan.AffectedPaths.Any(path => OptimizationPolicy.Covers(excluded.Path, path) || OptimizationPolicy.Covers(path, excluded.Path))))
                throw new InvalidOperationException("This maintenance would affect an excluded location. Remove that exclusion to proceed.");
            row.Command = plan.CommandPreview; CommandText.Text = plan.CommandPreview;
            bool confirmed = ConfirmMaintenance?.Invoke(plan) ?? MessageBox.Show(this,
                plan.Action + "\n\n" + plan.ConfirmationScopes + "\n\n" + plan.Consequence
                + "\n\nThis tool-wide action is not limited by the generated-file byte cap.",
                "Review native maintenance", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!confirmed) { StatusText.Text = "Maintenance cancelled before any native cleanup started."; return; }
            StatusText.Text = "Running " + plan.Action + "…";
            var result = await execute(plan, work.Token);
            row.Report = result.Output + (result.Error.Length > 0 ? "\n" + result.Error : "");
            row.NeedsInspection = true;
            row.Status = result.Completed ? "Maintenance completed" : "Inspect again before retrying";
            if (_scan is not null && plan.AffectedPaths.Any(path => OptimizationPolicy.Covers(path, _scan.RootPath) || OptimizationPolicy.Covers(_scan.RootPath, path)))
            { _scanChanged = true; InvalidatePreview(); }
            ShowNativeObservation(row); StatusText.Text = result.Summary;
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error)) { StatusText.Text = error.Message; }
        finally { _work = null; SetWorking(false); if (_closeAfterCancel) Close(); }
    }
}
