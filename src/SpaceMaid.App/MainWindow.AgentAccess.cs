using System.Windows;
using System.Windows.Controls;
using SpaceMaid.App.AgentAccess;

namespace SpaceMaid.App;

public partial class MainWindow
{
    private AgentAccessWindow? _agentAccessWindow;
    private MenuItem? _agentAccessMenu;

    private void AddAgentAccessMenu()
    {
        if (_agentAccessMenu is null)
        {
            _agentAccessMenu = new MenuItem { Header = "Agent access…" };
            _agentAccessMenu.Click += (_, _) => OpenAgentAccess();
            MoreButton.ContextMenu.Items.Insert(0, _agentAccessMenu);
        }
        _agentAccessMenu.IsEnabled = !Elevation.IsAdministrator && !_closing;
    }

    internal void OpenAgentAccess()
    {
        if (_closing || Elevation.IsAdministrator) return;
        if (_agentAccessWindow is not null) { _agentAccessWindow.Activate(); return; }
        _agentAccessWindow = new AgentAccessWindow(_currentPath, CreateAgentSessionAsync, ReviewAgentSuggestionAsync) { Owner = this };
        _agentAccessWindow.Closed += (_, _) => _agentAccessWindow = null;
        _agentAccessWindow.Show();
    }

    private async Task<AgentAggregateSession> CreateAgentSessionAsync(string root, IReadOnlyList<string> exclusions,
        AgentAccessLevel level, CancellationToken token)
    {
        if (_closing || _work is not null || _scanStale || _snapshot is not { } scan)
            throw new InvalidOperationException("Finish the current work and scan this folder before sharing.");
        var findings = _rows.Select(row => row.Item).ToArray();
        bool Current() => !_closing && _work is null && !_scanStale && ReferenceEquals(_snapshot, scan);
        bool classificationsComplete = _extensions is not null && OpportunityExtensions.Status(scan).Length == 0;
        var session = await Task.Run(() => AgentAggregateSession.CreateAsync(scan, findings, root, exclusions,
            level, classificationsComplete, Current, token), token);
        session.EnsureCurrent();
        return session;
    }

    private async Task ReviewAgentSuggestionAsync(AgentAggregateSession session)
    {
        if (_closing || _work is not null || _scanStale || _snapshot is not { } scan)
            throw new InvalidOperationException("Finish the current work and refresh the scan before reviewing.");
        var items = session.TakeReview();
        var identities = items.Select(item => (item.ProviderId, item.Path, item.RuleId)).ToHashSet();
        if (items.Any(item => !_rows.Any(row => row.SelectionIdentity == (item.ProviderId, item.Path, item.RuleId))))
            throw new InvalidOperationException("Cleanup findings changed. Open a new session.");
        await ReviewKnownPathsAsync(scan, _rows.Where(row => identities.Contains(row.SelectionIdentity))
            .Select(row => row.Item.Path).ToHashSet(StringComparer.Ordinal), CancellationToken.None);
    }
}
