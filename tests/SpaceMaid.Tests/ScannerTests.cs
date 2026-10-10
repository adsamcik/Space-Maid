using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ScannerTests
{
    [Fact]
    public void LayoutPreservesCombinedDosNamesAndAddsOnlyDataStreams()
    {
        var builder = new ScanBuilder { AllStreams = true };
        builder.RootIndex = builder.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
        byte[] batch = LayoutFixture.File(42, 5, "CACHE", nameFlags: 3, dataSize: 70, allocation: 4096);
        LayoutParser.Parse(batch, builder);
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        Assert.Equal(70, scan.LogicalBytes);
        Assert.Equal(4096, scan.AllocatedBytes);
        Assert.Equal(@"C:\CACHE", scan.PathForFile(1));
    }

    [Fact]
    public void LayoutRejectsTruncatedAndBackwardChains()
    {
        byte[] batch = LayoutFixture.File(42, 5, "file", 1, 100, 4096);
        Assert.Throws<InvalidDataException>(() => LayoutParser.Parse(batch[..70], new()));
        BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(56), 4); // name's NextNameOffset
        Assert.Throws<InvalidDataException>(() => LayoutParser.Parse(batch, new()));
    }

    [Fact]
    public void IdentityGenerationIsNotDiscardedAndHardLinksAreCountedOnce()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
        int folder = builder.AddFile(new(new(0x1000000000010), FileAttributes.Directory, 0, 0, 0));
        builder.AddLink(folder, new(5), "cache");
        int file = builder.AddFile(new(new(99), FileAttributes.Normal, 80, 4096, 0));
        builder.AddLink(file, new(5), "a");
        builder.AddLink(file, new(5), "b");
        int stale = builder.AddFile(new(new(100), FileAttributes.Normal, 200, 4096, 0));
        builder.AddLink(stale, new(0x2000000000010), "wrong-generation");
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        Assert.Equal(80, scan.LogicalBytes);
        Assert.Equal(4096, scan.AllocatedBytes);
        Assert.Null(scan.PathForFile(stale));
        Assert.NotEmpty(scan.Warnings);
    }

    [Fact]
    public void CyclesAreDiagnosedWithoutInventingPaths()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
        int a = builder.AddFile(new(new(10), FileAttributes.Directory, 0, 0, 0));
        int b = builder.AddFile(new(new(11), FileAttributes.Directory, 0, 0, 0));
        builder.AddLink(a, new(11), "a");
        builder.AddLink(b, new(10), "b");
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        Assert.Null(scan.PathForFile(a));
        Assert.NotEmpty(scan.Warnings);
    }

    [Fact]
    public async Task StandardScanReadsSizesAndDeduplicatesRealHardLinksWithoutChangingFiles()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "project", "bin")).FullName;
        string original = Path.Join(folder, "original.bin");
        byte[] content = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(original, content);
        Assert.True(CreateHardLink(Path.Join(folder, "alias.bin"), original, 0), $"Hard link error: {Marshal.GetLastWin32Error()}");
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        Assert.Equal(8192, scan.LogicalBytes);
        Assert.Equal(8192, scan.AllocatedBytes);
        Assert.Equal(4, scan.FileCount); // root, project, bin, one file object
        Assert.Equal(4, scan.NameCount); // two directories, two file names
        Assert.Empty(scan.Warnings);
        Assert.Equal(content, await File.ReadAllBytesAsync(original));
    }

    [Fact]
    public async Task CancellationDoesNotReturnACompleteSnapshot()
    {
        using var fixture = new TestDirectory();
        Directory.CreateDirectory(System.IO.Path.Join(fixture.Path, "nested"));
        await File.WriteAllTextAsync(System.IO.Path.Join(fixture.Path, "unchanged.txt"), "keep");
        using var token = new CancellationTokenSource();
        var progress = new InlineProgress<ScanProgress>(_ => token.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VolumeScanner().ScanAsync(fixture.Path,
            progress, token.Token, preferFast: false));
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Join(fixture.Path, "unchanged.txt")));
    }

    [Fact]
    public async Task SparseFileSizeIsNotPresentedAsAllocatedSpace()
    {
        using var fixture = new TestDirectory();
        string path = System.IO.Path.Join(fixture.Path, "sparse.bin");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.True(SetSparse(file.SafeFileHandle, 0x900C4, 0, 0, 0, 0, out _, 0));
            file.SetLength(16 * 1024 * 1024);
        }
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        Assert.Equal(16 * 1024 * 1024, scan.LogicalBytes);
        Assert.Equal(0, scan.AllocatedBytes);
    }

    [Fact]
    public void LayoutIncludesAlternateDataStreams()
    {
        var builder = new ScanBuilder { AllStreams = true };
        builder.RootIndex = builder.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
        byte[] batch = LayoutFixture.File(42, 5, "file", 1, 70, 4096);
        int secondStream = batch.Length - 48;
        LayoutFixture.Put32(batch, secondStream + 36, 0x80);
        LayoutFixture.Put64(batch, secondStream + 16, 4096);
        LayoutFixture.Put64(batch, secondStream + 24, 1234);
        LayoutParser.Parse(batch, builder);
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        Assert.Equal(1304, scan.LogicalBytes);
        Assert.Equal(8192, scan.AllocatedBytes);
    }

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSparse(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code,
        nint input, int inputLength, nint output, int outputLength, out int returned, nint overlapped);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, nint security);
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SpaceMaid-tests", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        string expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SpaceMaid-tests")) + System.IO.Path.DirectorySeparatorChar;
        if (!System.IO.Path.GetFullPath(Path).StartsWith(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its owned directory.");
        Directory.Delete(Path, recursive: true);
    }
}

internal static class LayoutFixture
{
    public static byte[] File(ulong id, ulong parent, string name, uint nameFlags, long dataSize, long allocation)
    {
        byte[] encoded = Encoding.Unicode.GetBytes(name);
        int streamOffset = (80 + encoded.Length + 7) & ~7;
        byte[] result = new byte[streamOffset + 96];
        Put32(result, 0, 1); Put32(result, 4, 16); Put32(result, 8, 1);
        Put32(result, 16, 1); Put32(result, 28, (uint)FileAttributes.Normal);
        Put64(result, 32, id); Put32(result, 40, 40); Put32(result, 44, (uint)(streamOffset - 16));
        Put32(result, 60, nameFlags); Put64(result, 64, parent); Put32(result, 72, (uint)encoded.Length);
        encoded.CopyTo(result, 80);
        Put32(result, streamOffset, 2); Put32(result, streamOffset + 4, 48);
        Put64(result, streamOffset + 16, (ulong)allocation); Put64(result, streamOffset + 24, (ulong)dataSize);
        Put32(result, streamOffset + 36, 0x80);
        Put32(result, streamOffset + 48, 2); // non-data stream should be ignored
        Put64(result, streamOffset + 64, 99999); Put64(result, streamOffset + 72, 99999);
        Put32(result, streamOffset + 84, 0x90);
        return result;
    }
    public static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    public static void Put64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
}
