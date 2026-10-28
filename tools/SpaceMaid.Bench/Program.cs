using System.Diagnostics;
using System.Text.Json;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Core.Analysis;

if (args.Length == 1 && args[0] == "--classify")
{
    long heapBefore = GC.GetTotalMemory(true), preparationBefore = GC.GetAllocatedBytesForCurrentThread();
    var preparation = Stopwatch.StartNew(); FileTypeClassifier.Prepare(); preparation.Stop();
    long preparationAllocated = GC.GetAllocatedBytesForCurrentThread() - preparationBefore;
    long retained = GC.GetTotalMemory(true) - heapBefore;
    string[] names = ["photo.JPG", "source.C", "types.d.ts", "archive.tar.gz", "drawing.svg.gz", "accounts-wal",
        "sample.safetensors", "sample.gguf", "sample.kicad_pcb", "sample.geojson", "sample.woff2", "sample.dat",
        "Dockerfile", ".env.local", "unknown.unlisted_extension_987", "README"];
    int checksum = 0;
    for (int i = 0; i < 5000; i++) checksum += (int)FileTypeClassifier.Classify(names[i % names.Length]);
    var lookups = new Stopwatch();
    long before = GC.GetAllocatedBytesForCurrentThread();
    lookups.Start();
    for (int i = 0; i < 100000; i++) checksum += (int)FileTypeClassifier.Classify(names[i % names.Length]);
    lookups.Stop();
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Console.WriteLine(JsonSerializer.Serialize(new { FileTypeClassifier.ExtensionCount, FileTypeClassifier.FormatCount,
        PreparationMs = preparation.Elapsed.TotalMilliseconds, PreparationAllocatedBytes = preparationAllocated,
        RetainedManagedBytes = retained, LookupCount = 100000, WarmLookupMs = lookups.Elapsed.TotalMilliseconds,
        WarmLookupAllocatedBytes = allocated, Checksum = checksum,
        Note = "Filename-only microbenchmark; excludes UI rendering and filesystem scanning." }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (args.Length == 0)
{
    Console.WriteLine("Usage: SpaceMaid.Bench <drive-or-folder> [--standard] [--recheck] [--log-directory <path>] | --classify");
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
    var reader = new VolumeScanner(args.Contains("--standard") ? null : new NativeNtfsLayoutSource());
    var scan = await reader.ScanAsync(args[0], cancellationToken: cancellation.Token,
        preferFast: !args.Contains("--standard"), diagnostic: log is null ? null : d => log.Record(d.Event, d));
    timer.Stop();
    log?.Record("benchmark-complete", new { ElapsedMs = timer.Elapsed.TotalMilliseconds, scan.Backend, scan.FallbackReason });
    object? recheck = null;
    if (args.Contains("--recheck"))
    {
        var recheckTimer = Stopwatch.StartNew();
        var refreshed = await reader.MeasureFolderAsync(args[0], cancellation.Token,
            (name, data) => log?.Record(name, data), scan);
        recheck = new { refreshed.Backend, refreshed.FileCount, refreshed.LogicalBytes, refreshed.AllocatedBytes,
            ElapsedMs = recheckTimer.Elapsed.TotalMilliseconds, refreshed.WarningCount };
    }
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
        Recheck = recheck, LogPath = log?.FilePath, LogError = log?.Error
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException) { log?.Record("benchmark-cancelled", new { Target = args[0] }); Console.Error.WriteLine("Scan cancelled."); return 2; }
catch (Exception ex) { log?.Record("benchmark-failed", new { Error = ex.ToString() }); Console.Error.WriteLine(ex.Message); return 1; }
