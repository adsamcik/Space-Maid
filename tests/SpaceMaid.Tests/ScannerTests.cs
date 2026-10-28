using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ScannerTests
{
    [ElevatedNtfsFact]
    [Trait("Stage", "System")]
    public void NativeLayoutBatchPreservesTheMftDataStreamSizes()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert.Equal("NTFS", new DriveInfo(root).DriveFormat);
        using var volume = NativeMethods.CreateFile(@"\\.\" + root[..2], 0x80000000, 7, 0, 3, 0, 0);
        Assert.False(volume.IsInvalid, $"Volume access error: {Marshal.GetLastWin32Error()}");
        byte[] input = new byte[32];
        LayoutFixture.Put32(input, 4, 0x37); // Same whole-volume request as production, limited to one batch.
        nint request = Marshal.AllocHGlobal(input.Length), response = Marshal.AllocHGlobal(1024 * 1024);
        try
        {
            Marshal.Copy(input, 0, request, input.Length);
            Assert.True(SetSparse(volume, NativeMethods.QueryFileLayout, request, input.Length, response, 1024 * 1024, out int bytes, 0), $"Layout query error: {Marshal.GetLastWin32Error()}");
            byte[] batch = new byte[bytes]; Marshal.Copy(response, batch, 0, bytes);
            int firstFile = (int)LayoutParser.U32(batch, 4);
            ulong id = LayoutParser.U64(batch, firstFile + 16);
            Assert.Equal(0UL, id & 0x0000FFFFFFFFFFFF); // The MFT itself.
            int stream = firstFile + (int)LayoutParser.U32(batch, firstFile + 28);
            while (LayoutParser.U32(batch, stream + 36) != 0x80)
            {
                uint next = LayoutParser.U32(batch, stream + 4);
                Assert.NotEqual(0U, next); // Standard-information/name attributes can precede DATA.
                stream += (int)next;
            }
            Assert.Equal(0x80U, LayoutParser.U32(batch, stream + 36));
            long logical = LayoutParser.I64(batch, stream + 24), allocated = LayoutParser.I64(batch, stream + 16);
            Assert.True(logical > 0 && allocated > 0);
            var builder = new ScanBuilder { AllStreams = true };
            LayoutParser.Parse(batch, builder);
            var scan = builder.Build(root, "Native first-batch fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
            var mft = Assert.Single(scan.Records, file => file.Identity.Low == id);
            Assert.True(mft.LogicalBytes >= logical);
            Assert.True(mft.AllocatedBytes >= allocated);
        }
        finally { Marshal.FreeHGlobal(request); Marshal.FreeHGlobal(response); }
    }

    [Fact]
    public void VersionOneTypedUnnamedDataIsIncludedAndOtherAttributesAreExcluded()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
        byte[] batch = LayoutFixture.File(42, 5, "file", 1, 8192, 8192);
        int stream = 16 + (int)LayoutParser.U32(batch, 44);
        LayoutFixture.Put32(batch, stream, 1);
        LayoutFixture.Put32(batch, stream + 48, 1); // A typed non-data entry with no identifier too.
        LayoutFixture.Put32(batch, stream + 48 + 36, 0x10); // Observed STANDARD_INFORMATION type.
        LayoutParser.Parse(batch, builder);
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        Assert.Equal(8192, scan.LogicalBytes);
        Assert.Equal(8192, scan.AllocatedBytes);
    }

    [Fact]
    public void LegacyVersionOneNamedDataIsIncludedButUnknownUnnamedTypesAreRejected()
    {
        byte[] batch = LayoutFixture.File(42, 5, "file", 1, 1234, 4096);
        int stream = batch.Length - 48; // Use the final record so a name can extend it.
        Array.Resize(ref batch, batch.Length + 32);
        LayoutFixture.Put32(batch, stream, 1);
        LayoutFixture.Put32(batch, stream + 36, 0); // Legacy reserved field.
        LayoutFixture.Put64(batch, stream + 16, 4096); LayoutFixture.Put64(batch, stream + 24, 12);
        byte[] name = Encoding.Unicode.GetBytes(":extra:$DATA");
        LayoutFixture.Put32(batch, stream + 44, (uint)name.Length); name.CopyTo(batch, stream + 48);
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
        LayoutParser.Parse(batch, builder);
        var scan = builder.Build(@"G:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        Assert.Equal(1246, scan.LogicalBytes);
        Assert.Equal(8192, scan.AllocatedBytes);
        LayoutFixture.Put32(batch, stream + 44, 0);
        Assert.Throws<InvalidDataException>(() => LayoutParser.Parse(batch, new()));
    }

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

    [Theory]
    [InlineData(0)] // Older Windows returns zero length for the complete legacy structure.
    [InlineData(24)]
    [InlineData(56)]
    public void LayoutReadsCreatedAndModifiedFromReturnedExtraMetadata(int length)
    {
        byte[] batch = LayoutFixture.File(42, 5, "file", 1, 100, 4096);
        int extra = batch.Length;
        Array.Resize(ref batch, extra + 56);
        LayoutFixture.Put32(batch, 16 + 32, (uint)(extra - 16));
        LayoutFixture.Put32(batch, 16 + 36, (uint)length);
        long created = new DateTime(2025, 2, 3, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), modified = created + TimeSpan.TicksPerDay;
        LayoutFixture.Put64(batch, extra, (ulong)created);
        LayoutFixture.Put64(batch, extra + 16, (ulong)modified);
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
        LayoutParser.Parse(batch, builder);
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        var metadata = scan.GetMetadata(scan.FindEntry(@"C:\file")!.Value);
        Assert.Equal(created, metadata.CreationFileTime); Assert.Equal(modified, metadata.LastWriteFileTime);
        LayoutFixture.Put32(batch, 16 + 36, 16);
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
        DateTime created = new(2023, 4, 6, 10, 0, 0, DateTimeKind.Utc), modified = created.AddDays(2);
        File.SetCreationTimeUtc(original, created); File.SetLastWriteTimeUtc(original, modified);
        Assert.True(CreateHardLink(Path.Join(folder, "alias.bin"), original, 0), $"Hard link error: {Marshal.GetLastWin32Error()}");
        var diagnostics = new List<ScanDiagnostic>();
        var reports = new List<ScanProgress>();
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, new InlineProgress<ScanProgress>(reports.Add),
            preferFast: false, diagnostic: diagnostics.Add);
        Assert.Equal(8192, scan.LogicalBytes);
        Assert.Equal(8192, scan.AllocatedBytes);
        Assert.Equal(4, scan.FileCount); // root, project, bin, one file object
        Assert.Equal(4, scan.NameCount); // two directories, two file names
        foreach (string name in new[] { "original.bin", "alias.bin" })
        {
            var metadata = scan.GetMetadata(scan.FindEntry(Path.Join(folder, name))!.Value);
            Assert.Equal(created.ToFileTimeUtc(), metadata.CreationFileTime);
            Assert.Equal(modified.ToFileTimeUtc(), metadata.LastWriteFileTime);
        }
        Assert.True(ScanSnapshot.IsValidFileTime(scan.GetMetadata(-1).LastWriteFileTime));
        Assert.Empty(scan.Warnings);
        Assert.Contains(diagnostics, d => d.Event == "backend-selected" && d.Backend == "Standard Windows scan");
        var complete = Assert.Single(diagnostics, d => d.Event == "scanner-complete");
        Assert.Equal(scan.FileCount, complete.FileObjects);
        Assert.True(complete.QueryCount > 0);
        Assert.Equal(scan.NameCount, complete.Names);
        var preview = Assert.IsType<ScanPreliminary>(reports[^1].Preliminary);
        Assert.Equal(scan.LogicalBytes, preview.LogicalBytes);
        Assert.Equal(scan.AllocatedBytes, preview.AllocatedBytes);
        Assert.Equal(8192, Assert.Single(preview.LargestFiles).LogicalBytes);
        Assert.Contains(Assert.Single(preview.LargestFiles).Path, new[] { original, Path.Join(folder, "alias.bin") });
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

internal sealed class ElevatedNtfsFactAttribute : FactAttribute
{
    public ElevatedNtfsFactAttribute()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            Skip = "Requires administrator access for the real NTFS layout query.";
    }
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
