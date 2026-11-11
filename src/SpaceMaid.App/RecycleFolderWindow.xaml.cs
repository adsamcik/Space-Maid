using System.Windows;
using System.Windows.Automation;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class RecycleFolderWindow : Window
{
    public RecycleFolderWindow(FolderDeletionTarget target) : this(target, administratorRetry: false) { }

    internal RecycleFolderWindow(FolderDeletionTarget target, bool administratorRetry)
    {
        InitializeComponent();
        TargetPath.Text = target.Path; TargetPath.ToolTip = target.Path;
        TargetName.Text = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(target.Path));
        TargetName.ToolTip = TargetName.Text;
        TargetSize.Text = target.HasAllocationData
            ? $"{SizeText.Format(target.AllocatedBytes)} · Estimate"
            : $"{SizeText.Format(target.LogicalBytes)} · Logical estimate";
        SizeDetails.Text = target.HasAllocationData
            ? $"Scanned allocation: {SizeText.Format(target.AllocatedBytes)} · Logical: {SizeText.Format(target.LogicalBytes)}"
            : $"Scanned logical size: {SizeText.Format(target.LogicalBytes)} · Allocation unknown";
        TargetSize.ToolTip = SizeDetails.Text;
        PartialScanNote.Visibility = target.IsIncomplete ? Visibility.Visible : Visibility.Collapsed;
        if (administratorRetry)
        {
            Title = "Retry recycling";
            Heading.Text = "Retry moving to the Recycle Bin?";
            RecycleNote.Text = "Windows may ask for administrator permission to move this folder and its contents to the Recycle Bin.";
            RecycleButton.Content = "Retry with administrator permission";
        }
    }

    internal RecycleFolderWindow(RecycleSelectionReview review) : this(review.Targets[0])
    {
        int count = review.Targets.Length;
        string folders = $"{count:N0} {(count == 1 ? "folder" : "folders")}";
        Title = "Recycle selected folders";
        Heading.Text = $"Move {folders} to the Recycle Bin?";
        RecycleButton.Content = $"Queue {folders}";
        SingleTargetHeader.Visibility = SizeDetails.Visibility = CoverageNote.Visibility = PartialScanNote.Visibility = Visibility.Collapsed;
        TargetPath.Text = string.Join(Environment.NewLine, review.Targets.Select(target => target.Path));
        TargetPath.ToolTip = null;
        TargetPath.Margin = new(0);
        TargetPath.MaxHeight = 220;
        AutomationProperties.SetName(TargetPath, "Exact folders to recycle");

        var notes = new List<string>();
        if (review.NestedSelections > 0)
            notes.Add($"{review.NestedSelections:N0} included with selected parents");
        if (review.Skipped.Length > 0)
        {
            notes.Add($"{review.Skipped.Length:N0} skipped · See Details");
            SkippedEntries.Text = string.Join(Environment.NewLine, review.Skipped.Select(entry => $"{entry.Path} · {entry.Reason}"));
            SkippedEntries.Visibility = Visibility.Visible;
        }
        SelectionNote.Text = string.Join(Environment.NewLine, notes);
        SelectionNote.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Recycle_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
