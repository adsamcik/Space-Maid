using System.Globalization;
using System.Windows.Data;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal sealed record ExplorerDeletionFeedback(string ShortText, string Text,
    FileIdentity? Identity = null, WeakReference<ScanSnapshot>? RemovedFrom = null)
{
    internal bool AppliesTo(ExplorerRow row) =>
        (RemovedFrom is null || RemovedFrom.TryGetTarget(out var scan) && ReferenceEquals(scan, row.Scan))
        && (Identity is null || NativeIdentity(row.Scan, row.Entry.LinkIndex) is not { } current || Identity == current);

    internal static FileIdentity? NativeIdentity(ScanSnapshot scan, int link)
    {
        var metadata = scan.GetMetadata(link);
        return metadata.NativeIdLow is { } low && metadata.NativeIdHigh is { } high ? new(low, high) : null;
    }
}

internal sealed class ExplorerDeletionStatus : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values[0] is not ExplorerRow row) return "";
        bool treeLabel = Equals(parameter, "Flags");
        if (!row.IsRemoved && values[1] is IReadOnlyDictionary<string, ExplorerDeletionFeedback> feedback
            && feedback.TryGetValue(row.Path, out var status) && status.AppliesTo(row))
            return treeLabel ? status.ShortText : status.Text;
        return treeLabel ? row.Flags : "";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
