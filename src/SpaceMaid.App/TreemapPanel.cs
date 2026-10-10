using System.Windows;
using System.Windows.Controls;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal enum SpaceKind { Other, Project, Cache, Agent }
internal sealed record SpaceContribution(int Link, string Name, long Bytes, SpaceKind Kind, string Path, int GroupedCount = 0,
    IReadOnlyList<SpaceContribution>? Children = null);

internal static class TreemapLayout
{
    public static Rect[] Arrange(IReadOnlyList<long> weights, Size size)
    {
        var rectangles = new Rect[weights.Count];
        if (weights.Count == 0 || size.Width <= 0 || size.Height <= 0) return rectangles;
        Split(0, weights.Count, new(0, 0, size.Width, size.Height));
        return rectangles;

        void Split(int start, int end, Rect bounds)
        {
            if (end - start == 1) { rectangles[start] = bounds; return; }
            double total = 0;
            for (int i = start; i < end; i++) total += weights[i];
            if (total <= 0) return;
            double first = 0;
            int cut = start + 1;
            for (int i = start; i < end - 1; i++) { first += weights[i]; cut = i + 1; if (first >= total / 2) break; }
            double ratio = first / total;
            if (bounds.Width >= bounds.Height)
            {
                double width = bounds.Width * ratio;
                Split(start, cut, new(bounds.X, bounds.Y, width, bounds.Height));
                Split(cut, end, new(bounds.X + width, bounds.Y, bounds.Width - width, bounds.Height));
            }
            else
            {
                double height = bounds.Height * ratio;
                Split(start, cut, new(bounds.X, bounds.Y, bounds.Width, height));
                Split(cut, end, new(bounds.X, bounds.Y + height, bounds.Width, bounds.Height - height));
            }
        }
    }

    public static IReadOnlyList<SpaceContribution> Contributions(ScanSnapshot scan, int folder, int[] children,
        IReadOnlyDictionary<int, SpaceKind> kinds, int maximum = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        var result = new List<SpaceContribution>();
        long total = scan.AttributedBytes(scan.PrimaryLink(folder));
        long observed = 0, grouped = 0;
        int groupedCount = 0;
        foreach (int link in children)
        {
            long bytes = scan.AttributedBytes(link);
            observed = checked(observed + bytes);
            if (bytes <= 0) continue;
            if (result.Count >= maximum - 1 || bytes < total * .005)
            { grouped = checked(grouped + bytes); groupedCount++; continue; }
            var entry = scan.Entry(link);
            result.Add(new(link, entry.Name, bytes, kinds.GetValueOrDefault(entry.FileIndex, kinds.GetValueOrDefault(folder)), scan.EntryPath(link) ?? "Unresolved path"));
        }
        long metadata = Math.Max(0, total - observed);
        if (grouped > 0 || metadata > 0)
            result.Add(new(-2, metadata > 0 ? "Other entries & metadata" : "Other entries",
                checked(grouped + metadata), SpaceKind.Other,
                $"{groupedCount:N0} smaller entries. Individual names are available in the tree."
                    + (metadata > 0 ? $" Includes {SizeText.Format(metadata)} attributed to the folder itself." : ""), groupedCount));
        return result;
    }

    public static bool CanNest(ScanSnapshot scan, SpaceContribution part, long total) =>
        part.Link >= 0 && part.Bytes >= total * .25
        && scan.Entry(part.Link) is { IsDirectory: true, IsPrimaryName: true };

    public static IReadOnlyList<SpaceContribution> Nest(ScanSnapshot scan, IReadOnlyList<SpaceContribution> parts,
        IReadOnlyDictionary<int, int[]> childOrders, IReadOnlyDictionary<int, SpaceKind> kinds)
    {
        var result = parts.ToArray();
        int remaining = 64 - parts.Count;
        int candidates = parts.Count(part => part.Link >= 0 && childOrders.ContainsKey(scan.Entry(part.Link).FileIndex));
        for (int i = 0; i < result.Length && candidates > 0; i++)
        {
            var part = result[i];
            if (part.Link < 0 || !childOrders.TryGetValue(scan.Entry(part.Link).FileIndex, out var children)) continue;
            int limit = remaining / candidates--;
            if (limit < 2) continue;
            var nested = Contributions(scan, scan.Entry(part.Link).FileIndex, children, kinds, limit);
            if (nested.Count == 0) continue;
            result[i] = part with { Children = nested };
            remaining -= nested.Count;
        }
        return result;
    }
}

// At most 64 map blocks across both levels. All small/remaining entries
// remain individually available in the tree.
internal sealed class TreemapPanel : Panel
{
    public IReadOnlyList<long> Weights { get; set; } = [];
    public IEnumerable<Button> Buttons
    {
        get
        {
            foreach (UIElement child in InternalChildren)
            {
                if (child is Button button) yield return button;
                else if (child is Border { Child: Grid grid })
                    foreach (UIElement item in grid.Children)
                    {
                        if (item is Button header) yield return header;
                        else if (item is TreemapPanel nested)
                            foreach (var tile in nested.Buttons) yield return tile;
                    }
            }
        }
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height);
        var rectangles = TreemapLayout.Arrange(Weights, size);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var rect = i < rectangles.Length ? rectangles[i] : new Rect();
            InternalChildren[i].Measure(new(Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - 4)));
        }
        return size;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var rectangles = TreemapLayout.Arrange(Weights, finalSize);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var rect = i < rectangles.Length ? rectangles[i] : Rect.Empty;
            if (rect.IsEmpty) { InternalChildren[i].Arrange(new Rect()); continue; }
            InternalChildren[i].Arrange(new(rect.X + 2, rect.Y + 2, Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - 4)));
            if (InternalChildren[i] is FrameworkElement element && element.Tag is StackPanel text)
            {
                if (element is Button button) button.Padding = rect.Height < 65 || rect.Width < 90 ? new(5) : new(10);
                text.Children[0].Visibility = rect.Height >= 24 && rect.Width >= 24 ? Visibility.Visible : Visibility.Collapsed;
                for (int line = 1; line < text.Children.Count; line++)
                    text.Children[line].Visibility = rect.Height >= (line == 1 ? 60 : 88) && rect.Width >= 90 ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (InternalChildren[i] is Border { Child: Grid grid, Tag: TreemapPanel nested })
            {
                bool show = rect.Width >= 160 && rect.Height >= 140;
                nested.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                grid.RowDefinitions[0].Height = show ? new GridLength(44) : new GridLength(1, GridUnitType.Star);
                grid.RowDefinitions[1].Height = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            }
        }
        return finalSize;
    }
}
