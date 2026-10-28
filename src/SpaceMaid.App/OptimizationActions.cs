using System.ComponentModel;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Optimization;

namespace SpaceMaid.App;

internal sealed record OptimizationIntegration(string ScopePath, OptimizationDiscovery Discovery,
    IReadOnlyDictionary<string, string> ProviderNames, ToolCacheService? NativeTools,
    Func<ToolCachePlan, CancellationToken, Task<ToolCacheRunResult>>? ExecuteNative);

internal sealed class OptimizationActionRow : INotifyPropertyChanged
{
    public OptimizationWindow.RuleRow? Rule { get; }
    public OptimizationContribution? Contribution { get; }
    public string Name { get; }
    public string Provider { get; }
    public string Mode { get; }
    public string Description { get; }
    public string Consequence { get; }
    public ToolCacheObservation? Observation { get; set; }
    public bool NeedsInspection { get; set; }
    public string Command { get; set; } = "";
    public string Report { get; set; } = "";
    private string _status;
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal OptimizationActionRow(OptimizationWindow.RuleRow rule, int locations)
    {
        Rule = rule; Name = rule.Name; Provider = rule.Provider; Mode = "Generated files";
        Description = $"Review verified generated files in {locations:N0} scanned {(locations == 1 ? "folder" : "folders")}.";
        Consequence = rule.Regeneration; _status = "Ready to review";
    }
    internal OptimizationActionRow(OptimizationContribution contribution)
    {
        Contribution = contribution; Name = contribution.Action.Name; Provider = contribution.OwnerName;
        Mode = contribution.Action.NativeTool is null ? "Review in extension" : "Native tool · tool-wide scope";
        Description = contribution.Action.Description; Consequence = contribution.Action.Consequence;
        _status = contribution.Action.NativeTool is null ? "" : "Not inspected";
    }
}
