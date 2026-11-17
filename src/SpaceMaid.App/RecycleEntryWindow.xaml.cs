using System.Windows;
using System.Windows.Automation;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class RecycleEntryWindow : Window
{
    public RecycleEntryWindow(DeletionTarget target) : this(target, permanent: false, recycleWarning: null) { }

    internal RecycleEntryWindow(DeletionTarget target, bool permanent, string? recycleWarning)
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
        string kind = target.IsDirectory ? "folder" : "file";
        if (!target.IsDirectory)
        {
            TargetIcon.Text = "\uE8A5";
            Title = "Recycle file";
            Consequence.Text = "Moves the file to the Recycle Bin. Empty the bin to free space.";
            CancellationNote.Text = "You can restore the file from the Recycle Bin.";
            AutomationProperties.SetName(TargetPath, "Exact file to recycle");
        }
        if (permanent)
        {
            Title = $"Delete {kind} permanently";
            Heading.Text = $"Delete this {kind} permanently?";
            string permanentWarning = target.IsDirectory
                ? "The folder and all its current contents will be deleted. This can’t be undone."
                : "The file will be deleted. This can’t be undone.";
            Consequence.Text = recycleWarning is null ? permanentWarning : recycleWarning + "\n\n" + permanentWarning;
            Consequence.Opacity = 1;
            if (target.IsDirectory) CancellationNote.Text = "Stopping a deletion can leave some contents permanently deleted.";
            else CancellationNote.Visibility = Visibility.Collapsed;
            RecycleButton.Content = "Delete permanently";
            RecycleButton.ClearValue(StyleProperty);
            AutomationProperties.SetName(TargetPath, $"Exact {kind} to delete permanently");
        }
    }
    private void Recycle_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
