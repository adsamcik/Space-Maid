using SpaceMaid.Core.Analysis;

namespace SpaceMaid.Extensions.Contracts;

// Optional browser capability. Providers remain independent of this workflow.
// Scan ownership, validation, confirmation and file operations stay in the host.
public interface IOpportunityBrowserServices { IOpportunityBrowserHost? OpportunityBrowser { get; } }
public interface IOpportunityBrowserHost
{
    string ProviderStatus { get; }
    IReadOnlyList<OpportunityGroup> BuildCleanupGroups(OpportunityGroupContext context);
    void SelectionChanged();
    void WorktreePolicyChanged();
    Task RefreshRecycleBinAsync();
    Task EmptyRecycleBinAsync();
    Task ReviewSelectionAsync();
    Task CheckWorktreesAsync();
    Task ShowInTreeAsync(Opportunity opportunity);
    void OpenFolder(string path);
    void ShowStatus(string message);
    void SetCaption(string caption);
    bool SaveBuildRetention(string value);
    OpportunityFolderContents FolderContents(Opportunity opportunity);
}
public sealed record OpportunityBrowserState(IReadOnlyList<OpportunityRow> Rows, OpportunityGroupSet Groups,
    string? RootPath, bool HasAllocationData, string CurrentPath, bool Stale,
    IReadOnlyDictionary<string, GitWorktreeAssessment> WorktreeAssessments);
public sealed record OpportunityRecycleBinState(string Title, string Size, string Count, string Hint, bool CanEmpty);
public sealed record OpportunityFolderContent(string Name, string Size, string Detail);
public sealed record OpportunityFolderContents(IReadOnlyList<OpportunityFolderContent> Items, string Note);
public interface IOpportunityBrowserView
{
    OpportunityRow? Detail { get; }
    WorktreeCleanupPolicy WorktreePolicy { get; }
    IReadOnlyList<OpportunityGroup> CleanupGroups { get; }
    string BuildRetention { get; }
    void SetBuildRetention(string value, bool needsRepair);
    void ShowOptions();
    void SetEmptyMessage(string message);
    void Publish(OpportunityBrowserState state, string? detailPath = null, bool preserveScope = false);
    void Reset();
    void SetBusy(bool busy);
    void UpdateContext(string currentPath, bool stale);
    void UpdateSelection();
    void RefreshGroups();
    void UpdateRecycleBin(OpportunityRecycleBinState state);
    bool ShowGroup(string? key, string name);
}
