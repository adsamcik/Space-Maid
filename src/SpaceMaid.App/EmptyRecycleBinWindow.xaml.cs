using System.Windows;
using SpaceMaid.Core.IO;

namespace SpaceMaid.App;

public partial class EmptyRecycleBinWindow : Window
{
    private readonly long _listedItems;
    private readonly int _eligibleLeftovers;
    internal bool DeleteLeftovers => _eligibleLeftovers > 0 && IncludeLeftovers.IsChecked == true;

    internal EmptyRecycleBinWindow(RecycleBinSnapshot bin) : this(bin, null, false) { }

    internal EmptyRecycleBinWindow(RecycleBinSnapshot bin, long? scannedBytes, bool allocated)
        : this(bin, scannedBytes, allocated, null, null) { }

    internal EmptyRecycleBinWindow(RecycleBinSnapshot bin, long? scannedBytes, bool allocated,
        DiskAccess.RecycleLeftoverReview? leftovers, string? leftoverNotice)
    {
        InitializeComponent();
        _listedItems = bin.Items;
        AccountScope.Text = bin.RootPath + " · Your Windows account";
        BinScope.Text = $"{bin.Items:N0} {(bin.Items == 1 ? "item" : "items")} listed by Windows";
        BinSize.Text = SizeText.Format(bin.Bytes);
        BinSize.ToolTip = "Size reported by Windows. Released disk space may differ.";
        var eligible = leftovers?.Entries.Where(entry => entry.CanDelete).ToArray() ?? [];
        _eligibleLeftovers = eligible.Length;
        if (eligible.Length > 0)
        {
            LeftoverSummary.Visibility = Visibility.Visible;
            LeftoverSize.Text = $"Include {eligible.Length:N0} untracked {(eligible.Length == 1 ? "entry" : "entries")} · {SizeText.Format(eligible.Sum(entry => entry.AllocatedBytes ?? 0))} on disk";
        }
        int kept = (leftovers?.Entries.Count ?? 0) - eligible.Length;
        if (kept > 0) leftoverNotice = $"{kept:N0} untracked {(kept == 1 ? "entry needs" : "entries need")} attention and will stay in the bin. Recent changes require 24 hours; protected or unchecked files are kept.";
        if (leftoverNotice is not null)
        {
            LeftoverNotice.Text = leftoverNotice;
            LeftoverNotice.Visibility = Visibility.Visible;
        }
        if (scannedBytes is { } bytes)
        {
            ScanSummary.Visibility = Visibility.Visible;
            ScanSizeLabel.Text = allocated ? "Last scan on disk · all accounts" : "Last scan logical size · all accounts";
            ScanSize.Text = SizeText.Format(bytes);
            ScanSummary.ToolTip = "The last scan may include other accounts and older data. It is not the amount being deleted.";
        }
        if (_listedItems == 0 && _eligibleLeftovers == 0)
        {
            Heading.Text = "Nothing ready to delete";
            Explanation.Text = "Use Review leftovers in Cleanup to inspect or recover untracked files.";
            KeepButton.Content = "Close";
        }
        UpdateAction();
    }

    private void Selection_Changed(object sender, RoutedEventArgs e) => UpdateAction();
    private void UpdateAction() { if (EmptyButton is not null) EmptyButton.IsEnabled = _listedItems > 0 || DeleteLeftovers; }
    private void Empty_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
