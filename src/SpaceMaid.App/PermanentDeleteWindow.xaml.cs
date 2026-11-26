using System.Windows;
using System.Windows.Automation;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class PermanentDeleteWindow : Window
{
    public PermanentDeleteWindow(DeletionTarget target, string? recycleWarning = null)
    {
        InitializeComponent();
        string kind = target.IsDirectory ? "folder" : "file";
        Heading.Text = $"Delete this {kind} permanently?";
        Consequence.Text = target.IsDirectory
            ? "When this runs, it deletes the folder and everything inside it. This can’t be undone."
            : "When this runs, it deletes the file. This can’t be undone.";
        if (recycleWarning is not null)
        {
            RecycleWarning.Text = recycleWarning;
            RecycleWarning.Visibility = Visibility.Visible;
        }
        if (!target.IsDirectory)
        {
            TargetIcon.Text = "\uE8A5";
            CancellationNote.Visibility = Visibility.Collapsed;
        }
        TargetPath.Text = target.Path; TargetPath.ToolTip = target.Path;
        AutomationProperties.SetName(TargetPath, $"Exact {kind} to delete permanently");
        TargetName.Text = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(target.Path));
        TargetName.ToolTip = TargetName.Text;
        TargetSize.Text = target.HasAllocationData
            ? $"{SizeText.Format(target.AllocatedBytes)} on disk"
            : $"{SizeText.Format(target.LogicalBytes)} logical";
        SizeDetails.Text = target.HasAllocationData
            ? $"Scanned allocation: {SizeText.Format(target.AllocatedBytes)} · Logical: {SizeText.Format(target.LogicalBytes)}"
            : $"Scanned logical size: {SizeText.Format(target.LogicalBytes)} · Allocation unknown";
        TargetSize.ToolTip = SizeDetails.Text;
        PartialScanNote.Visibility = target.IsIncomplete ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Delete_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
