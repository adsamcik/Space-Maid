using SpaceMaid.Core.Analysis;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OpportunityPresentationTests
{
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
