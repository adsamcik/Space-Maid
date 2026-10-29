using SpaceMaid.Extensions.Cloud;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CloudDiscoveryTests
{
    [Theory]
    [InlineData("OneDrive!user!Personal", "OneDrive")]
    [InlineData("GoogleDriveFS!user!account", "Google Drive")]
    [InlineData("Dropbox!user!team", "Dropbox")]
    [InlineData("ProtonDrive!user!account", "Proton Drive")]
    [InlineData("Unrelated!ProtonDrive!account", null)]
    public void RegistrationProviderUsesProviderIdOnly(string registration, string? expected)
        => Assert.Equal(expected, CloudDiscovery.ProviderFor(registration));

    [Fact]
    public void DropboxConfigurationFindsBothAccountsAndIgnoresOtherFields()
    {
        var roots = CloudDiscovery.ParseDropbox("""
            {"personal":{"path":"C:\\Personal","host":123},"business":{"path":"D:\\Team"},"other":{"path":23}}
            """);
        Assert.Equal(2, roots.Count);
        Assert.All(roots, root => Assert.Equal("Dropbox", root.Provider));
        Assert.Contains(roots, root => root.Path == @"C:\Personal");
        Assert.Contains(roots, root => root.Path == @"D:\Team");
    }

    [Fact]
    public void RootsAreDeduplicatedWithoutCombiningNestedScopes()
    {
        var roots = CloudDiscovery.Merge([
            new("Proton Drive", @"C:\Cloud\", "registered"),
            new("Proton Drive", @"c:\cloud", "added"),
            new("Proton Drive", @"C:\Cloud\Photos", "added"),
            new("Dropbox", @"\\server\share", "added"),
            new("OneDrive", "relative", "added")]);
        Assert.Equal(2, roots.Count);
        Assert.Contains(roots, root => root.Path == @"C:\Cloud\Photos");
    }

    [Theory]
    [InlineData(@"C:\Cloud\Photos", @"C:\Cloud", true)]
    [InlineData(@"c:\cloud", @"C:\Cloud", true)]
    [InlineData(@"C:\Cloud-other", @"C:\Cloud", false)]
    [InlineData(@"C:\Cloud\..\Elsewhere", @"C:\Cloud", false)]
    [InlineData(@"D:\Cloud", @"C:\Cloud", false)]
    public void BrowsingStaysWithinTheSelectedRoot(string path, string root, bool expected)
        => Assert.Equal(expected, CloudView.Within(path, root));
}
