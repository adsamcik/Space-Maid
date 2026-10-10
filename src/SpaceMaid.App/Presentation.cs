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

internal sealed class OpportunityRow(Opportunity opportunity, bool hasAllocation, OpportunityGroup? group = null, bool unmeasured = false) : INotifyPropertyChanged
{
    private bool _selected;
    private string? _scope;
    public Opportunity Item { get; } = opportunity;
    public string Title => Item.Title;
    public string Category => Item.Category;
    public string Path => Item.Path;
    public string Size => unmeasured ? "Not checked" : SizeText.Format(Item.DisplayBytes(hasAllocation));
    public bool IsUnmeasured => unmeasured;
    public long Bytes => Item.DisplayBytes(hasAllocation);
    public string GroupName => group?.Name ?? "";
    public string GroupKey => group?.Key ?? "";
    public string ScopeLabel => _scope is null ? GroupName : "";
    public string DisplayPath => _scope is null ? Path : Path.Equals(_scope, StringComparison.Ordinal) ? "(group root)"
        : Path.StartsWith(_scope.EndsWith('\\') ? _scope : _scope + "\\", StringComparison.Ordinal)
            ? Path[(_scope.EndsWith('\\') ? _scope.Length : _scope.Length + 1)..] : Path;
    public void SetScope(string? scope)
    { if (_scope == scope) return; _scope = scope; PropertyChanged?.Invoke(this, new(nameof(DisplayPath))); PropertyChanged?.Invoke(this, new(nameof(ScopeLabel))); }
    public string Review => Item.RequiresManualReview ? "Review carefully" : "Usually rebuildable";
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class OpportunityGroupRow(OpportunityGroup group, IReadOnlyList<OpportunityRow> matches) : INotifyPropertyChanged
{
    private int _selected;
    public OpportunityGroup Group { get; } = group;
    public IReadOnlyList<OpportunityRow> Matches { get; } = matches;
    public string Name => Group.Name;
    public string Path => Group.Path;
    public string Size => Matches.Any(row => row.IsUnmeasured)
        ? Group.ObservedBytes == 0 ? "Not checked" : SizeText.Format(Group.ObservedBytes) + " +"
        : SizeText.Format(Group.ObservedBytes);
    public string Summary => Group.Summary;
    public string Kind => Group.Kind;
    public bool CanSelectGroup => Group.Key != "cleanup:worktrees";
    public string SelectionHint => CanSelectGroup ? "Select this group for fresh validation and cleanup review."
        : "Use Check worktrees to find old, clean, integrated checkouts, or View folders to review other checkouts individually.";
    public bool? Checked => _selected == 0 ? false : _selected == Matches.Count ? true : null;
    public string Count => (Matches.Count == Group.Locations.Count ? $"{Matches.Count:N0} locations" : $"{Matches.Count:N0} of {Group.Locations.Count:N0} locations")
        + (_selected > 0 ? $" · {_selected:N0} selected" : "");
    public string Description => $"{Name}, {Path}, {Size} observed candidate size. {Count}. {Group.Kind}.";
    public void SetSelectedCount(int count)
    { if (_selected == count) return; _selected = count; PropertyChanged?.Invoke(this, new(nameof(Count))); PropertyChanged?.Invoke(this, new(nameof(Description))); PropertyChanged?.Invoke(this, new(nameof(Checked))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed record DriveChoice(string Path, string Label, long TotalBytes, long FreeBytes)
{
    public override string ToString() => $"{Label}  ·  {SizeText.Format(TotalBytes)} total";
}
