using System.Windows;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Extensions.Contracts;
using SpaceMaid.Extensions.Developer.Analysis;

namespace SpaceMaid.Extensions.Developer;

// Contributes to the shared Opportunities view; file operations stay in the host.
public sealed class DeveloperExtension : ISpaceMaidExtension, IOpportunityExtension, IOptimizationExtension
{
    public ExtensionDescriptor Descriptor => new("space-maid.developer", "Developer", "1.0.0");

    public Task InitializeAsync(IExtensionHost host, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public FrameworkElement? CreateView() => null;
    public IReadOnlyList<ExtensionOptimizationAction> GetOptimizationActions() =>
    [
        new("npm-cache", "npm cache", "Inspect npm's configured cache and supported native maintenance.", "npm verifies cache records. Later installs may need downloads.", NativeTool: ToolCacheKind.Npm),
        new("pnpm-store", "pnpm package store", "Inspect pnpm's package store and metadata cache.", "Pruning removes unreferenced packages and metadata. Later installs may need downloads or private-feed access.", NativeTool: ToolCacheKind.Pnpm),
        new("nuget-cache", "NuGet caches", "Find NuGet's configured cache locations and project resources.", "Packages can be required by offline restores. Selective removal is not supported here.", NativeTool: ToolCacheKind.NuGet),
        new("cargo-cache", "Cargo caches", "Inspect Cargo's downloaded and project-managed resources.", "Keep dependencies needed for offline builds. Selective removal is not supported here.", NativeTool: ToolCacheKind.Cargo),
        new("gradle-cache", "Gradle caches", "Inspect Gradle's local cache and project resources.", "Gradle owns cache retention. Selective removal is not supported here.", NativeTool: ToolCacheKind.Gradle)
    ];
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
