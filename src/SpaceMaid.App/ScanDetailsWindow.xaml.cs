using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

public partial class ScanDetailsWindow : Window
{
    public ScanDetailsWindow(ScanSnapshot scan, TimeSpan detection, TimeSpan presentation)
    {
        InitializeComponent();
        BackendText.Text = scan.Backend;
        TargetText.Text = scan.RootPath;
        FallbackText.Text = scan.FallbackReason ?? (scan.Backend == "Fast NTFS scan"
            ? "Bulk NTFS metadata scan." : "Standard directory metadata scanning.");
        CountsText.Text = $"{scan.RegularFileCount:N0} files · {scan.DirectoryCount:N0} folders · {scan.NameCount:N0} names";
        double seconds = scan.Metrics.Acquisition.TotalSeconds;
        ThroughputText.Text = seconds > 0 ? $"Metadata throughput: {scan.RegularFileCount / seconds:N0} files/s" : "Metadata throughput: —";
        using var process = Process.GetCurrentProcess();
        MemoryText.Text = $"Peak process memory: {SizeText.Format(process.PeakWorkingSet64)} · Managed allocations during scanning: {SizeText.Format(scan.Metrics.ManagedAllocatedBytes)}";
        CoverageText.Text = scan.WarningCount > 0 ? $"Partial coverage · {scan.WarningCount:N0} warnings" : "No skipped-path warnings";
        var phases = new[] { ("Read file metadata", scan.Metrics.Acquisition), ("Calculate folder totals", scan.Metrics.Aggregation), ("Find opportunities & estimate candidates", detection), ("Prepare explorer", presentation) };
        double total = phases.Sum(phase => phase.Item2.TotalSeconds);
        foreach (var (name, time) in phases)
        {
            var row = new DockPanel();
            var value = new TextBlock { Text = $"{time.TotalSeconds:0.000} s", HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12 };
            DockPanel.SetDock(value, Dock.Right); row.Children.Add(value);
            row.Children.Add(new TextBlock { Text = name, FontSize = 12, Opacity = .7 });
            PhaseList.Children.Add(row);
            PhaseList.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(.001, total), Value = time.TotalSeconds, Height = 6, Margin = new(0, 6, 0, 12) });
        }
        TotalText.Text = $"Total observed work: {total:0.000} s";
        var notes = new List<string> { "A live scan is an observation, not a point-in-time snapshot. File totals can differ from drive usage because filesystem metadata and reserved storage are not fully attributed.", "Shared file names remain visible; allocation is attributed once. Other hard-link names show zero additional allocation." };
        if (scan.LastRecheckUtc is { } rechecked)
            notes.Insert(0, $"Targeted metadata recheck: {rechecked.ToLocalTime():g}. Other folders retain earlier data. Phase timings are from the original scan; recheck timings and locations are in the operation log.");
        if (!scan.IncludesAllDataStreams) notes.Add("Alternate data streams are not fully included in standard scanning.");
        if (!scan.HasCompleteVolumeLinks) notes.Add("Hard links outside accessible/scanned locations remain unverified.");
        if (scan.RetainsEarlierNtfsMetadata) notes.Add("Reserved NTFS metadata retains its original scan age; directory rechecks cannot reread that hidden namespace.");
        if (!scan.HasAllocationData) notes.Add("Physical allocation is unavailable. The tree is sorted by logical size and the map uses logical size.");
        AccountingText.Text = string.Join("\n\n", notes);
        WarningHeading.Text = scan.WarningCount > 8 ? "DIAGNOSTICS · FIRST 8 EXAMPLES & TOTAL" : "DIAGNOSTICS";
        WarningList.ItemsSource = scan.Warnings.Count > 0 ? scan.Warnings : new[] { "No coverage warnings were recorded." };
    }
}
