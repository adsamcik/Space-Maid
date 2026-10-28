using System.Windows;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Optimization;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OptimizationCatalogTests
{
    [Fact]
    public void ContributionsKeepTheirOwnerAndRejectAnInvalidProviderAtomically()
    {
        var native = new ExtensionOptimizationAction("npm", "npm cache", "Inspect the configured cache.", "Later installs may need downloads.", NativeTool: ToolCacheKind.Npm);
        var route = new ExtensionOptimizationAction("models", "Downloaded models", "Review local models.", "Deleted models need downloading.", Workflow: "ollama");
        var result = OptimizationCatalog.Discover([new Provider("valid", [native, route]),
            new Provider("invalid", [native, native]), new Provider("ambiguous", [native with { Workflow = "other" }])]);
        Assert.Equal(2, result.Actions.Count); Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Actions, action => Assert.Equal("valid", action.OwnerId));
        Assert.Equal("Provider valid", result.Actions[0].OwnerName);
        Assert.Equal(ToolCacheKind.Npm, result.Actions[0].Action.NativeTool);
        Assert.Equal("ollama", result.Actions[1].Action.Workflow);
    }

    [Fact]
    public void UnknownNativeKindsAndUnboundedDeclarationsAreUnavailable()
    {
        var invalid = new ExtensionOptimizationAction("bad", "Bad", "Description", "Consequence", NativeTool: (ToolCacheKind)999);
        var result = OptimizationCatalog.Discover([new Provider("unknown", [invalid]), new Provider("large", Enumerable.Repeat(invalid, 33).ToArray())]);
        Assert.Empty(result.Actions); Assert.Equal(2, result.Errors.Count);
    }

    private sealed class Provider(string id, IReadOnlyList<ExtensionOptimizationAction> actions) : ISpaceMaidExtension, IOptimizationExtension
    {
        public ExtensionDescriptor Descriptor => new(id, "Provider " + id, "1.0.0");
        public Task InitializeAsync(IExtensionHost host, CancellationToken token) => Task.CompletedTask;
        public FrameworkElement? CreateView() => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public IReadOnlyList<ExtensionOptimizationAction> GetOptimizationActions() => actions;
    }
}
