using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CleanupImpactTests
{
    [Fact]
    public void RecyclingExplainsWhenSpaceIsFreedAndRecoveryEnds()
    {
        var impact = CleanupImpact.Describe(Preview(Build()), BuildCleanupAction.Recycle);
        Assert.Equal("Potential space after emptying the Recycle Bin", impact.Caption);
        Assert.Equal("Recycle 1 folder", impact.Action);
        Assert.Contains("Recoverable until you empty it", impact.Consequence);
        Assert.False(impact.Permanent);
    }

    [Fact]
    public void PermanentPolicyDescribesOnlyActuallySelectedBuilds()
    {
        var builds = CleanupImpact.Describe(Preview(Build()), BuildCleanupAction.PermanentlyDelete);
        Assert.Equal("Delete 1 folder permanently", builds.Action);
        Assert.True(builds.Permanent);
        Assert.Contains("No recovery", builds.Consequence);
        var cacheOnly = CleanupImpact.Describe(Preview(Cache()), BuildCleanupAction.PermanentlyDelete);
        Assert.Equal("Recycle 1 folder", cacheOnly.Action);
        Assert.False(cacheOnly.Permanent);
        Assert.DoesNotContain("permanently", cacheOnly.Consequence);
    }

    [Fact]
    public void MixedCleanupNamesBothConsequences()
    {
        var impact = CleanupImpact.Describe(Preview(Build(), Cache()), BuildCleanupAction.PermanentlyDelete);
        Assert.Equal("Clean 2 folders", impact.Action);
        Assert.Contains("cleanup and emptying", impact.Caption);
        Assert.Contains("1 folder will be permanently deleted", impact.Consequence);
        Assert.Contains("1 folder will move to the Recycle Bin", impact.Consequence);
    }

    [Theory]
    [InlineData(BuildCleanupAction.Recycle)]
    [InlineData(BuildCleanupAction.PermanentlyDelete)]
    public void LogicalSizeNeverPromisesDiskSavings(BuildCleanupAction action)
    {
        var preview = new CleanupPreview([Build()], 123, 0, 1, true, []);
        var impact = CleanupImpact.Describe(preview, action);
        Assert.Equal("Selected file size · disk savings unknown", impact.Caption);
    }

    [Fact]
    public void EmptySelectionDoesNotSuggestDeletion()
    {
        var impact = CleanupImpact.Describe(Preview(), BuildCleanupAction.PermanentlyDelete);
        Assert.Equal("Choose folders to clean", impact.Action);
        Assert.False(impact.Permanent);
        Assert.Contains("Nothing will be removed", impact.Consequence);
    }

    private static CleanupPreview Preview(params Opportunity[] locations) => new(locations, 123, 0, 1, false, []);
    private static Opportunity Build() => Cache() with { IsBuildOutput = true };
    private static Opportunity Cache() => new(1, "Fixture", "Cache", @"C:\fixture", 123, 123, "", "", "", true);
}
