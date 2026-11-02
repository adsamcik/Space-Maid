using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.IO;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ExtensionNavigationIconTests
{
    [Fact]
    public void APackagePngKeepsItsAlphaAndDoesNotRetainTheFile()
    {
        using var fixture = new TestDirectory();
        string assets = Directory.CreateDirectory(Path.Join(fixture.Path, "Assets")).FullName;
        string path = Path.Join(assets, "Navigation.png"); WriteIcon(path);
        var errors = new List<string>();
        var icon = ExtensionRuntime.ReadNavigationIcon(new DiskAccess(() => null), fixture.Path, "Assets/Navigation.png", errors.Add);
        Assert.NotNull(icon); Assert.True(icon.IsFrozen); Assert.Equal(2, icon.PixelWidth); Assert.Equal(1, icon.PixelHeight);
        Assert.Empty(errors);
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Delete(path);
        var pixels = new byte[8]; new FormatConvertedBitmap(icon, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 8, 0);
        Assert.Equal(0, pixels[3]); Assert.Equal(255, pixels[7]);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("C:\\outside.png")]
    [InlineData("https://example.invalid/icon.png")]
    [InlineData("Assets/Navigation.png:alternate.png")]
    [InlineData("Assets/icon.svg")]
    public void UnsafeOrUnsupportedPathsUseTheFallback(string relative)
    {
        using var fixture = new TestDirectory(); var errors = new List<string>();
        Assert.Null(ExtensionRuntime.ReadNavigationIcon(new DiskAccess(() => null), fixture.Path, relative, errors.Add));
        Assert.Single(errors);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("file-size")]
    [InlineData("dimensions")]
    public void InvalidOrUnboundedImagesUseTheFallback(string kind)
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "icon.png");
        if (kind == "invalid") File.WriteAllBytes(path, new byte[40]);
        else if (kind == "file-size") { using var file = File.Create(path); file.SetLength(1024 * 1024 + 1); }
        else
        {
            var header = new byte[33]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 13); "IHDR"u8.CopyTo(header.AsSpan(12));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 1025); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 1);
            File.WriteAllBytes(path, header);
        }
        var errors = new List<string>();
        Assert.Null(ExtensionRuntime.ReadNavigationIcon(new DiskAccess(() => null), fixture.Path, "icon.png", errors.Add));
        Assert.Single(errors);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("valid")]
    public void ApiV1ManifestsAcceptOptionalIconsAndUseFallbacks(string kind)
    {
        using var fixture = new TestDirectory();
        var manifest = new ExtensionManifest("test.navigation", "Navigation", "1.0.0", 1, "Entry.dll", "Test.Extension")
            { NavigationIcon = kind == "omitted" ? null : "Assets/Navigation.png" };
        File.WriteAllText(Path.Join(fixture.Path, "extension.json"), JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        if (kind is "valid" or "malformed")
        {
            string assets = Directory.CreateDirectory(Path.Join(fixture.Path, "Assets")).FullName;
            if (kind == "valid") WriteIcon(Path.Join(assets, "Navigation.png"));
            else File.WriteAllBytes(Path.Join(assets, "Navigation.png"), new byte[40]);
        }
        var restored = ExtensionRuntime.ReadManifest(fixture.Path);
        Assert.Equal(manifest, restored);
        var errors = new List<string>();
        var icon = ExtensionRuntime.ReadNavigationIcon(new DiskAccess(() => null), fixture.Path, restored.NavigationIcon, errors.Add);
        if (kind == "valid") { Assert.NotNull(icon); Assert.True(icon.IsFrozen); }
        else Assert.Null(icon);
        if (kind is "missing" or "malformed") Assert.Single(errors);
        else Assert.Empty(errors);
    }

    [Fact]
    [Trait("Stage", "System")]
    public async Task ALinkedAssetDirectoryIsNotFollowed()
    {
        using var fixture = new TestDirectory(); using var outside = new TestDirectory();
        WriteIcon(Path.Join(outside.Path, "Navigation.png"));
        string junction = Path.Join(fixture.Path, "Assets");
        await OpportunityReviewValidatorTests.CreateJunctionAsync(junction, outside.Path);
        try
        {
            var errors = new List<string>();
            Assert.Null(ExtensionRuntime.ReadNavigationIcon(new DiskAccess(() => null), fixture.Path, "Assets/Navigation.png", errors.Add));
            Assert.Single(errors); Assert.True(File.Exists(Path.Join(outside.Path, "Navigation.png")));
        }
        finally { Directory.Delete(junction); }
    }

    private static void WriteIcon(string path)
    {
        var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 0, 255, 255, 255, 255 }, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
