using System.Buffers.Binary;
using System.IO;
using SpaceMaid.Extensions.Containers;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class VirtualDiskCleanupTests
{
    [Theory]
    [InlineData(@"\\server\share\ext4.vhdx")]
    [InlineData(@"\\?\C:\disks\ext4.vhdx")]
    [InlineData(@"C:\disks\..\ext4.vhdx")]
    [InlineData(@"C:\disks\ext4.vhdx ")]
    [InlineData(@"C:\disks\ext4.vhd")]
    [InlineData(@"C:\disks\ext4.vhdx:stream")]
    [InlineData("relative.vhdx")]
    public void CompactionRejectsRedirectableOrUnsupportedTargetNames(string path)
        => Assert.Throws<InvalidOperationException>(() => VirtualDiskCleanup.ValidatePath(path));

    [Fact]
    public void APreviewPinsNativeIdentitySizeAndWriteStateWithoutOpeningOrChangingDiskContent()
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "ext4.vhdx");
        File.WriteAllText(path, "Owned non-disk fixture: preview must not read or mutate content.");
        var first = VirtualDiskCleanup.Capture(path);
        Assert.Equal(path, first.Path); Assert.Equal(new FileInfo(path).Length, first.Length);
        Assert.NotEqual(0UL, first.IdLow | first.IdHigh);
        VirtualDiskCleanup.RequireUnchanged(first, VirtualDiskCleanup.Capture(path));
        File.AppendAllText(path, "Changed after preview.");
        Assert.Throws<InvalidOperationException>(() => VirtualDiskCleanup.RequireUnchanged(first, VirtualDiskCleanup.Capture(path)));
        Assert.Contains("Owned non-disk fixture", File.ReadAllText(path));
    }

    [Fact]
    public void CompactionPreviewRejectsAReplacedDiskEvenWhenItsLengthMatches()
    {
        using var fixture = new TestDirectory(); string path = Path.Join(fixture.Path, "disk.vhdx");
        File.WriteAllText(path, "original"); var captured = VirtualDiskCleanup.Capture(path);
        File.Move(path, path + ".kept"); File.WriteAllText(path, "replaced");
        Assert.Throws<InvalidOperationException>(() => VirtualDiskCleanup.RequireUnchanged(captured, VirtualDiskCleanup.Capture(path)));
        Assert.Equal("original", File.ReadAllText(path + ".kept")); Assert.Equal("replaced", File.ReadAllText(path));
    }

    [Fact]
    public async Task HelperProtocolRoundTripsOnlyTheCapturedTargetAndDisclosedShutdownOption()
    {
        var expected = new VirtualDiskRequest(new(@"C:\disks\ext4.vhdx", 1, 2, 3, 4096, 5678), true);
        using var stream = new MemoryStream();
        await VirtualDiskCleanup.WriteAsync(stream, expected, default); stream.Position = 0;
        Assert.Equal(expected, await VirtualDiskCleanup.ReadAsync<VirtualDiskRequest>(stream, default));
        foreach (int length in new[] { -1, 0, VirtualDiskCleanup.MaximumFrameBytes + 1 })
        {
            byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
            using var invalid = new MemoryStream(header);
            await Assert.ThrowsAsync<InvalidDataException>(() => VirtualDiskCleanup.ReadAsync<VirtualDiskRequest>(invalid, default));
        }
    }
}
