using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ModelRepositoryCleanupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WholeModelRepositoriesEnterManualReviewWithCapturedRecycleTargets(bool huggingFace)
    {
        using var fixture = new TestDirectory();
        string repository = Path.Join(fixture.Path, (huggingFace ? "hub/models--org--model" : ".lmstudio/models/publisher/model").Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(repository);
        string weights;
        if (huggingFace)
        {
            Directory.CreateDirectory(Path.Join(repository, "refs"));
            Directory.CreateDirectory(Path.Join(repository, "snapshots", "revision"));
            weights = Path.Join(repository, "snapshots", "revision", "model.safetensors");
        }
        else weights = Path.Join(repository, "model.gguf");
        File.WriteAllText(weights, "Owned model fixture, preserved during preview");
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var finding = Assert.Single(OpportunityDetector.Find(scan));
        Assert.Empty(finding.CleanupBlockReason); Assert.True(finding.RequiresManualReview);
        var plan = await new FileOperations().PrepareCleanupAsync(scan, [finding], discover: DeveloperOpportunityFixture.Find);
        Assert.True(plan.CanExecute); Assert.Empty(plan.BlockedLocations);
        Assert.Equal(repository, Assert.Single(plan.Targets).Path);
        Assert.Equal(CleanupReadiness.NeedsReview, plan.BuildAssessments[finding.DirectoryIndex].State);
        Assert.Equal("Owned model fixture, preserved during preview", File.ReadAllText(weights));
    }
}
