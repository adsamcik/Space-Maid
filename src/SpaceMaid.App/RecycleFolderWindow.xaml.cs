using System.Windows;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class RecycleFolderWindow : Window
{
    public RecycleFolderWindow(FolderDeletionTarget target)
    {
        InitializeComponent();
        TargetPath.Text = target.Path; TargetPath.ToolTip = target.Path;
        TargetName.Text = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(target.Path));
        TargetName.ToolTip = TargetName.Text;
        TargetSize.Text = target.HasAllocationData
            ? $"{SizeText.Format(target.AllocatedBytes)} · Scanned"
            : $"{SizeText.Format(target.LogicalBytes)} · Logical";
        SizeDetails.Text = target.HasAllocationData
            ? $"Scanned allocation: {SizeText.Format(target.AllocatedBytes)} · Logical: {SizeText.Format(target.LogicalBytes)}"
            : $"Scanned logical size: {SizeText.Format(target.LogicalBytes)} · Allocation unknown";
        TargetSize.ToolTip = SizeDetails.Text;
        PartialScanNote.Visibility = target.IsIncomplete ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Recycle_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
