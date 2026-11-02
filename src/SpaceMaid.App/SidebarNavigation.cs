using System.Windows;
using System.Windows.Media;

namespace SpaceMaid.App;

// Shared presentation metadata for built-in and contributed navigation items.
public static class SidebarNavigation
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(ImageSource), typeof(SidebarNavigation), new PropertyMetadata(null));
    public static ImageSource? GetIcon(DependencyObject element) => (ImageSource?)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, ImageSource? value) => element.SetValue(IconProperty, value);

    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.RegisterAttached(
        "IsCollapsed", typeof(bool), typeof(SidebarNavigation),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsCollapsed(DependencyObject element) => (bool)element.GetValue(IsCollapsedProperty);
    public static void SetIsCollapsed(DependencyObject element, bool value) => element.SetValue(IsCollapsedProperty, value);
}
