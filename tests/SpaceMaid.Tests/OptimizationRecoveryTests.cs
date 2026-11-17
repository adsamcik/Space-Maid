using SpaceMaid.App;
using SpaceMaid.Core.Optimization;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OptimizationRecoveryTests
{
    private static readonly AutomaticCleanupRule Rule = new("fixture", "Fixture", "Recreated by fixture", [".tmp"]);
    private static readonly OptimizationPreview Preview = new([
        File(@"G:\owned-optimization\first", "one.tmp"),
        File(@"G:\owned-optimization\first", "two.tmp"),
        File(@"G:\owned-optimization\other", "three.tmp")], [], 0, false, false);

    [Fact]
    public void PreparationFailureDoesNotInvalidateAnyInventoryScope()
    {
        Assert.Empty(MainWindow.OptimizationRecoveryScopes(null, null, false));
        Assert.Empty(MainWindow.OptimizationRecoveryScopes(Preview, null, false));
    }

    [Fact]
    public void InterruptedExecutionKeepsAllPossiblyAffectedContainersButNotTheWholeRoot()
    {
        foreach (var receipt in new OptimizationResult?[] { null, new([], true) })
        {
            var scopes = MainWindow.OptimizationRecoveryScopes(Preview, receipt, true);
            Assert.Equal(new[] { @"G:\owned-optimization\first", @"G:\owned-optimization\other" }, scopes.Select(scope => scope.Path));
            Assert.All(scopes, scope => Assert.True(scope.Recursive));
        }
    }

    [Fact]
    public void CompletedReceiptsNarrowRefreshFailureToChangedOrUncertainFiles()
    {
        var result = new OptimizationResult([
            new(Preview.Files[0].Path, OptimizationOutcome.Deleted, 1, "Deleted"),
            new(Preview.Files[1].Path, OptimizationOutcome.Failed, 0, "Uncertain"),
            new(Preview.Files[2].Path, OptimizationOutcome.Skipped, 0, "Kept")], false);
        Assert.Equal(@"G:\owned-optimization\first",
            Assert.Single(MainWindow.OptimizationRecoveryScopes(Preview, result, true)).Path);
        Assert.Empty(MainWindow.OptimizationRecoveryScopes(Preview,
            new([new(Preview.Files[0].Path, OptimizationOutcome.Skipped, 0, "Kept")], false), true));
    }

    private static OptimizationFile File(string container, string name) =>
        new(System.IO.Path.Join(container, name), container, "fixture", 0, 0, 0, 0, null, Rule);
}
