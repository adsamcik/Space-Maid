using System.IO;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ExplorerTests
{
    [Fact]
    public void ChildrenAreSortedWithoutCountingSharedNamesTwice()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int file = builder.AddFile(new(new(2), FileAttributes.Normal, 9000, 8192, 0));
        builder.AddLink(file, new(1), "original.bin");
        builder.AddLink(file, new(1), "alias.bin");
        int sparse = builder.AddFile(new(new(3), FileAttributes.Normal, 16000, 0, 0));
        builder.AddLink(sparse, new(1), "sparse.bin");
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        int[] children = scan.SortedChildren(scan.RootIndex);
        Assert.Equal("original.bin", scan.Entry(children[0]).Name);
        Assert.Equal(scan.AllocatedBytes, children.Sum(scan.AttributedBytes));
        var alias = scan.Entry(children.Single(link => scan.Entry(link).Name == "alias.bin"));
        Assert.True(alias.IsShared);
        Assert.False(alias.IsPrimaryName);
        Assert.Equal(8192, alias.AllocatedBytes);
        Assert.Equal(0, alias.AttributedBytes(true));
        Assert.Equal(2, scan.RegularFileCount);
        Assert.Equal(1, scan.DirectoryCount);
        Assert.Equal(@"C:\alias.bin", scan.EntryPath(alias.LinkIndex));
    }

    [Fact]
    public void CoverageFlagsPropagateOnlyThroughTheAffectedBranchAndWarningsAreBounded()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int parent = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0));
        int child = builder.AddFile(new(new(3), FileAttributes.Directory, 0, 0, 0));
        int clean = builder.AddFile(new(new(4), FileAttributes.Directory, 0, 0, 0));
        builder.AddLink(parent, new(1), "parent");
        builder.AddLink(child, new(2), "unreadable");
        builder.AddLink(clean, new(1), "clean");
        for (int i = 0; i < 10; i++) builder.Warn($"Could not read C:\\parent\\unreadable ({i})", new(3));
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        Assert.True(scan.RootEntry.IsIncomplete);
        Assert.True(scan.Entry(scan.PrimaryLink(parent)).IsIncomplete);
        Assert.True(scan.Entry(scan.PrimaryLink(child)).IsIncomplete);
        Assert.False(scan.Entry(scan.PrimaryLink(clean)).IsIncomplete);
        Assert.Equal(10, scan.WarningCount);
        Assert.Equal(9, scan.Warnings.Count); // eight examples plus the total
    }

    [Fact]
    public void FallbackReasonDoesNotInventSkippedPathsAndBasicChildrenUseLogicalSize()
    {
        var builder = new ScanBuilder { AllocationData = false, FallbackReason = "Metadata provider fallback" };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int small = builder.AddFile(new(new(2), FileAttributes.Normal, 3, 0, 0));
        int large = builder.AddFile(new(new(3), FileAttributes.Normal, 30, 0, 0));
        builder.AddLink(small, new(1), "small"); builder.AddLink(large, new(1), "large");
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        Assert.Equal("large", scan.Entry(scan.SortedChildren(scan.RootIndex)[0]).Name);
        Assert.Equal("Metadata provider fallback", scan.FallbackReason);
        Assert.Equal(0, scan.WarningCount);
        Assert.False(scan.RootEntry.IsIncomplete);
        Assert.Throws<OperationCanceledException>(() => scan.SortedChildren(scan.RootIndex, new CancellationToken(true)));
    }
}
