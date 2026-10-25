namespace SpaceMaid.Core.Analysis;

public sealed record OpportunityGroup(string Key, string Name, string Path, string Kind,
    IReadOnlyList<Opportunity> Locations, long ObservedBytes, string Summary = "", IReadOnlyList<OpportunityGroup>? Children = null)
{
    public string ProviderId { get; init; } = "";
    public string Caption { get; init; } = "";
    public string Glyph { get; init; } = "\uE8B7";
    public string StatusLabel { get; init; } = "";
    public string BreakdownHeading { get; init; } = "";
    public bool CanSelectGroup { get; init; } = true;
    public OpportunityGroupRole Role { get; init; }
    // Informational breakdown of the same findings, separate from project navigation.
    public IReadOnlyList<OpportunityGroup>? ContentGroups { get; init; }
}

public enum OpportunityGroupRole { General, Worktrees, MatchedWorktrees }

public sealed record OpportunityGroupSet(IReadOnlyList<OpportunityGroup> Projects, IReadOnlyList<OpportunityGroup> Types,
    IReadOnlyList<OpportunityGroup>? BuildLanguages = null,
    IReadOnlyDictionary<string, IReadOnlyList<OpportunityGroup>>? CleanupBreakdowns = null,
    IReadOnlyList<OpportunityGroup>? Cleanup = null);
