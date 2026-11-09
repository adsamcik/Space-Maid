using System.Windows;

namespace SpaceMaid.App;

public partial class EmptyRecycleBinWindow : Window
{
    internal EmptyRecycleBinWindow(RecycleBinSnapshot bin) : this(bin, null, false) { }

    internal EmptyRecycleBinWindow(RecycleBinSnapshot bin, long? scannedBytes, bool allocated)
    {
        InitializeComponent();
        BinScope.Text = $"{bin.RootPath} · {bin.Items:N0} {(bin.Items == 1 ? "item" : "items")} listed by Windows";
        BinSize.Text = SizeText.Format(bin.Bytes);
        BinSize.ToolTip = "Size reported by Windows. Released disk space may differ.";
        if (scannedBytes is { } bytes)
        {
            ScanSummary.Visibility = Visibility.Visible;
            ScanSizeLabel.Text = allocated ? "On disk at last scan" : "Logical size at last scan";
            ScanSize.Text = SizeText.Format(bytes);
            Explanation.Text = "The scan can also include leftover files and other accounts. Emptying permanently deletes the items Windows lists for your account. You can review leftovers separately.";
        }
    }

    internal EmptyRecycleBinWindow(string root)
    {
        InitializeComponent();
        Title = Heading.Text = "Recycle Bin leftovers found";
        BinScope.Text = root + " · Your Windows account";
        Explanation.Text = "Some files have no matching Recycle Bin record. Review them to recover or permanently delete selected entries.";
        KeepButton.Content = "Keep leftovers";
        EmptyButton.Content = "Review leftovers…";
    }
    private void Empty_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
