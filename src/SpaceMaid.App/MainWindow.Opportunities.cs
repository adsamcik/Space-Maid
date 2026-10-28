using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private IOpportunityBrowserView? OpportunityBrowser => OpportunitiesContent?.Content as IOpportunityBrowserView;
    private OpportunityGroupSet _opportunityGroups = new([], []);
    private IReadOnlyList<OpportunityGroup> _cleanupGroups => OpportunityBrowser?.CleanupGroups ?? _opportunityGroups.Cleanup ?? [];
    private readonly Dictionary<string, GitWorktreeAssessment> _worktreeAssessments = new(StringComparer.Ordinal);
    private bool _changingOpportunitySelection;
    private void ResetOpportunityBrowser()
    { _opportunityGroups = new([], []); _worktreeAssessments.Clear(); _worktreesRequiringCheck.Clear(); OpportunityBrowser?.Reset(); }
    private void PublishOpportunities(ScanSnapshot scan, IReadOnlyList<Opportunity> locations, OpportunityGroupSet groups,
        IReadOnlySet<(string Provider, string Path, string Rule)>? selectedLocations = null, string? detailPath = null, bool preserveScope = false)
    {
        _rows.Clear(); _opportunityGroups = groups;
        if (!preserveScope) _worktreesRequiringCheck.Clear();
        else foreach (string path in _worktreesRequiringCheck.Keys.Where(path => selectedLocations is null || !selectedLocations.Any(selected => GitWorktreeGuard.Covers(selected.Path, path))).ToArray())
            _worktreesRequiringCheck.Remove(path);
        _worktreeAssessments.Clear();
        var owners = groups.Projects.SelectMany(group => group.Locations.Select(item => (Identity: (item.ProviderId, item.DirectoryIndex, item.RuleId), Group: group)))
            .DistinctBy(item => item.Identity).ToDictionary(item => item.Identity, item => item.Group);
        foreach (var location in locations)
        {
            var row = new OpportunityRow(location, scan.HasAllocationData, owners.GetValueOrDefault((location.ProviderId, location.DirectoryIndex, location.RuleId)), scan.IsUnmeasuredDirectory(location.DirectoryIndex))
                { Selected = selectedLocations?.Contains((location.ProviderId, location.Path, location.RuleId)) == true };
            _rows.Add(row);
        }
        OpportunityBrowser?.Publish(new(_rows.ToArray(), groups, scan.RootPath, scan.HasAllocationData, _currentPath, _scanStale,
            _worktreeAssessments), detailPath, preserveScope);
    }
    private bool RevealRecommendedGroup(DriveRecommendation recommendation)
    {
        OpportunitiesTab.IsChecked = true;
        if (OpportunityBrowser is { } browser) return browser.ShowGroup(recommendation.GroupKey, recommendation.Name);
        StatusText.Text = "Opportunities unavailable · Check Extensions"; return false;
    }
    private sealed class OpportunityBrowserHost(MainWindow window) : IOpportunityBrowserHost
    {
        public string ProviderStatus => window._snapshot is { } scan ? window.OpportunityExtensions.Status(scan) : "";
        public IReadOnlyList<OpportunityGroup> BuildCleanupGroups(OpportunityGroupContext context)
            => window._snapshot is { } scan && window._extensions is not null
                ? window.OpportunityExtensions.BuildCleanupGroups(scan, window._rows.Select(row => row.Item).ToArray(), window._opportunityGroups, context) : [];
        public void SelectionChanged() { if (!window._changingOpportunitySelection) window.UpdateSelection(); }
        public void WorktreePolicyChanged()
        { foreach (string path in window._worktreesRequiringCheck.Keys.ToArray()) window._worktreesRequiringCheck[path] = window.CurrentWorktreePolicy; }
        public Task RefreshRecycleBinAsync() => window.RefreshRecycleBinAsync();
        public Task EmptyRecycleBinAsync() => window.EmptySelectedRecycleBinAsync();
        public Task ReviewSelectionAsync() => window.ReviewSelectionAsync();
        public async Task CheckWorktreesAsync()
        {
            var task = window.CheckWorktreesAsync(); window._worktreeCheckTask = task;
            try { await task; } finally { window._worktreeCheckTask = null; window.UpdateUpdateButton(); }
        }
        public async Task ShowInTreeAsync(Opportunity opportunity)
        {
            if (window._snapshot is null || window._work is not null || !window._rows.Any(row => ReferenceEquals(row.Item, opportunity))) return;
            window.ExploreTab.IsChecked = true;
            await window.NavigateToEntryAsync(window._snapshot.PrimaryLink(opportunity.DirectoryIndex));
        }
        public void OpenFolder(string path) => window.OpenFolderInExplorer(path);
        public void ShowStatus(string message) => window.StatusText.Text = message;
        public void SetCaption(string caption) => window.OpportunitiesTab.Content = caption;
        public bool SaveBuildRetention(string value) => window.SaveBuildRetention(value);
        public OpportunityFolderContents FolderContents(Opportunity opportunity) => window.ReadBuildFolderContents(opportunity);
    }
}
