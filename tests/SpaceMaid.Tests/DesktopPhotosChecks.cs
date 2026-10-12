using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckPhotos(MainWindow window, Application app, TestDirectory fixture, string artifactRoot, Action<string> setStage)
    {
        setStage("loading the bundled Photos extension into its own assembly context");
        PumpUntil(() => window.ExtensionsReady, TimeSpan.FromSeconds(10));
        var extensionRuntime = Assert.IsType<SpaceMaid.App.Extensions.ExtensionRuntime>(typeof(MainWindow)
            .GetField("_extensions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var developerExtension = Assert.Single(extensionRuntime.Entries, value => value.Id == "space-maid.developer");
        Assert.Equal("Loaded", developerExtension.Status); Assert.Null(developerExtension.View);
        var photosView = Assert.IsAssignableFrom<FrameworkElement>(((ContentControl)window.FindName("PhotosContent")).Content);
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default,
            System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(photosView.GetType().Assembly));
        ((RadioButton)window.FindName("PhotosTab")).IsChecked = true;
        Assert.Equal(Visibility.Visible, ((Grid)window.FindName("PhotosPanel")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("OpportunitiesPanel")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("ExplorePanel")).Visibility);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "photos-empty.png"), 1220, 940);
        Assert.IsType<ComboBox>(photosView.FindName("CollectionList"));
        Assert.Equal(Visibility.Collapsed, ((Border)photosView.FindName("SelectionPanel")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((Grid)photosView.FindName("CollectionHeader")).Visibility);
        Assert.Equal("Add folder", ((Button)photosView.FindName("EmptyActionButton")).Content);
        Assert.NotNull(((Image)photosView.FindName("LibraryIllustration")).Source);
        setStage("discovering and analyzing owned photos through the loaded extension and real tool broker");
        PumpUntil(() => !string.IsNullOrEmpty(((TextBlock)photosView.FindName("ToolText")).Text), TimeSpan.FromSeconds(10));
        string photoFolder = Directory.CreateDirectory(Path.Join(fixture.Path, "photo-review")).FullName;
        string originalPhoto = Path.Join(photoFolder, "original.png"), copyPhoto = Path.Join(photoFolder, "copy.png");
        var sample = BitmapSource.Create(200, 100, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat((byte)120, 200 * 100 * 4).ToArray(), 200 * 4);
        var photoEncoder = new PngBitmapEncoder(); photoEncoder.Frames.Add(BitmapFrame.Create(sample));
        using (var output = File.Create(originalPhoto)) photoEncoder.Save(output);
        File.Copy(originalPhoto, copyPhoto);
        byte[] originalBytes = File.ReadAllBytes(originalPhoto);
        var addPhotos = (Task)photosView.GetType().GetMethod("AddFolderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(photosView, [photoFolder])!;
        PumpUntil(() => addPhotos.IsCompleted, TimeSpan.FromSeconds(10)); addPhotos.GetAwaiter().GetResult();
        var photoFiles = (DataGrid)photosView.FindName("FileList");
        Assert.Equal(2, photoFiles.Items.Count);
        var photoGallery = (ListBox)photosView.FindName("PhotoGallery");
        Assert.Same(photoFiles.ItemsSource, photoGallery.ItemsSource);
        Assert.Equal(Visibility.Visible, photoGallery.Visibility);
        Assert.Equal(Visibility.Collapsed, photoFiles.Visibility);
        photoGallery.SelectedIndex = 0;
        Assert.Same(photoGallery.SelectedItem, photoFiles.SelectedItem);
        Assert.All(Descendants(photoGallery).OfType<Image>(), image => Assert.Null(image.Source));
        ((Button)photosView.FindName("CloseSelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Visible, ((Grid)photosView.FindName("CollectionHeader")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)photosView.FindName("PageButtons")).Visibility);
        Assert.False(((Expander)photosView.FindName("CollectionDetails")).IsExpanded);
        Assert.Equal(Visibility.Collapsed, photoFiles.Columns.Single(column => column.SortMemberPath == "Status").Visibility);
        Assert.Equal(Visibility.Collapsed, photoFiles.Columns.Single(column => column.SortMemberPath == "Type").Visibility);
        var statusColumnChoice = (MenuItem)photosView.FindName("ShowStatusColumn");
        statusColumnChoice.IsChecked = true; statusColumnChoice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(Visibility.Visible, photoFiles.Columns.Single(column => column.SortMemberPath == "Status").Visibility);
        statusColumnChoice.IsChecked = false; statusColumnChoice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ((RadioButton)photosView.FindName("FamiliesTab")).IsChecked = true;
        PumpUntil(() => photoFiles.Items.Count == 0 && ((TextBlock)photosView.FindName("EmptyTitle")).Text == "No related files found", TimeSpan.FromSeconds(3));
        Assert.Equal(Visibility.Collapsed, photoFiles.Visibility);
        Assert.Equal("Browse photos", ((Button)photosView.FindName("EmptyActionButton")).Content);
        photosView.GetType().GetMethod("ShowFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(photosView, [new IOException("Owned fixture retry")]);
        Assert.Equal(Visibility.Visible, ((Grid)photosView.FindName("StatusBand")).Visibility);
        Assert.Equal("Try again", ((Button)photosView.FindName("EmptyActionButton")).Content);
        ((Button)photosView.FindName("EmptyActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((TextBlock)photosView.FindName("EmptyTitle")).Text == "No related files found", TimeSpan.FromSeconds(3));
        Assert.Equal(Visibility.Hidden, ((Grid)photosView.FindName("StatusBand")).Visibility);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "photos-related-empty.png"), 1220, 940);
        ((Button)photosView.FindName("EmptyActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => photoFiles.Items.Count == 2, TimeSpan.FromSeconds(3));
        // The template-based Name column still routes header sorting to
        // catalog paging, with a visible arrow and actual row reordering.
        var nameColumn = photoFiles.Columns.Single(column => column.SortMemberPath == "Name");
        var onSorting = typeof(DataGrid).GetMethod("OnSorting", BindingFlags.Instance | BindingFlags.NonPublic)!;
        onSorting.Invoke(photoFiles, [new DataGridSortingEventArgs(nameColumn)]);
        PumpUntil(() => photoFiles.Items.Count == 2 && PhotoName(photoFiles.Items[0]) == "original.png", TimeSpan.FromSeconds(3));
        Assert.Equal(ListSortDirection.Descending, nameColumn.SortDirection);
        onSorting.Invoke(photoFiles, [new DataGridSortingEventArgs(nameColumn)]);
        PumpUntil(() => photoFiles.Items.Count == 2 && PhotoName(photoFiles.Items[0]) == "copy.png", TimeSpan.FromSeconds(3));
#pragma warning disable WPF0001
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"photos-browse-{(theme == ThemeMode.Light ? "light" : "dark")}.png"), 1220, 940);
        }
#pragma warning restore WPF0001
        photoFiles.SelectedIndex = 0; window.UpdateLayout();
        Assert.Equal(Visibility.Visible, ((Border)photosView.FindName("SelectionPanel")).Visibility);
        Assert.Null(((Image)photosView.FindName("SelectedPreview")).Source);
        Assert.Equal(Visibility.Hidden, ((Image)photosView.FindName("SelectedPreview")).Visibility);
        Assert.Contains("Analyze", ((TextBlock)photosView.FindName("PreviewStatusText")).Text);
        ((Button)photosView.FindName("CloseSelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(photoFiles.SelectedItem); Assert.Equal(Visibility.Collapsed, ((Border)photosView.FindName("SelectionPanel")).Visibility);
        ((RadioButton)photosView.FindName("DuplicatesTab")).IsChecked = true;
        PumpUntil(() => photoFiles.Items.Count == 0 && Equals(((Button)photosView.FindName("EmptyActionButton")).Content, "Analyze photos"), TimeSpan.FromSeconds(3));
        var analyzePhotos = (Button)photosView.FindName("AnalyzeButton");
        ((Button)photosView.FindName("EmptyActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => analyzePhotos.IsEnabled && ((TextBlock)photosView.FindName("StatusText")).Text.Contains("Analysis complete", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        ((RadioButton)photosView.FindName("DuplicatesTab")).IsChecked = true;
        PumpUntil(() => photoFiles.Items.Count == 2 && ((TextBlock)photosView.FindName("CoverageText")).Text.Contains("Default-stream equality", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        photoFiles.SelectedIndex = 0;
        PumpUntil(() => ((Image)photosView.FindName("SelectedPreview")).Source is not null, TimeSpan.FromSeconds(10));
        var photoPreview = Assert.IsAssignableFrom<BitmapSource>(((Image)photosView.FindName("SelectedPreview")).Source);
        Assert.InRange(photoPreview.PixelWidth, 1, 160); Assert.InRange(photoPreview.PixelHeight, 1, 160);
        Assert.Contains("PNG", ((TextBox)photosView.FindName("SelectionText")).Text);
        setStage("sorting the gallery and switching views without losing selection or previews");
        var gallerySelection = photoGallery.SelectedItem;
        ((Button)photosView.FindName("SortDirectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => PhotoName(photoGallery.Items[0]) == "original.png", TimeSpan.FromSeconds(3));
        Assert.Same(gallerySelection, photoGallery.SelectedItem);
        ((Button)photosView.FindName("SortDirectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => PhotoName(photoGallery.Items[0]) == "copy.png", TimeSpan.FromSeconds(3));
        ((RadioButton)photosView.FindName("ListButton")).IsChecked = true; window.UpdateLayout();
        Assert.Equal(Visibility.Visible, photoFiles.Visibility); Assert.Equal(Visibility.Collapsed, photoGallery.Visibility);
        Assert.Same(gallerySelection, photoFiles.SelectedItem);
        ((RadioButton)photosView.FindName("GalleryButton")).IsChecked = true; window.UpdateLayout();
        Assert.Same(gallerySelection, photoGallery.SelectedItem);
        Assert.Same(photoPreview, ((Image)photosView.FindName("SelectedPreview")).Source);
        setStage("refreshing the saved photo page without rebuilding selection or previews");
        var photoPicker = (ComboBox)photosView.FindName("CollectionList");
        var pickerSource = photoPicker.ItemsSource; var pickerSelection = photoPicker.SelectedItem;
        var photoSource = photoFiles.ItemsSource; var photoSelection = photoFiles.SelectedItem;
        var rowContainer = photoFiles.ItemContainerGenerator.ContainerFromItem(photoSelection);
        var galleryContainer = photoGallery.ItemContainerGenerator.ContainerFromItem(photoSelection);
        var changedSelection = 0; photoFiles.SelectionChanged += (_, _) => changedSelection++;
        var collectionChanges = 0;
        ((System.Collections.Specialized.INotifyCollectionChanged)photoSource).CollectionChanged += (_, _) => collectionChanges++;
        var reloadPhotos = photosView.GetType().GetMethod("LoadCollectionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reload = (Task)reloadPhotos.Invoke(photosView, [null])!;
        PumpUntil(() => reload.IsCompleted, TimeSpan.FromSeconds(3)); reload.GetAwaiter().GetResult(); window.UpdateLayout();
        Assert.Same(pickerSource, photoPicker.ItemsSource); Assert.Same(pickerSelection, photoPicker.SelectedItem);
        Assert.Same(photoSource, photoFiles.ItemsSource); Assert.Same(photoSelection, photoFiles.SelectedItem);
        Assert.Same(rowContainer, photoFiles.ItemContainerGenerator.ContainerFromItem(photoSelection));
        Assert.NotNull(galleryContainer); Assert.Same(galleryContainer, photoGallery.ItemContainerGenerator.ContainerFromItem(photoSelection));
        Assert.Same(photoPreview, ((Image)photosView.FindName("SelectedPreview")).Source);
        Assert.Equal(0, changedSelection); Assert.Equal(0, collectionChanges);
        onSorting.Invoke(photoFiles, [new DataGridSortingEventArgs(nameColumn)]);
        PumpUntil(() => PhotoName(photoFiles.Items[0]) == "original.png", TimeSpan.FromSeconds(3));
        Assert.Same(photoSelection, photoFiles.SelectedItem);
        Assert.Same(photoPreview, ((Image)photosView.FindName("SelectedPreview")).Source);
        onSorting.Invoke(photoFiles, [new DataGridSortingEventArgs(nameColumn)]);
        PumpUntil(() => PhotoName(photoFiles.Items[0]) == "copy.png", TimeSpan.FromSeconds(3));
        // Simulate a page query beating the discovery worker's catalog
        // revocation. Discovery must never republish old analysis.
        ((RadioButton)photosView.FindName("BrowseTab")).IsChecked = true;
        PumpUntil(() => photoFiles.Columns[0].Visibility == Visibility.Collapsed && photoFiles.Items.Count == 2, TimeSpan.FromSeconds(3));
        var discoveringField = photosView.GetType().GetField("_discovering", BindingFlags.Instance | BindingFlags.NonPublic)!;
        discoveringField.SetValue(photosView, true);
        try
        {
            reload = (Task)reloadPhotos.Invoke(photosView, [null])!;
            PumpUntil(() => reload.IsCompleted, TimeSpan.FromSeconds(3)); reload.GetAwaiter().GetResult();
            Assert.Same(photoSelection, photoFiles.SelectedItem);
            Assert.Null(((Image)photosView.FindName("SelectedPreview")).Source);
            Assert.DoesNotContain("SHA-256", ((TextBox)photosView.FindName("SelectionText")).Text);
            Assert.DoesNotContain("Default stream verified", ((TextBox)photosView.FindName("SelectionText")).Text);
            Assert.Contains("Preliminary", ((TextBlock)photosView.FindName("FreshnessText")).Text);
        }
        finally { discoveringField.SetValue(photosView, false); }
        ((RadioButton)photosView.FindName("DuplicatesTab")).IsChecked = true;
        PumpUntil(() => photoFiles.Items.Count == 2 && ((Image)photosView.FindName("SelectedPreview")).Source is not null, TimeSpan.FromSeconds(3));
        ((Button)photosView.FindName("RetainButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("Review preference", ((TextBox)photosView.FindName("SelectionText")).Text);
        Assert.True(((Expander)photosView.FindName("FileDetails")).IsExpanded);
        Assert.Equal(originalBytes, File.ReadAllBytes(originalPhoto)); Assert.Equal(originalBytes, File.ReadAllBytes(copyPhoto));
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "photos-duplicates.png"), 1220, 940);
        Render(window, Path.Join(artifactRoot, "artifacts", "ui", "photos-duplicates-compact.png"), 1020, 820);
        File.Delete(copyPhoto); // Only the owned fixture; refresh must invalidate earlier evidence.
        ((Button)photosView.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((Button)photosView.FindName("RefreshButton")).IsEnabled && photoFiles.Items.Count == 0, TimeSpan.FromSeconds(10));
        ((RadioButton)photosView.FindName("BrowseTab")).IsChecked = true;
        PumpUntil(() => photoFiles.Items.Count == 1, TimeSpan.FromSeconds(10));
        photoFiles.SelectedIndex = 0;
        Assert.Null(((Image)photosView.FindName("SelectedPreview")).Source);
        Assert.Equal(originalBytes, File.ReadAllBytes(originalPhoto));

        setStage("preserving the photo viewport across metadata refresh and a removed earlier row");
        // Metadata-only fixtures expand one bounded page without adding
        // image decoding or ExifTool work to the existing Application.
        for (var i = 0; i < 104; i++) File.WriteAllBytes(Path.Join(photoFolder, $"z-{i:D3}.jpg"), [1]);
        var photosRefresh = (Button)photosView.FindName("RefreshButton");
        photosRefresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => photosRefresh.IsEnabled && photoGallery.Items.Count == 100, TimeSpan.FromSeconds(3)); window.UpdateLayout();
        var galleryScroll = Descendants(photoGallery).OfType<ScrollViewer>().First();
        galleryScroll.ScrollToVerticalOffset(250); window.UpdateLayout();
        PumpUntil(() => galleryScroll.VerticalOffset > 0, TimeSpan.FromSeconds(2));
        var galleryOffset = galleryScroll.VerticalOffset;
        var galleryReload = (Task)reloadPhotos.Invoke(photosView, [null])!;
        PumpUntil(() => galleryReload.IsCompleted, TimeSpan.FromSeconds(3)); galleryReload.GetAwaiter().GetResult(); window.UpdateLayout();
        Assert.Equal(galleryOffset, galleryScroll.VerticalOffset, 2);
#pragma warning disable WPF0001
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            app.ThemeMode = theme;
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"photos-gallery-page-{(theme == ThemeMode.Light ? "light" : "dark")}.png"), 1220, 940);
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"photos-gallery-page-compact-{(theme == ThemeMode.Light ? "light" : "dark")}.png"), 1020, 820);
        }
#pragma warning restore WPF0001
        ((RadioButton)photosView.FindName("ListButton")).IsChecked = true; window.UpdateLayout();
        var listTop = photoFiles.TranslatePoint(new Point(), photosView).Y;
        photosRefresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        Assert.Equal(listTop, photoFiles.TranslatePoint(new Point(), photosView).Y, 2);
        PumpUntil(() => photosRefresh.IsEnabled && photoFiles.Items.Count == 100, TimeSpan.FromSeconds(3)); window.UpdateLayout();
        Assert.Same(photoSource, photoFiles.ItemsSource); Assert.Same(pickerSelection, photoPicker.SelectedItem);
        Assert.Equal(listTop, photoFiles.TranslatePoint(new Point(), photosView).Y, 2);
        photoFiles.SelectedIndex = 70; photoSelection = photoFiles.SelectedItem;
        photoFiles.Columns.Single(column => column.SortMemberPath == "Status").Visibility = Visibility.Visible;
        photoFiles.Columns.Single(column => column.SortMemberPath == "Type").Visibility = Visibility.Visible;
        nameColumn.Width = 400; // Ensure a real horizontal viewport to preserve.
        photoFiles.ScrollIntoView(photoSelection); window.UpdateLayout();
        var photoScroll = Descendants(photoFiles).OfType<ScrollViewer>().First();
        photoScroll.ScrollToHorizontalOffset(60); window.UpdateLayout();
        PumpUntil(() => photoScroll.VerticalOffset > 0 && photoScroll.HorizontalOffset > 0, TimeSpan.FromSeconds(2));
        var topName = PhotoName(photoFiles.Items[(int)photoScroll.VerticalOffset]);
        var vertical = photoScroll.VerticalOffset; var horizontal = photoScroll.HorizontalOffset;
        reload = (Task)reloadPhotos.Invoke(photosView, [null])!;
        PumpUntil(() => reload.IsCompleted, TimeSpan.FromSeconds(3)); reload.GetAwaiter().GetResult(); window.UpdateLayout();
        Assert.Same(photoSelection, photoFiles.SelectedItem);
        Assert.Equal(vertical, photoScroll.VerticalOffset, 2); Assert.Equal(horizontal, photoScroll.HorizontalOffset, 2);
        File.Delete(Path.Join(photoFolder, "z-000.jpg"));
        photosRefresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => photosRefresh.IsEnabled, TimeSpan.FromSeconds(3)); window.UpdateLayout();
        PumpUntil(() => PhotoName(photoFiles.Items[(int)photoScroll.VerticalOffset]) == topName, TimeSpan.FromSeconds(2));
        Assert.Same(photoSelection, photoFiles.SelectedItem);
        Assert.Same(photoSource, photoFiles.ItemsSource); Assert.Equal(horizontal, photoScroll.HorizontalOffset, 2);
        Assert.Equal(ListSortDirection.Ascending, nameColumn.SortDirection);
        Assert.Equal(listTop, photoFiles.TranslatePoint(new Point(), photosView).Y, 2);
        ((RadioButton)window.FindName("ExploreTab")).IsChecked = true;

        static string PhotoName(object display)
        {
            var value = display.GetType().GetProperty("Row")!.GetValue(display)!;
            return (string)value.GetType().GetProperty("Name")!.GetValue(value)!;
        }
    }
}
