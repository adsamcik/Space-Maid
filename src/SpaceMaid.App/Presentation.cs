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
    public (string Provider, int Directory, string Rule) Identity => (Item.ProviderId, Item.DirectoryIndex, Item.RuleId);
    public (string Provider, string Path, string Rule) SelectionIdentity => (Item.ProviderId, Item.Path, Item.RuleId);
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
    public string Review => Item.CleanupBlockReason.Length > 0 ? "Manage in owning tool"
        : Item.RequiresManualReview ? "Review carefully" : "Usually rebuildable";
    public bool CanSelect => Item.CleanupBlockReason.Length == 0;
    public bool Selected
    {
        get => _selected;
        set { value &= CanSelect; if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class OpportunityGroupRow(OpportunityGroup group, IReadOnlyList<OpportunityRow> matches, bool showChildren = false, long comparisonBytes = 0) : INotifyPropertyChanged
{
    private int _selected;
    private readonly int _selectableCount = matches.Count(row => row.CanSelect);
    public OpportunityGroup Group { get; } = group;
    public IReadOnlyList<OpportunityRow> Matches { get; } = matches;
    public string Name => Group.Name;
    public string Path => Group.Path;
    public string Size => Matches.Any(row => row.IsUnmeasured)
        ? Group.ObservedBytes == 0 ? "Not checked" : SizeText.Format(Group.ObservedBytes) + " +"
        : SizeText.Format(Group.ObservedBytes);
    public string Summary => Group.Caption.Length > 0 ? Group.Caption : Group.Summary;
    public string Kind => Group.Kind;
    public double RelativeSize => comparisonBytes > 0 ? Math.Clamp(100d * Group.ObservedBytes / comparisonBytes, 0, 100) : 0;
    public string SizeComparison => $"{Size} · Compared with the largest visible group. Groups may overlap.";
    public string Glyph => Group.Glyph;
    public string DecisionHint => Group.StatusLabel.Length > 0 ? Group.StatusLabel : Group.Kind;
    public IReadOnlyList<OpportunityGroupRow> Children { get; } = showChildren ? CreateChildren(group, matches) : [];
    public bool HasChildren => Children.Count > 0;
    public string BreakdownHeading => Group.BreakdownHeading;
    public string ProjectCount => $"{Group.Children?.Count ?? 0:N0} {(Group.Children?.Count == 1 ? "project" : "projects")}";
    public bool CanSelectGroup => Group.CanSelectGroup && _selectableCount > 0;
    public string SelectionHint => CanSelectGroup ? "Check and review this group."
        : Group.Summary;
    public bool? Checked => _selected == 0 ? false : _selected == _selectableCount ? true : null;
    public string Count => $"{Matches.Count:N0}" + (Matches.Count == Group.Locations.Count ? "" : $" of {Group.Locations.Count:N0}")
        + $" {(Group.Locations.Count == 1 ? "location" : "locations")}"
        + (_selected > 0 ? $" · {_selected:N0} selected" : "");
    public string Description => $"{Name}, {Path}, {Size} observed candidate size. {Count}. {Group.Kind}. {Group.Summary}";
    public void SetSelectedCount(int count)
    {
        foreach (var child in Children) child.SetSelectedCount(child.Matches.Count(row => row.Selected));
        if (_selected == count) return;
        _selected = count; PropertyChanged?.Invoke(this, new(nameof(Count))); PropertyChanged?.Invoke(this, new(nameof(Description))); PropertyChanged?.Invoke(this, new(nameof(Checked)));
    }
    private static IReadOnlyList<OpportunityGroupRow> CreateChildren(OpportunityGroup group, IReadOnlyList<OpportunityRow> matches)
    {
        var identities = matches.Select(row => row.Identity).ToHashSet();
        var children = (group.Children ?? []).Where(child => child.Locations.Any(item => identities.Contains((item.ProviderId, item.DirectoryIndex, item.RuleId)))).ToArray();
        long largest = children.Select(child => child.ObservedBytes).DefaultIfEmpty().Max();
        return children.Select(child =>
        {
            var members = child.Locations.Select(item => (item.ProviderId, item.DirectoryIndex, item.RuleId)).ToHashSet();
            return new OpportunityGroupRow(child, matches.Where(row => members.Contains(row.Identity)).ToArray(), comparisonBytes: largest);
        }).Where(child => child.Matches.Count > 0).OrderByDescending(child => child.Group.ObservedBytes).ThenBy(child => child.Name).ToArray();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
