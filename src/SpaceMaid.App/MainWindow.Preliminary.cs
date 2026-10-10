using System.Windows;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private ScanPreliminary? _preliminary;

    internal void ShowPreliminary(ScanPreliminary preliminary)
    {
        _preliminary = preliminary;
        if (!_preliminaryUserSorted) _preliminarySortColumn = preliminary.HasAllocationData ? ScanSortColumn.Allocated : ScanSortColumn.Logical;
        PreliminaryCaption.Text = "Scanning… Folder totals and cleanup estimates will appear when ready.";
        PreliminaryHint.Text = "Waiting for file metadata…";
        RefreshPreliminaryRows();
        PreliminaryHint.Visibility = preliminary.LargestFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScannedSizeLabel.Text = preliminary.HasAllocationData ? "PRELIMINARY" : "PRELIMINARY LOGICAL";
        ScannedSizeText.Text = SizeText.Format(preliminary.HasAllocationData ? preliminary.AllocatedBytes : preliminary.LogicalBytes);
        ScannedSizeCaption.Text = preliminary.HasAllocationData
            ? $"{SizeText.Format(preliminary.LogicalBytes)} logical · Files counted once"
            : "Logical sizes only · Hard links may repeat";
        PreliminaryNote.Text = preliminary.HasAllocationData
            ? "Up to 100 files · Shared file allocation counted once · Unresolved paths are marked pending"
            : "Up to 100 files · Hard links may repeat without file identities · Unresolved paths are marked pending";
        View_Changed(this, new RoutedEventArgs());
    }

    private void ClearPreliminary()
    {
        _preliminary = null;
        _preliminarySortColumn = ScanSortColumn.Allocated;
        _preliminaryDescending = true;
        _preliminaryUserSorted = false;
        PreliminaryFiles.ItemsSource = null;
        PreliminaryCaption.Text = "Scanning… Folder totals and cleanup estimates will appear when ready.";
        PreliminaryHint.Text = "Waiting for file metadata…";
        View_Changed(this, new RoutedEventArgs());
    }

    internal void RetainCancelledPreliminary()
    {
        // Clear any partially prepared final tree/opportunities, then restore
        // only the bounded acquisition preview. It never enables cleanup review.
        var preliminary = _preliminary;
        var column = _preliminarySortColumn;
        bool descending = _preliminaryDescending, userSorted = _preliminaryUserSorted;
        ClearResults();
        if (preliminary is null) return;
        _preliminarySortColumn = column;
        _preliminaryDescending = descending;
        _preliminaryUserSorted = userSorted;
        ShowPreliminary(preliminary);
        PreliminaryCaption.Text = "Scan cancelled · These partial results cover only metadata read before cancellation.";
        PreliminaryHint.Text = "No file metadata was read before cancellation.";
        CandidateCaption.Text = "Unavailable for a cancelled scan";
    }
}

internal sealed record PreliminaryRow(PreliminaryFile File, bool HasAllocationData)
{
    public string PathText => File.Path ?? "Path pending…";
    public string Allocated => HasAllocationData ? SizeText.Format(File.AllocatedBytes) : "—";
    public string Logical => SizeText.Format(File.LogicalBytes);
}

// Capture the latest worker report synchronously as well as posting to WPF.
// Completion/cancellation can then drain it before ignoring queued UI callbacks.
internal sealed class ScanUiProgress(Action<ScanProgress> update) : IProgress<ScanProgress>
{
    private readonly IProgress<ScanProgress> _posted = new Progress<ScanProgress>(update);
    private ScanPreliminary? _latestPreliminary;
    public ScanPreliminary? LatestPreliminary => Volatile.Read(ref _latestPreliminary);
    public void Report(ScanProgress value)
    {
        if (value.Preliminary is { } preliminary) Volatile.Write(ref _latestPreliminary, preliminary);
        _posted.Report(value);
    }
}
