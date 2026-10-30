using SpaceMaid.Core.Analysis;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OpportunityPresentationTests
{
    [Theory]
    [InlineData("models--buttercrab--dia-v1-1.6b", "buttercrab/dia-v1-1.6b")]
    [InlineData("datasets--org--speech-data", "org/speech-data")]
    [InlineData("spaces--org--demo", "org/demo")]
    [InlineData("models--gpt2", "gpt2")]
    [InlineData("Models--Org--Model", "Org/Model")]
    [InlineData("models----model", "models----model")]
    [InlineData("models--org--model--extra", "models--org--model--extra")]
    public void HuggingFaceNamesAreReadableAndKeepExactPaths(string folder, string expected)
    {
        string path = @"G:\cache\huggingface\hub\" + folder;
        var finding = new Opportunity(1, "Hugging Face repository downloads", "AI models & data", path,
            4096, 4096, "", "", "", true);
        var row = new OpportunityRow(finding, true);
        Assert.Equal(expected, row.Name);
        Assert.Equal(path, row.Path);
        Assert.Equal(folder, row.DisplayPath[(row.DisplayPath.LastIndexOf('\\') + 1)..]);
        var unrelated = new OpportunityRow(finding with { Title = "Other cache" }, true);
        Assert.Equal(folder, unrelated.Name);
    }

    [Fact]
    public void FolderNamesKeepTheirParentContextAsTheBrowserScopeChanges()
    {
        var finding = new Opportunity(1, ".NET build output", "Build output", @"G:\Code\Orbit\module\obj",
            4096, 4096, "", "", "", false);
        var row = new OpportunityRow(finding, true);
        Assert.Equal("obj", row.Name);
        Assert.Equal(@"G:\Code\Orbit\module", row.LocationContext);
        var notifications = new List<string?>();
        row.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        row.SetScope(@"G:\Code\Orbit");
        Assert.Equal("module", row.LocationContext);
        Assert.Contains(nameof(OpportunityRow.LocationContext), notifications);
        row.SetScope(@"G:\Code\Orbit\module");
        Assert.Equal("", row.LocationContext);
        row.SetScope(row.Path);
        Assert.Equal("obj", row.Name); Assert.Equal("", row.LocationContext);
        row.SetScope(null);
        Assert.Equal(@"G:\Code\Orbit\module", row.LocationContext);
    }
}
