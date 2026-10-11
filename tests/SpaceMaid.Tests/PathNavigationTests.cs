using System.IO;
using SpaceMaid.App;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PathNavigationTests
{
    [Theory]
    [InlineData(@"G:", @"G:\")]
    [InlineData(" \"G:\\My projects\\app\" ", @"G:\My projects\app")]
    [InlineData(@"..\other", @"G:\projects\other")]
    [InlineData(@"\\server\share\Photos\", @"\\server\share\Photos")]
    public void FullPathEditsNormalizeWithoutOpeningFiles(string input, string expected)
        => Assert.Equal(expected, PathNavigation.Normalize(input, @"G:\projects\app"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void EmptyEditsAreRejected(string input)
        => Assert.Throws<ArgumentException>(() => PathNavigation.Normalize(input, @"G:\projects\app"));

    [Fact]
    public void AncestorsKeepFullTargetsForDriveAndNetworkPaths()
    {
        var local = PathNavigation.Crumbs(@"G:\projects\My app\build");
        Assert.Equal(new[] { @"G:\", @"G:\projects", @"G:\projects\My app", @"G:\projects\My app\build" }, local.Select(crumb => crumb.Path));
        Assert.Equal("My app", local[2].Name);
        var network = PathNavigation.Crumbs(@"\\server\share\Photos\2026");
        Assert.Equal(new[] { @"\\server\share", @"\\server\share\Photos", @"\\server\share\Photos\2026" }, network.Select(crumb => crumb.Path));
    }

    [Fact]
    public void EnvironmentVariablesExpandInPathEdits()
        => Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetEnvironmentVariable("SystemRoot")!)),
            PathNavigation.Normalize("%SystemRoot%", @"G:\projects\app"));
}
