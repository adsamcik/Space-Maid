using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CloudInspectionTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070032))] // Not supported
    [InlineData(unchecked((int)0x80070178))] // Not a cloud file
    [InlineData(unchecked((int)0x80070186))] // Not under a sync root
    [InlineData(unchecked((int)0x80071126))] // Not a reparse point
    public void ExplicitNativeAbsenceMeansUnsupported(int result)
        => Assert.False(CloudFiles.InspectionInformationAvailable(result));

    [Theory]
    [InlineData(unchecked((int)0x80070005))] // Access denied
    [InlineData(unchecked((int)0x8007016A))] // Cloud provider not running
    [InlineData(unchecked((int)0x800700EA))] // Incomplete buffer
    [InlineData(unchecked((int)0x80070002))] // Item disappeared
    public void NativeInspectionFailuresCannotEstablishAbsence(int result)
        => Assert.Throws<IOException>(() => CloudFiles.InspectionInformationAvailable(result));

    [Fact]
    [Trait("Stage", "System")]
    public async Task UnavailableItemCanRecoverWithoutBecomingUnsupportedUntilReadSucceeds()
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "restored.bin");
        var operations = new FileOperations();

        var unavailable = await operations.InspectCloudDetailedAsync(path);
        Assert.Equal(CloudInspectionStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.State);
        Assert.False(string.IsNullOrWhiteSpace(unavailable.Error));
        Assert.Null(await operations.InspectCloudAsync(path)); // Existing caller shape remains compatible.

        File.WriteAllText(path, "original contents");
        var recovered = await operations.InspectCloudDetailedAsync(path);
        Assert.Equal(CloudInspectionStatus.Unsupported, recovered.Status);
        Assert.Null(recovered.State);
        Assert.Null(recovered.Error);
        var listing = CloudFiles.ReadDirectory(fixture.Path, default);
        Assert.True(listing.Complete);
        Assert.Empty(listing.Warnings);
        var row = Assert.Single(listing.Entries);
        Assert.Equal("Managed by provider", row.Availability);
        Assert.False(row.CanRequestOnlineOnly);
        Assert.Equal("original contents", File.ReadAllText(path));
    }

    [Fact]
    public async Task DetailedHostInspectionHonorsCancellationBeforeAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FileOperations().InspectCloudDetailedAsync(@"Z:\not-accessed", cancellation.Token));
    }
}
