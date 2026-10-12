using System.ComponentModel;
using System.Globalization;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Apps;

internal sealed class AppRow(DetectedApp app) : INotifyPropertyChanged
{
    public DetectedApp App { get; } = app;
    private InventoryFolderSize? _measurement;
    private string? _error;
    private long _largest;
    public string Name => App.Name;
    public string PathText => App.Path ?? "Install folder unknown";
    public string Publisher => App.Publisher;
    public string Subtitle => string.IsNullOrEmpty(Publisher) ? App.Source : Publisher;
    public long? SizeBytes => _measurement?.AllocatedBytes ?? _measurement?.LogicalBytes ?? App.ReportedBytes;
    public string SizeText => SizeBytes is { } bytes ? Format(bytes) : "—";
    public string Basis => _measurement is { } size
        ? (size.AllocatedBytes.HasValue ? "On disk" : "File size") + (size.Complete ? "" : " · partial")
        : App.ReportedBytes.HasValue ? "Installer estimate" : _error is not null ? "Unavailable" : "Not measured";
    public double Share => SizeBytes is { } bytes && _largest > 0 ? Math.Clamp(100d * bytes / _largest, 0, 100) : 0;
    public string Details => string.Join("\n", new[]
    {
        string.Join(" · ", App.Names) + (string.IsNullOrEmpty(App.Version) ? "" : " · " + App.Version),
        _measurement is { } size ? $"{size.Source} · {size.ObservedAtUtc.ToLocalTime():g} · {size.LogicalBytes:N0} file bytes"
            + (size.AllocatedBytes is { } allocated ? $" · {allocated:N0} allocated bytes" : " · allocation unavailable") : "Installer estimates can differ from current folder usage.",
        "Install folder only; app data and caches elsewhere are excluded. Shared files are counted once within the measurement.",
        App.Overlaps ? "This folder overlaps another listed app folder; their sizes must not be added together." : "",
        _error ?? "",
        _measurement is { } measured ? string.Join("\n", measured.Warnings) : ""
    }.Where(text => text.Length > 0));
    internal void SetMeasurement(InventoryFolderSize? size, string? error = null)
    { _measurement = size; _error = error; Changed(); }
    internal void SetLargest(long largest) { _largest = largest; PropertyChanged?.Invoke(this, new(nameof(Share))); }
    private void Changed() { PropertyChanged?.Invoke(this, new(null)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = bytes; int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit == 0 ? "N0" : "0.##", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
