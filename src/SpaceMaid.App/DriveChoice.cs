using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

internal sealed record DriveDescriptor(string RootPath, string Label, string? VolumeId, bool Ready,
    long? TotalBytes = null, long? FreeBytes = null, string? UnavailableReason = null)
{
    internal bool LowSpace => TotalBytes is > 0 && FreeBytes is { } free && free < TotalBytes.Value / 10;
}

internal static class DriveDiscovery
{
    internal static (long Total, long Free) Space(string root)
    {
        var drive = new DriveInfo(root);
        return (drive.TotalSize, drive.AvailableFreeSpace);
    }

    internal static IReadOnlyList<DriveDescriptor> Read()
        => DriveInfo.GetDrives().Select(ReadDrive).ToArray();

    private static DriveDescriptor ReadDrive(DriveInfo drive)
    {
        try
        {
            // Network capacity/label lookups may wait for an offline server.
            // Keep these selectable without probing their remote filesystem.
            if (drive.DriveType == DriveType.Network) return new(drive.Name, "Network drive", null, true);
            if (!drive.IsReady) return new(drive.Name, "", null, false, UnavailableReason: "Not available");
            return new(drive.Name, drive.VolumeLabel, VolumeId(drive.Name), true, drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(drive.Name, "", null, false, UnavailableReason: "Not available"); }
    }

    // A drive letter can be reused by a different device. Only a matching
    // Windows volume identity may inherit a scan's historical suggestions.
    internal static string? VolumeId(string root)
    {
        var name = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPoint(root, name, name.Capacity) ? name.ToString() : null;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);
}

internal sealed record DriveChoice(DriveDescriptor Drive, DriveHistoryEntry? History, bool Current)
{
    public string RootPath => Drive.RootPath;
    public string Title => string.IsNullOrWhiteSpace(Drive.Label) ? RootPath : RootPath + "  ·  " + Drive.Label;
    public bool Ready => Drive.Ready;
    public bool HasCapacity => Ready && Drive.TotalBytes is > 0 && Drive.FreeBytes is not null;
    public double FreePercent => HasCapacity ? Math.Clamp((double)Drive.FreeBytes!.Value / Drive.TotalBytes!.Value * 100, 0, 100) : 0;
    public bool HasScanHistory => History?.ScannedAtUtc is not null;
    public string SpaceHint => Drive.LowSpace ? "Low space" : "";
    public string Capacity => !Ready ? Drive.UnavailableReason ?? "Not available"
        : Drive.TotalBytes is { } total && Drive.FreeBytes is { } free ? $"{SizeText.Format(free)} free of {SizeText.Format(total)}" : "Capacity unavailable";
    public string HistoryCaption => History?.ScannedAtUtc is { } scanned
        ? (History.PartialScan ? "Partial scan" : "Last scan") + $" · {scanned.ToLocalTime():d}"
            + (History.ScanPath is { } scope && !scope.Equals(RootPath, StringComparison.OrdinalIgnoreCase)
                ? " · " + Path.GetFileName(Path.TrimEndingDirectorySeparator(scope)) : "")
            + (History.HasAllocationData ? "" : " · Logical sizes")
        : "No scan history";
    public string HistoryTooltip => History?.ScanPath is { } scope ? $"Previous scan: {scope}\nHistorical sizes; scan again to check current contents." : "Scan this drive to get suggestions.";
    public IReadOnlyList<DriveRecommendationLink> Recommendations => History?.Recommendations
        .Select(hint => new DriveRecommendationLink(Drive, History.ScanPath ?? RootPath, hint)).ToArray() ?? [];
    public bool HasRecentFolder => History is { } history && !history.LastPath.Equals(RootPath, StringComparison.OrdinalIgnoreCase);
    public string RecentFolder => HasRecentFolder ? "Recent: " + Path.GetFileName(Path.TrimEndingDirectorySeparator(History!.LastPath)) : "";
    public string? RecentPath => HasRecentFolder ? History!.LastPath : null;
}

internal sealed record DriveRecommendationLink(DriveDescriptor Drive, string ScopePath, DriveRecommendation Recommendation)
{
    public string Label => $"{Recommendation.Name} · {SizeText.Format(Recommendation.Bytes)}";
    public string Help => $"Open {Recommendation.Name}. Scans {Drive.RootPath} if needed.";
    public bool Ready => Drive.Ready;

    internal bool CanReuse(string? scannedVolume, string? scannedRoot)
    {
        if (Drive.VolumeId is null || !Drive.VolumeId.Equals(scannedVolume, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(scannedRoot)) return false;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scannedRoot));
        string scope = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ScopePath));
        return scope.Equals(root, StringComparison.OrdinalIgnoreCase)
            || scope.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
