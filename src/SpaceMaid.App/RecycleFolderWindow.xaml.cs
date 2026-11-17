using System.Windows;
using System.Windows.Automation;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class RecycleFolderWindow : Window
{
    public RecycleFolderWindow(FolderDeletionTarget target) : this(target, permanent: false, recycleWarning: null) { }

    internal RecycleFolderWindow(FolderDeletionTarget target, bool permanent, string? recycleWarning)
    {
        InitializeComponent();
        TargetPath.Text = target.Path; TargetPath.ToolTip = target.Path;
        TargetName.Text = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(target.Path));
        TargetName.ToolTip = TargetName.Text;
        TargetSize.Text = target.HasAllocationData
            ? $"{SizeText.Format(target.AllocatedBytes)} · Estimated"
            : $"{SizeText.Format(target.LogicalBytes)} · Estimated logical";
        SizeDetails.Text = target.HasAllocationData
            ? $"Scanned allocation: {SizeText.Format(target.AllocatedBytes)} · Logical: {SizeText.Format(target.LogicalBytes)}"
            : $"Scanned logical size: {SizeText.Format(target.LogicalBytes)} · Allocation unknown";
        TargetSize.ToolTip = SizeDetails.Text;
        PartialScanNote.Visibility = target.IsIncomplete ? Visibility.Visible : Visibility.Collapsed;
        if (permanent)
        {
            Title = "Delete folder permanently";
            Heading.Text = "Delete this folder permanently?";
            const string permanentWarning = "The folder and all its current contents will be deleted. This can’t be undone.";
            Consequence.Text = recycleWarning is null ? permanentWarning : recycleWarning + "\n\n" + permanentWarning;
            Consequence.Opacity = 1;
            CancellationNote.Text = "Stopping a deletion can leave some contents permanently deleted.";
            RecycleButton.Content = "Delete permanently";
            RecycleButton.ClearValue(StyleProperty);
            AutomationProperties.SetName(TargetPath, "Exact folder to delete permanently");
        }
    }
    private void Recycle_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
