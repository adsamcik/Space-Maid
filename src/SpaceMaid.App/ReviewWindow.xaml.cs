using System.Windows;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

public partial class ReviewWindow : Window
{
    public ReviewWindow(CleanupPreview preview, string? validationNote = null)
    {
        InitializeComponent();
        ValidationText.Text = validationNote;
        ValidationText.Visibility = string.IsNullOrEmpty(validationNote) ? Visibility.Collapsed : Visibility.Visible;
        EstimateText.Text = SizeText.Format(preview.EstimatedBytes);
        EstimateCaption.Text = $"{(preview.UsesLogicalSize ? "Logical size; physical saving unknown" : "Estimated candidate allocation")}" +
            $" · {preview.Locations.Count:N0} {(preview.Locations.Count == 1 ? "location" : "locations")}"
            + $" · {preview.FileCount:N0} file {(preview.FileCount == 1 ? "object" : "objects")}";
        if (preview.ExcludedSharedBytes > 0)
            EstimateCaption.Text += $" · {SizeText.Format(preview.ExcludedSharedBytes)} shared allocation excluded";
        CautionText.Text = string.Join("\n\n", preview.Cautions);
        LocationList.ItemsSource = preview.Locations;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
