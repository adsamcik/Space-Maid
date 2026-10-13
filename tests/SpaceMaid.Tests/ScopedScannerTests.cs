using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ScopedScannerTests
{
    [Fact]
    public async Task ScopedBatchesPreserveDirectoryMembershipAndAvoidWholeVolumeAcquisition()
    {
        using var fixture = new TestDirectory();
        for (int i = 0; i < ScanPipeProtocol.MaximumFileIds + 3; i++)
            File.WriteAllText(Path.Join(fixture.Path, $"file-{i}.dat"), "data");
        var original = await new VolumeScanner().MeasureFolderAsync(fixture.Path);
        var source = new MetadataSource(original);
        var measured = await new VolumeScanner(source).MeasureFolderAsync(fixture.Path);
        Assert.Equal("Fast NTFS scoped metadata", measured.Backend);
        Assert.Equal(original.LogicalBytes, measured.LogicalBytes);
        Assert.Equal(original.AllocatedBytes, measured.AllocatedBytes);
        Assert.Equal(original.NameCount, measured.NameCount);
        Assert.Null(measured.FindEntry(Path.Join(fixture.Path, "outside-name")));
        Assert.True(measured.IncludesAllDataStreams); Assert.False(measured.HasCompleteVolumeLinks);
        Assert.Equal(2, source.Requests.Count);
        Assert.All(source.Requests, ids => Assert.InRange(ids.Length, 1, ScanPipeProtocol.MaximumFileIds));
        Assert.Equal(original.FileCount, source.Requests.Sum(ids => ids.Length));
        Assert.Equal(0, source.WholeVolumeCalls);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("access-denied")]
    [InlineData("missing")]
    [InlineData("unexpected")]
    [InlineData("duplicate")]
    public async Task FailedOrIncompleteFastLookupFallsBackToFreshStreamAccounting(string defect)
    {
        using var fixture = new TestDirectory();
        string file = Path.Join(fixture.Path, "payload.bin");
        File.WriteAllBytes(file, new byte[4096]); File.WriteAllBytes(file + ":extra", new byte[8192]);
        var original = await new VolumeScanner().MeasureFolderAsync(fixture.Path);
        var source = new MetadataSource(original) { Defect = defect };
        var events = new List<string>();
        var measured = await new VolumeScanner(source).MeasureFolderAsync(fixture.Path, diagnostic: (name, _) => events.Add(name));
        Assert.Equal("Targeted Windows stream metadata", measured.Backend);
        Assert.Equal(12288, measured.LogicalBytes); Assert.Equal(original.AllocatedBytes, measured.AllocatedBytes);
        Assert.True(measured.IncludesAllDataStreams); Assert.NotNull(measured.FallbackReason);
        Assert.Contains("scoped-fast-fallback", events); Assert.Equal(0, source.WholeVolumeCalls);
    }

    [Fact]
    public async Task CancellationOfFastLookupDoesNotStartAFallbackRead()
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "data.dat"), "keep");
        var original = await new VolumeScanner().MeasureFolderAsync(fixture.Path);
        using var cancellation = new CancellationTokenSource();
        var source = new MetadataSource(original) { BeforeQuery = () => cancellation.Cancel() };
        var events = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VolumeScanner(source).MeasureFolderAsync(fixture.Path,
            cancellation.Token, (name, _) => events.Add(name)));
        Assert.DoesNotContain("scoped-fast-fallback", events);
        Assert.Equal("keep", File.ReadAllText(Path.Join(fixture.Path, "data.dat")));
    }

    [Fact]
    public async Task ReviewRefreshFolderScanAndExtensionSizesReuseScopedMetadata()
    {
        using var fixture = new TestDirectory();
        string project = Directory.CreateDirectory(Path.Join(fixture.Path, "project")).FullName;
        File.WriteAllText(Path.Join(project, "project.csproj"), "<Project />");
        string output = Directory.CreateDirectory(Path.Join(project, "bin")).FullName;
        string file = Path.Join(output, "result.dll"); File.WriteAllBytes(file, new byte[4096]);
        BuildActivityFixture.AgeProject(project);
        var original = await new VolumeScanner().MeasureFolderAsync(fixture.Path);
        var selected = Assert.Single(DeveloperOpportunityFixture.Find(original), item => item.Path == output);
        File.WriteAllBytes(file + ":named", new byte[8192]);
        var fresh = await new VolumeScanner().MeasureFolderAsync(fixture.Path);
        var source = new MetadataSource(fresh);
        var checkedSelection = await OpportunityReviewValidator.ValidateAsync(original, [selected],
            discover: DeveloperOpportunityFixture.Find, layoutSource: source);
        Assert.Equal(12288, Assert.Single(checkedSelection.ValidatedLocations).LogicalBytes);
        Assert.True(checkedSelection.CanMergeInventory);
        var refreshed = await ScanRechecker.RefreshAsync(original, [new(output)], layoutSource: source);
        Assert.Equal(fresh.LogicalBytes, refreshed.LogicalBytes);
        var progress = new CapturedProgress();
        var scanned = await new VolumeScanner(source).ScanAsync(output, progress);
        Assert.Equal("Fast NTFS folder scan", scanned.Backend); Assert.Equal(12288, scanned.LogicalBytes);
        Assert.True(scanned.IncludesAllDataStreams);
        var preliminary = Assert.IsType<ScanPreliminary>(progress.Last?.Preliminary);
        Assert.Equal(scanned.LogicalBytes, preliminary.LogicalBytes);
        Assert.Equal(12288, Assert.Single(preliminary.LargestFiles).LogicalBytes);
        var inventory = new ExtensionInventory(() => null, () => false, () => new Lease(), layoutSource: () => source);
        var size = await inventory.ReadFolderSizeAsync(output, refresh: true, default);
        Assert.NotNull(size); Assert.Contains("Fast NTFS", size.Source); Assert.Equal(12288, size.LogicalBytes);
        Assert.Equal(0, source.WholeVolumeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedNamespaceDuringFastLookupCannotPublishOrFallBackToOldMembership(bool replaceNestedFolder)
    {
        using var fixture = new TestDirectory();
        string nested = Directory.CreateDirectory(Path.Join(fixture.Path, "nested")).FullName;
        File.WriteAllText(Path.Join(nested, "keep.dat"), "keep");
        var original = await new VolumeScanner().MeasureFolderAsync(fixture.Path);
        var source = new MetadataSource(original)
        {
            BeforeQuery = () =>
            {
                if (replaceNestedFolder)
                {
                    Directory.Move(nested, Path.Join(fixture.Path, "moved"));
                    Directory.CreateDirectory(nested);
                    File.WriteAllText(Path.Join(nested, "keep.dat"), "replacement");
                }
                else File.WriteAllText(Path.Join(nested, "new.dat"), "new contents");
            }
        };
        var events = new List<string>();
        await Assert.ThrowsAsync<IOException>(() => new VolumeScanner(source).MeasureFolderAsync(fixture.Path,
            diagnostic: (name, _) => events.Add(name)));
        Assert.DoesNotContain("scoped-fast-fallback", events);
        Assert.DoesNotContain("scoped-fast-complete", events);
        Assert.Equal(replaceNestedFolder ? "replacement" : "keep", File.ReadAllText(Path.Join(nested, "keep.dat")));
    }

    [ElevatedNtfsFact]
    public async Task NativeFilteredLookupMatchesLiveOwnedNamedStreams()
    {
        using var fixture = new TestDirectory();
        string file = Path.Join(fixture.Path, "payload.bin");
        File.WriteAllBytes(file, new byte[4096]); File.WriteAllBytes(file + ":named", new byte[8192]);
        var normal = await new VolumeScanner().MeasureFolderAsync(fixture.Path);
        var fast = await new VolumeScanner(new NativeNtfsLayoutSource()).MeasureFolderAsync(fixture.Path);
        Assert.Equal("Fast NTFS scoped metadata", fast.Backend);
        Assert.Equal(normal.LogicalBytes, fast.LogicalBytes); Assert.Equal(normal.AllocatedBytes, fast.AllocatedBytes);
        Assert.Equal(normal.NameCount, fast.NameCount);
    }

    [Fact]
    public async Task FastFolderScanRetainsStableJunctionCoverageWarnings()
    {
        using var fixture = new TestDirectory();
        using var outside = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "keep.dat"), "keep");
        File.WriteAllText(Path.Join(outside.Path, "outside.dat"), "outside");
        string junction = Path.Join(fixture.Path, "linked");
        var start = new ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = $"/c mklink /J \"{junction}\" \"{outside.Path}\"";
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        try
        {
            var normal = await new VolumeScanner().MeasureFolderAsync(fixture.Path);
            var fast = await new VolumeScanner(new MetadataSource(normal)).ScanAsync(fixture.Path);
            Assert.Equal("Fast NTFS folder scan", fast.Backend);
            Assert.Equal(normal.LogicalBytes, fast.LogicalBytes);
            Assert.Equal(normal.WarningCount, fast.WarningCount); Assert.True(fast.RootEntry.IsIncomplete);
            Assert.Null(fast.FindEntry(Path.Join(junction, "outside.dat")));
            Assert.Equal("outside", File.ReadAllText(Path.Join(outside.Path, "outside.dat")));
        }
        finally { Directory.Delete(junction); }
    }

    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class CapturedProgress : IProgress<ScanProgress>
    {
        internal ScanProgress? Last { get; private set; }
        public void Report(ScanProgress value) => Last = value;
    }

    private sealed class MetadataSource(ScanSnapshot snapshot) : INtfsLayoutSource
    {
        private readonly Dictionary<FileIdentity, FileRecord> _records = snapshot.Records.ToDictionary(record => record.Identity);
        internal List<FileIdentity[]> Requests { get; } = [];
        internal int WholeVolumeCalls { get; private set; }
        internal string? Defect { get; init; }
        internal Action? BeforeQuery { get; init; }
        public void Acquire(string volume, Action<NtfsLayoutBlock> consume, CancellationToken token)
        { WholeVolumeCalls++; throw new InvalidOperationException("A scoped read must not acquire the whole volume."); }
        public void AcquireFiles(string volume, IReadOnlyList<FileIdentity> files, Action<NtfsLayoutBlock> consume, CancellationToken token)
        {
            Requests.Add(files.ToArray()); BeforeQuery?.Invoke(); token.ThrowIfCancellationRequested();
            if (Defect == "unsupported") throw new NotSupportedException("No scoped lookup.");
            if (Defect == "access-denied") throw new Win32Exception(5);
            var records = files.Select(id => _records[id]).ToList();
            if (Defect == "missing") records.RemoveAt(records.Count - 1);
            if (Defect == "unexpected") records[0] = records[0] with { Identity = new(999999) };
            if (Defect == "duplicate") records.Add(records[0]);
            var entries = records.Select(Encode).ToArray();
            byte[] block = new byte[16 + entries.Sum(entry => entry.Length - 16)];
            LayoutFixture.Put32(block, 0, (uint)entries.Length); LayoutFixture.Put32(block, 4, entries.Length == 0 ? 0u : 16u);
            int offset = 16;
            for (int i = 0; i < entries.Length; i++)
            {
                int length = entries[i].Length - 16;
                entries[i].AsSpan(16).CopyTo(block.AsSpan(offset));
                LayoutFixture.Put32(block, offset + 4, i + 1 == entries.Length ? 0u : (uint)length);
                offset += length;
            }
            consume(new(block, 1));
        }
        private static byte[] Encode(FileRecord record)
        {
            byte[] entry = LayoutFixture.File(record.Identity.Low, 5, "outside-name", 1, record.LogicalBytes, record.AllocatedBytes);
            int extra = entry.Length; Array.Resize(ref entry, extra + 56);
            LayoutFixture.Put32(entry, 28, (uint)record.Attributes);
            LayoutFixture.Put32(entry, 48, (uint)(extra - 16)); LayoutFixture.Put32(entry, 52, 56);
            LayoutFixture.Put64(entry, extra, (ulong)record.CreationFileTime);
            LayoutFixture.Put64(entry, extra + 16, (ulong)record.LastWriteFileTime);
            return entry;
        }
    }
}
