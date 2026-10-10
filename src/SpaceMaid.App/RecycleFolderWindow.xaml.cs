using System.Windows;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class RecycleFolderWindow : Window
{
    public RecycleFolderWindow(FolderDeletionTarget target)
    {
        InitializeComponent();
        TargetPath.Text = target.Path;
        TargetSize.Text = target.HasAllocationData
            ? $"Observed allocated size: {SizeText.Format(target.AllocatedBytes)} · Logical: {SizeText.Format(target.LogicalBytes)}"
            : $"Observed logical size: {SizeText.Format(target.LogicalBytes)} · Physical allocation unavailable";
        CoverageNote.Text = target.IsIncomplete
            ? "The scan is partial. Deletion includes current contents, including files the scan could not read. These sizes are not a promise of space freed."
            : "Sizes come from the scan. Current contents may have changed; shared allocation may remain in use elsewhere.";
    }
    private void Recycle_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
