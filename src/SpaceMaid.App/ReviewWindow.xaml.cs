using System.Windows;
using System.ComponentModel;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class ReviewWindow : Window
{
    private readonly Func<IProgress<CleanupProgress>, CancellationToken, Task<CleanupBatchResult>>? _execute;
    private CancellationTokenSource? _cleanup;
    private bool _closeWhenFinished;
    private readonly List<CleanupReviewRow> _locations;
    internal CleanupBatchResult? Result { get; private set; }
    internal Task? Execution { get; private set; }

    public ReviewWindow(CleanupPreview preview, string? validationNote = null)
    {
        InitializeComponent();
        ValidationText.Text = validationNote;
        ValidationText.Visibility = string.IsNullOrEmpty(validationNote) ? Visibility.Collapsed : Visibility.Visible;
        EstimateText.Text = SizeText.Format(preview.EstimatedBytes);
        EstimateCaption.Text = $"{(preview.UsesLogicalSize ? "Logical size; physical saving unknown" : "Estimated candidate allocation")}" +
            $" · {preview.Locations.Count:N0} {(preview.Locations.Count == 1 ? "location" : "locations")}"
            + $" · {preview.FileCount:N0} file {(preview.FileCount == 1 ? "object" : "objects")}";
        if (preview.ExcludedSharedBytes > 0)
            EstimateCaption.Text += $" · {SizeText.Format(preview.ExcludedSharedBytes)} shared allocation excluded";
        CautionText.Text = string.Join("\n\n", preview.Cautions);
        _locations = preview.Locations.Select(location => new CleanupReviewRow(location)).ToList();
        LocationList.ItemsSource = _locations;
        ResultText.Text = "Preview only: open Review selection in Opportunities to check live folders and enable cleanup.";
    }

    internal ReviewWindow(CleanupPlan plan, string validationNote,
        Func<IProgress<CleanupProgress>, CancellationToken, Task<CleanupBatchResult>> execute) : this(plan.Preview, validationNote)
    {
        _execute = execute;
        _locations.AddRange(plan.BlockedLocations.Select(item => new CleanupReviewRow(item.Opportunity) { Status = "Blocked: " + item.Reason }));
        LocationList.Items.Refresh();
        CleanupButton.IsEnabled = plan.CanExecute;
        ResultText.Text = $"{plan.Targets.Count:N0} folders ready · {plan.BlockedLocations.Count:N0} blocked. "
            + "Includes current contents, including source or custom files. Recycled data keeps occupying space until the bin is emptied.";
    }

    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (_execute is null || !CleanupButton.IsEnabled || Execution is not null) return;
        using var cleanup = new CancellationTokenSource(); _cleanup = cleanup;
        CleanupButton.IsEnabled = false;
        CloseButton.Content = "Cancel cleanup";
        CleanupProgressBar.Visibility = Visibility.Visible; CleanupProgressBar.IsIndeterminate = true;
        var progress = new Progress<CleanupProgress>(update =>
        {
            if (_cleanup is null) return;
            CleanupProgressBar.IsIndeterminate = update.Path.Length == 0;
            CleanupProgressBar.Maximum = Math.Max(1, update.Total); CleanupProgressBar.Value = update.Completed;
            ResultText.Text = $"{update.Completed:N0}/{update.Total:N0} · {update.Message}" + (update.Path.Length > 0 ? "\n" + update.Path : "");
        });
        try
        {
            var execution = _execute(progress, cleanup.Token); Execution = execution;
            Result = await execution;
            var outcomes = Result.Locations.ToDictionary(item => item.Location.Path, StringComparer.Ordinal);
            foreach (var row in _locations)
                if (outcomes.TryGetValue(row.Path, out var outcome))
                    row.Status = $"{outcome.Outcome}: {outcome.Message}";
            ResultText.Text = Result.Summary + (Result.ValidationError is { } error ? "\nValidation could not finish: " + error : "")
                + (Result.RefreshNote is { } note ? "\n" + note : "");
        }
        catch (Exception error) when (CleanupBatch.IsExpected(error))
        { ResultText.Text = "Cleanup could not finish: " + error.Message + " Some contents may already have moved. Recheck before trying again."; }
        finally
        {
            _cleanup = null; CleanupProgressBar.Visibility = Visibility.Collapsed;
            CloseButton.Content = "Close results";
            if (_closeWhenFinished) Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanup is not null) { _cleanup.Cancel(); ResultText.Text = "Cancelling… Waiting for the current Windows operation to stop."; }
        else Close();
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_cleanup is null) return;
        e.Cancel = true; _closeWhenFinished = true; _cleanup.Cancel();
    }
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        e.Handled = true; Close_Click(sender, e);
    }
}

internal sealed class CleanupReviewRow(Opportunity location) : INotifyPropertyChanged
{
    public Opportunity Location { get; } = location;
    public string Title => Location.Title;
    public string Path => Location.Path;
    public string Reason => Location.Reason;
    public string Caution => Location.Caution;
    public string SuggestedAction => Location.SuggestedAction;
    private string _status = "Ready for confirmation";
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
