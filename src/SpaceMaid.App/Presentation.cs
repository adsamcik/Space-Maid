using System.ComponentModel;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

internal static class SizeText
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}

internal sealed class OpportunityRow(Opportunity opportunity, bool hasAllocation) : INotifyPropertyChanged
{
    private bool _selected;
    public Opportunity Item { get; } = opportunity;
    public string Title => Item.Title;
    public string Category => Item.Category;
    public string Path => Item.Path;
    public string Size => SizeText.Format(Item.DisplayBytes(hasAllocation));
    public string Review => Item.RequiresManualReview ? "Review carefully" : "Usually rebuildable";
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed record DriveChoice(string Path, string Label, long TotalBytes, long FreeBytes)
{
    public override string ToString() => Label;
}
