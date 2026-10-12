using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class TreeColumnTests
{
    [Fact]
    public void LayoutRemembersOrderVisibilityWidthsAndKeepsNameAvailable()
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "columns.json");
        new TreeColumnLayout(false, [new(ScanSortColumn.Modified, true, 177),
            new(ScanSortColumn.Name, false, 311), new(ScanSortColumn.Logical, false, 90)]).Save(path);
        var saved = TreeColumnLayout.Load(path);
        Assert.False(saved.FitName);
        Assert.Equal(ScanSortColumn.Modified, saved.Columns[0].Id);
        Assert.Equal(177, saved.Columns[0].Width);
        Assert.True(saved.Columns.Single(column => column.Id == ScanSortColumn.Name).Visible);
        Assert.False(saved.Columns.Single(column => column.Id == ScanSortColumn.Logical).Visible);
        Assert.Equal(Enum.GetValues<ScanSortColumn>().Length, saved.Columns.Length);
        File.WriteAllText(path, "{broken");
        Assert.Throws<System.Text.Json.JsonException>(() => TreeColumnLayout.Load(path));
        new TreeColumnLayout(false, [new(ScanSortColumn.Name, true, 10)]).Save(path);
        Assert.Throws<InvalidDataException>(() => TreeColumnLayout.Load(path));
    }

    [Fact]
    public void DatesSortChronologicallyWithMissingDatesLastAndFolderDatesAreNotAggregated()
    {
        var builder = new ScanBuilder();
        long early = new DateTime(2024, 3, 2, 9, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), late = early + TimeSpan.TicksPerDay;
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        Add(2, "older", FileAttributes.Directory, early, late);
        Add(3, "newer.jpg", FileAttributes.Normal, late, early);
        Add(4, "unknown", FileAttributes.Normal, 0, 0);
        Add(5, "invalid", FileAttributes.Normal, long.MaxValue, -1);
        int descendant = builder.AddFile(new(new(6), FileAttributes.Normal, 1, 1, late, late));
        builder.AddLink(descendant, new(2), "changed.jpg");
        var scan = builder.Build(@"C:\", "fixture", TimeSpan.Zero, GC.GetTotalAllocatedBytes(), default);
        Assert.Equal(new[] { "newer.jpg", "older", "invalid", "unknown" }, Names(ScanSortColumn.Modified, true));
        Assert.Equal(new[] { "older", "newer.jpg", "invalid", "unknown" }, Names(ScanSortColumn.Modified, false));
        Assert.Equal(new[] { "older", "newer.jpg", "invalid", "unknown" }, Names(ScanSortColumn.Created, true));
        Assert.Equal(new[] { "newer.jpg", "older", "invalid", "unknown" }, Names(ScanSortColumn.Created, false));
        var folder = new ExplorerRow(scan, scan.Entry(scan.FindEntry(@"C:\older")!.Value), 1, false);
        Assert.Equal(DateTime.FromFileTimeUtc(early).ToLocalTime().ToString("g"), folder.Modified);
        Assert.Equal("—", ExplorerRow.FormatDate(0)); Assert.Equal("—", ExplorerRow.FormatDate(long.MaxValue));
        Assert.Equal("Folder", folder.Type); Assert.Equal("—", folder.Extension);
        Assert.Equal("Folder", new ExplorerRow(scan, scan.RootEntry, 0, false).Type);
        var image = new ExplorerRow(scan, scan.Entry(scan.FindEntry(@"C:\newer.jpg")!.Value), 1, false);
        Assert.Equal("Ambiguous", image.Type); Assert.Equal(".jpg", image.Extension);
        Assert.Equal(new[] { "newer.jpg", "older", "invalid", "unknown" }, Names(ScanSortColumn.Type, false));
        Assert.Equal("newer.jpg", Names(ScanSortColumn.Extension, true)[0]);

        void Add(ulong id, string name, FileAttributes attributes, long modified, long created)
        { int index = builder.AddFile(new(new(id), attributes, 0, 0, modified, created)); builder.AddLink(index, new(1), name); }
        string[] Names(ScanSortColumn column, bool descending) => scan.SortedChildren(scan.RootIndex, column, descending).Select(link => scan.Entry(link).Name).ToArray();
    }
}
