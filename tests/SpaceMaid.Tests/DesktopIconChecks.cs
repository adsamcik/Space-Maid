using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpaceMaid.App;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckIconFallback(MainWindow window, string artifactRoot)
    {
        var loading = window.InitializeExplorerAsync(CrowdedMapFixture(), [], default);
        PumpUntil(() => loading.IsCompleted, TimeSpan.FromSeconds(5)); loading.GetAwaiter().GetResult();
        var explore = (RadioButton)window.FindName("ExploreTab");
        explore.IsChecked = true;
        CheckVisibleIcons("explorer");
        ((Button)window.FindName("DrivesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            var picker = (FrameworkElement)window.FindName("DrivePickerPanel");
            PumpUntil(() => ((ItemsControl)window.FindName("DriveChoices")).Items.Count > 0, TimeSpan.FromSeconds(5));
            picker.UpdateLayout();
            CheckFallback(picker, () => RenderMenu(picker, Path.Join(artifactRoot, "artifacts", "ui", "icons-drive-picker-fallback.png")));
        }
        finally { ((System.Windows.Controls.Primitives.Popup)window.FindName("DrivePopup")).IsOpen = false; }

        ((RadioButton)window.FindName("OpportunitiesTab")).IsChecked = true;
        CheckVisibleIcons("cleanup");
        _ = OpportunityControl(window, "CleanupGroupCards");
        CheckVisibleIcons("cleanup-browser");
        explore.IsChecked = true;

        void CheckVisibleIcons(string name)
        {
            Render(window, Path.Join(artifactRoot, "artifacts", "ui", $"icons-{name}.png"), 1020, 790);
            CheckFallback(window, () => Render(window,
                Path.Join(artifactRoot, "artifacts", "ui", $"icons-{name}-fallback.png"), 1020, 790));
        }
    }

    private static void CheckFallback(DependencyObject surface, Action render)
    {
        var owners = Descendants(surface).Prepend(surface).Where(element => element is Control or TextBlock
            && ((FontFamily)element.GetValue(TextBlock.FontFamilyProperty)).Source.StartsWith("Segoe Fluent Icons", StringComparison.Ordinal)
            && DependencyPropertyHelper.GetValueSource(element, TextBlock.FontFamilyProperty).BaseValueSource != BaseValueSource.Inherited).ToArray();
        Assert.NotEmpty(owners);
        var fonts = owners.Select(element => (FontFamily)element.GetValue(TextBlock.FontFamilyProperty)).ToArray();
        var values = owners.Select(element => element.ReadLocalValue(TextBlock.FontFamilyProperty)).ToArray();
        try
        {
            // Leave the configured fallback intact while making the primary font
            // unavailable, without changing the machine's installed fonts.
            for (int i = 0; i < owners.Length; i++)
                owners[i].SetValue(TextBlock.FontFamilyProperty, new FontFamily(fonts[i].Source.Replace(
                    "Segoe Fluent Icons", "Space Maid Unavailable Icon Font", StringComparison.Ordinal)));
            render();
            var icons = Descendants(surface).OfType<TextBlock>().Where(block => block.IsVisible
                && block.Text.Length == 1 && block.Text[0] is >= '\uE000' and <= '\uF8FF'
                && block.FontFamily.Source.StartsWith("Space Maid Unavailable Icon Font", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(icons);
            foreach (var icon in icons)
            {
                var runs = Glyphs(VisualTreeHelper.GetDrawing(icon)).ToArray();
                Assert.NotEmpty(runs);
                Assert.All(runs, run =>
                {
                    Assert.DoesNotContain((ushort)0, run.GlyphIndices);
                    Assert.True(run.GlyphTypeface.FamilyNames.Values.Contains("Segoe MDL2 Assets"),
                        $"U+{(int)icon.Text[0]:X4}, {icon.FontFamily.Source}: {string.Join(", ", run.GlyphTypeface.FamilyNames.Values)}");
                });
            }
        }
        finally
        {
            for (int i = 0; i < owners.Length; i++)
            {
                if (values[i] == DependencyProperty.UnsetValue) owners[i].ClearValue(TextBlock.FontFamilyProperty);
                else owners[i].SetValue(TextBlock.FontFamilyProperty, values[i]);
            }
        }

        static IEnumerable<GlyphRun> Glyphs(Drawing? drawing) => drawing switch
        {
            GlyphRunDrawing glyph => [glyph.GlyphRun],
            DrawingGroup group => group.Children.SelectMany(Glyphs),
            _ => []
        };
    }
}
