using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpaceMaid.Extensions.Photos;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class PhotoDisplayTests
{
    [Fact]
    public void MetadataUpdatesRetainPreviewAndNotifyWithoutReplacingDisplayItem()
    {
        var row = Row(); var display = new PhotoDisplayRow(row, @"C:\photos");
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); image.Freeze();
        Assert.True(display.TrySetPreview(new(image, "Preview"), display.PreviewVersion));
        var changes = 0; display.PropertyChanged += (_, _) => changes++;
        Assert.False(display.Update(row with { })); Assert.Equal(0, changes);
        Assert.True(display.Update(row with { LogicalBytes = 100, Status = "Updated metadata", GroupCount = 3 }));
        Assert.True(changes > 0); Assert.Same(image, display.Preview);
        Assert.Equal(100, display.Row.LogicalBytes); Assert.Contains("Updated metadata", display.LocationHint);
        Assert.Equal("", display.Folder);
        display.CancelPreviewRequest(); Assert.Same(image, display.Preview);
        var earlierRequest = display.PreviewVersion;
        display.Update(display.Row with { AnalysisCurrent = false });
        Assert.Null(display.Preview); Assert.Contains("Analyze", display.PreviewStatus);
        Assert.False(display.TrySetPreview(new(image, "Earlier preview"), earlierRequest));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("identity")]
    [InlineData("sidecar")]
    [InlineData("orientation")]
    [InlineData("authorization")]
    [InlineData("offline")]
    [InlineData("cancel")]
    public void ChangedOrCancelledEvidenceRejectsAnEarlierPreviewCompletion(string change)
    {
        var row = Row(); var display = new PhotoDisplayRow(row, @"C:\photos");
        var request = display.PreviewVersion; display.Requested = true;
        var changed = change switch
        {
            "revision" => row with { Revision = "new-revision" },
            "identity" => row with { ObjectId = "replacement" },
            "sidecar" => row with { SidecarRevision = "new-edits" },
            "orientation" => row with { Orientation = 6 },
            "authorization" => row with { AnalysisCurrent = false },
            "offline" => row with { ContentAvailable = false },
            _ => row
        };
        if (change == "cancel") display.CancelPreviewRequest(); else display.Update(changed);
        Assert.False(display.Requested);
        Assert.False(display.TrySetPreview(new(null, "Old completion"), request));
        Assert.DoesNotContain("Old completion", display.PreviewStatus);
        Assert.True(display.TrySetPreview(new(null, "Current completion"), display.PreviewVersion));
        Assert.Equal("Current completion", display.PreviewStatus);
    }

    [Fact]
    public void CollectionCountAndNameUpdateOnTheExistingPickerItem()
    {
        var collection = new PhotoCollection(1, @"C:\photos", "Photos", "Discovered", null, 10);
        var item = new PhotoDisplayCollection(collection); var changes = 0;
        item.PropertyChanged += (_, _) => changes++;
        item.Update(collection with { }); Assert.Equal(0, changes);
        item.Update(collection with { Count = 12, Name = "Holiday", Status = "Discovering" });
        Assert.Equal(1, changes); Assert.Equal(12, item.Count); Assert.Equal("Holiday", item.Name);
        Assert.Equal("Discovering", item.Value.Status);
    }

    private static PhotoRow Row() => new("identity", @"C:\photos\one.png", "one.png", "Viewing image", 4, 4, 4,
        "revision", true, 1, "Verified", "PNG", 1, 1, 1, null, "hash", "now", "edits", null, 0) { AnalysisCurrent = true };
}
