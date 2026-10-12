using System.IO;
using System.Text.Json;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal sealed record TreeColumnState(ScanSortColumn Id, bool Visible, double Width);
internal sealed record TreeColumnLayout(bool FitName, TreeColumnState[] Columns)
{
    internal static TreeColumnLayout Default => new(true,
    [
        new(ScanSortColumn.Name, true, 260), new(ScanSortColumn.Allocated, true, 96),
        new(ScanSortColumn.Modified, true, 150), new(ScanSortColumn.Logical, false, 96),
        new(ScanSortColumn.Share, false, 80), new(ScanSortColumn.Created, false, 150),
        new(ScanSortColumn.Type, false, 170), new(ScanSortColumn.Extension, false, 90),
        new(ScanSortColumn.Attributes, false, 180)
    ]);

    internal static TreeColumnLayout Load(string path)
    {
        if (!File.Exists(path)) return Default;
        using var stream = File.OpenRead(path);
        if (stream.Length > 16 * 1024) throw new InvalidDataException("Column preferences are too large.");
        var layout = JsonSerializer.Deserialize<TreeColumnLayout>(stream);
        if (layout?.Columns is not { Length: > 0 and <= 32 } columns)
            throw new InvalidDataException("Invalid column preferences.");
        var known = new List<TreeColumnState>();
        foreach (var column in columns)
        {
            if (column is null || !Enum.IsDefined(column.Id)) continue;
            if (known.Any(value => value.Id == column.Id) || !double.IsFinite(column.Width)
                || column.Width < (column.Id == ScanSortColumn.Name ? 140 : 48) || column.Width > 1600)
                throw new InvalidDataException("Invalid column width or duplicate column.");
            known.Add(column with { Visible = column.Id == ScanSortColumn.Name || column.Visible });
        }
        foreach (var column in Default.Columns)
            if (known.All(value => value.Id != column.Id)) known.Add(column);
        return new(layout.FitName, [.. known]);
    }

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string pending = path + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(this));
        File.Move(pending, path, overwrite: true);
    }
}
