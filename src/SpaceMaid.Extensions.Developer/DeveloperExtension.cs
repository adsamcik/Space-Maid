using System.Windows;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Extensions.Contracts;
using SpaceMaid.Extensions.Developer.Analysis;

namespace SpaceMaid.Extensions.Developer;

// Contributes to the shared Opportunities view; file operations stay in the host.
public sealed class DeveloperExtension : ISpaceMaidExtension, IOpportunityExtension
{
    public ExtensionDescriptor Descriptor => new("space-maid.developer", "Developer", "1.0.0");

    public Task InitializeAsync(IExtensionHost host, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public FrameworkElement? CreateView() => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public OpportunityAnalysis Analyze(IOpportunityInventory inventory, CancellationToken token)
    {
        var findings = OpportunityDetector.Find(inventory, token);
        var groups = OpportunityGrouping.Build(inventory, findings, token);
        var analysis = new OpportunityAnalysis(findings, groups);
        return analysis with
        {
            Groups = groups with
            {
                Cleanup = BuildCleanupGroups(inventory, analysis,
                    new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), WorktreeCleanupPolicy.Default), token)
            }
        };
    }

    public IReadOnlyList<OpportunityGroup> BuildCleanupGroups(IOpportunityInventory inventory,
        OpportunityAnalysis analysis, OpportunityGroupContext context, CancellationToken token)
    {
        var groups = CleanupGrouping.Build(inventory, analysis.Findings,
            context.MatchedWorktreePaths, token, context.WorktreePolicy);
        return groups.Select(group => group with
        {
            Children = group.Key == "cleanup:build" ? analysis.Groups.BuildLanguages
                : analysis.Groups.CleanupBreakdowns?.GetValueOrDefault(group.Key)
        }).ToArray();
    }
}
