using System.IO;
using SpaceMaid.Core.Scanning;
using SpaceMaid.App;
using System.Windows;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ExplorerTests
{
    [Fact]
    public void CollapsingOneBranchPreservesTheOtherExpandedBranch()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        for (ulong id = 2; id <= 3; id++)
        {
            int folder = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(folder, new(1), $"folder-{id}");
            int file = builder.AddFile(new(new(id + 10), FileAttributes.Normal, (long)id, 4096, 0));
            builder.AddLink(file, new(id), "file.bin");
        }
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        var rows = new ExplorerRows(scan);
        int[] folders = scan.SortedChildren(scan.RootIndex);
        rows.Expand(-1, folders);
        foreach (int link in folders) rows.Expand(link, scan.SortedChildren(scan.Entry(link).FileIndex));
        Assert.Equal(5, rows.Count);
        var orders = new Dictionary<int, int[]> { [scan.RootIndex] = folders.Reverse().ToArray() };
        foreach (int link in folders) orders[scan.Entry(link).FileIndex] = scan.SortedChildren(scan.Entry(link).FileIndex);
        rows.Reorder(orders);
        Assert.Equal(folders[1], rows.Row(1).Entry.LinkIndex);
        Assert.All(folders, link => Assert.True(rows.IsExpanded(link)));
        Assert.Equal("file.bin", rows.Row(rows.Find(folders[1]) + 1).Name);
        rows.Collapse(folders[0]);
        Assert.Equal(4, rows.Count);
        Assert.True(rows.IsExpanded(folders[1]));
        Assert.Equal("file.bin", rows.Row(rows.Find(folders[1]) + 1).Name);
        rows.Collapse(-1);
        Assert.Single(rows.Cast<object>());
        Assert.False(rows.IsExpanded(folders[1]));
    }

    [Fact]
    public void TreemapGroupsSmallEntriesAndItsRectanglesPreserveProportions()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 512, 0));
        for (int i = 0; i < 200; i++)
        {
            int file = builder.AddFile(new(new((ulong)i + 2), FileAttributes.Normal, 4096, 4096, 0));
            builder.AddLink(file, new(1), $"file-{i}.bin");
        }
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        var parts = TreemapLayout.Contributions(scan, scan.RootIndex, scan.SortedChildren(scan.RootIndex), new Dictionary<int, SpaceKind>());
        Assert.InRange(parts.Count, 1, 64);
        Assert.Equal(scan.AllocatedBytes, parts.Sum(part => part.Bytes));
        Assert.Contains(parts, part => part.Link == -2 && part.GroupedCount > 0);
        var rectangles = TreemapLayout.Arrange(parts.Select(part => part.Bytes).ToArray(), new Size(400, 250));
        double area = rectangles.Sum(rect => rect.Width * rect.Height);
        Assert.Equal(100000, area, precision: 5);
        for (int i = 0; i < rectangles.Length; i++)
        {
            var rect = rectangles[i];
            Assert.True(new Rect(0, 0, 400, 250).Contains(rect));
            Assert.Equal((double)parts[i].Bytes / scan.AllocatedBytes, rect.Width * rect.Height / area, precision: 6);
        }
    }

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
        Assert.Equal(new[] { "alias.bin", "original.bin", "sparse.bin" },
            scan.SortedChildren(scan.RootIndex, ScanSortColumn.Name, false).Select(link => scan.Entry(link).Name));
        Assert.Equal("sparse.bin", scan.Entry(scan.SortedChildren(scan.RootIndex, ScanSortColumn.Name, true)[0]).Name);
        Assert.Equal("sparse.bin", scan.Entry(scan.SortedChildren(scan.RootIndex, ScanSortColumn.Logical, true)[0]).Name);
        Assert.Equal("original.bin", scan.Entry(scan.SortedChildren(scan.RootIndex, ScanSortColumn.Allocated, true)[0]).Name);
        Assert.Equal("alias.bin", scan.Entry(scan.SortedChildren(scan.RootIndex, ScanSortColumn.Share, false)[0]).Name);
    }

    [Fact]
    public void NestedMapSharesOneBlockBudgetAndPreservesEveryLevelTotal()
    {
        var builder = new ScanBuilder();
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 512, 0));
        for (ulong id = 2; id <= 3; id++)
        {
            int folder = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 512, 0));
            builder.AddLink(folder, new(1), $"folder-{id}");
            for (ulong i = 0; i < 40; i++)
            {
                long bytes = id == 2 ? 16384 : 8192;
                int file = builder.AddFile(new(new(id * 100 + i), FileAttributes.Normal, bytes, bytes, 0));
                builder.AddLink(file, new(id), $"child-{i}.bin");
            }
        }
        for (ulong i = 0; i < 60; i++)
        {
            int file = builder.AddFile(new(new(1000 + i), FileAttributes.Normal, 4096, 4096, 0));
            builder.AddLink(file, new(1), $"small-{i}.bin");
        }
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        var kinds = new Dictionary<int, SpaceKind>();
        var top = TreemapLayout.Contributions(scan, scan.RootIndex, scan.SortedChildren(scan.RootIndex), kinds, maximum: 32);
        var orders = top.Where(part => TreemapLayout.CanNest(scan, part, scan.AllocatedBytes))
            .ToDictionary(part => scan.Entry(part.Link).FileIndex, part => scan.SortedChildren(scan.Entry(part.Link).FileIndex));
        var nested = TreemapLayout.Nest(scan, top, orders, kinds);
        Assert.Equal(scan.AllocatedBytes, nested.Sum(part => part.Bytes));
        Assert.Equal(64, nested.Count + nested.Sum(part => part.Children?.Count ?? 0));
        Assert.Equal(2, nested.Count(part => part.Children is not null));
        foreach (var part in nested.Where(part => part.Children is not null))
        {
            var children = part.Children!;
            Assert.Equal(part.Bytes, children.Sum(child => child.Bytes));
            Assert.All(children, child => Assert.Null(child.Children));
            Assert.Contains(children, child => child.GroupedCount > 0);
        }
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
