using System.Diagnostics;
using System.Text.Json;
using SpaceMaid.Core.Scanning;

if (args.Length == 0)
{
    Console.WriteLine("Usage: SpaceMaid.Bench <drive-or-folder> [--standard] [--log-directory <path>]");
    return 1;
}
using var cancellation = new CancellationTokenSource();
int logArgument = Array.IndexOf(args, "--log-directory");
if (logArgument == args.Length - 1) { Console.Error.WriteLine("--log-directory requires a path."); return 1; }
using var log = logArgument >= 0 && logArgument + 1 < args.Length ? new ScanLog(args[logArgument + 1]) : null;
log?.Record("benchmark-start", new { Target = args[0], Runtime = Environment.Version.ToString(), ProcessId = Environment.ProcessId });
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var timer = Stopwatch.StartNew();
    var scan = await new VolumeScanner().ScanAsync(args[0], cancellationToken: cancellation.Token,
        preferFast: !args.Contains("--standard"), diagnostic: log is null ? null : d => log.Record(d.Event, d));
    log?.Record("benchmark-complete", new { ElapsedMs = timer.Elapsed.TotalMilliseconds, scan.Backend, scan.FallbackReason });
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
        scan.Warnings,
        LogPath = log?.FilePath, LogError = log?.Error
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException) { log?.Record("benchmark-cancelled", new { Target = args[0] }); Console.Error.WriteLine("Scan cancelled."); return 2; }
catch (Exception ex) { log?.Record("benchmark-failed", new { Error = ex.ToString() }); Console.Error.WriteLine(ex.Message); return 1; }
