using System.Windows;

namespace SpaceMaid.App;

public partial class EmptyRecycleBinWindow : Window
{
    internal EmptyRecycleBinWindow(RecycleBinSnapshot bin)
    {
        InitializeComponent();
        BinScope.Text = $"{bin.RootPath} · {bin.Items:N0} {(bin.Items == 1 ? "item" : "items")}";
        BinSize.Text = SizeText.Format(bin.Bytes);
        BinSize.ToolTip = "Windows' current bin size; released disk space may differ.";
    }
    private void Empty_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
