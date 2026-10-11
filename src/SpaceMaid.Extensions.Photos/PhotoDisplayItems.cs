using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace SpaceMaid.Extensions.Photos;

internal sealed class PhotoDisplayCollection(PhotoCollection value) : INotifyPropertyChanged
{
    public PhotoCollection Value { get; private set; } = value;
    public long Id => Value.Id;
    public string Root => Value.Root;
    public string Name => Value.Name;
    public long Count => Value.Count;
    public void Update(PhotoCollection value)
    {
        if (Value == value) return;
        Value = value;
        PropertyChanged?.Invoke(this, new(null));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class PhotoDisplayRow(PhotoRow row, string root) : INotifyPropertyChanged
{
    public PhotoRow Row { get; private set; } = row;
    public string Folder => Path.GetRelativePath(root, Path.GetDirectoryName(Row.Path)!) is { } parent && parent != "." ? parent : "";
    public string LocationHint => Row.Path + "\n" + Row.Status;
    public BitmapSource? Preview { get; private set; }
    public string PreviewStatus { get; private set; } = DefaultPreviewStatus(row);
    public bool Requested { get; set; }
    public long PreviewVersion { get; private set; }

    public bool Update(PhotoRow row)
    {
        if (Row == row) return false;
        var keepPreview = Row.ObjectId == row.ObjectId && Row.Path == row.Path && Row.Revision == row.Revision &&
            Row.SidecarRevision == row.SidecarRevision && Row.Orientation == row.Orientation &&
            Row.ContentAvailable == row.ContentAvailable && Row.AnalysisCurrent == row.AnalysisCurrent;
        Row = row;
        if (!keepPreview)
        {
            PreviewVersion++; Requested = false; Preview = null; PreviewStatus = DefaultPreviewStatus(row);
        }
        PropertyChanged?.Invoke(this, new(null));
        return true;
    }

    // A cancelled request cannot publish into a later page/refresh, but an
    // already decoded preview need not disappear on an unchanged page query.
    public void CancelPreviewRequest() { PreviewVersion++; Requested = Preview is not null; }
    public bool TrySetPreview(PhotoThumbnail result, long requestVersion)
    {
        if (requestVersion != PreviewVersion) return false;
        Preview = result.Image; PreviewStatus = result.Status;
        PropertyChanged?.Invoke(this, new(nameof(Preview)));
        PropertyChanged?.Invoke(this, new(nameof(PreviewStatus)));
        return true;
    }
    private static string DefaultPreviewStatus(PhotoRow row) => !row.ContentAvailable ? "Offline/reparse preview unavailable" :
        row.AnalysisCurrent ? "Waiting for preview" : "Analyze to enable previews";
    public event PropertyChangedEventHandler? PropertyChanged;
}
