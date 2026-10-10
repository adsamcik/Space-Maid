using System.Diagnostics;
using System.Text.Json;
using SpaceMaid.Core.Scanning;

if (args.Length == 0)
{
    Console.WriteLine("Usage: SpaceMaid.Bench <drive-or-folder> [--standard]");
    return 1;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var timer = Stopwatch.StartNew();
    var scan = await new VolumeScanner().ScanAsync(args[0], cancellationToken: cancellation.Token,
        preferFast: !args.Contains("--standard"));
    Console.WriteLine(JsonSerializer.Serialize(new {
        scan.RootPath, scan.Backend, scan.FallbackReason, scan.FileCount, scan.RegularFileCount,
        scan.DirectoryCount, scan.NameCount, scan.WarningCount,
        scan.LogicalBytes, scan.AllocatedBytes, scan.HasAllocationData,
        scan.IncludesAllDataStreams, scan.HasCompleteVolumeLinks,
        ElapsedMs = timer.Elapsed.TotalMilliseconds,
        AcquisitionMs = scan.Metrics.Acquisition.TotalMilliseconds,
        AggregationMs = scan.Metrics.Aggregation.TotalMilliseconds,
        scan.Metrics.ManagedAllocatedBytes,
        PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        scan.Warnings
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Scan cancelled."); return 2; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
