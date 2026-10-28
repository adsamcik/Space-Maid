using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using SpaceMaid.Core.Scanning;

// Each case uses the production whole-volume metadata reader. No directory
// fallback and no content reads; only one native scan is active at a time.
internal static class NativeNtfsBench
{
    public static int Run(string[] args)
    {
        Options options;
        try { options = Parse(args); }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 1; }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        var samples = new List<Sample>();
        try
        {
            for (int pass = 1; pass <= options.Passes && !stop.IsCancellationRequested; pass++)
            {
                var cases = (pass & 1) == 1 ? options.Cases : options.Cases.Reverse();
                foreach (var configuration in cases)
                {
                    if (stop.IsCancellationRequested) break;
                    Console.Error.WriteLine($"Native NTFS pass {pass}: {configuration.MiB} MiB, read-ahead {configuration.ReadAhead}, probe {options.Probe}.");
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var sample = Measure(options.Volume, configuration, pass, options.Probe, stop.Token);
                    samples.Add(sample);
                    Publish(); // Preserve completed cases if a later run is interrupted.
                    if (sample.Outcome == "Failed") return 1;
                }
            }
            return stop.IsCancellationRequested ? 2 : 0;
        }
        finally { Publish(final: true); Console.CancelKeyPress -= cancel; }

        void Publish(bool final = false)
        {
            var first = samples.FirstOrDefault(sample => sample.Outcome == "Complete");
            foreach (var sample in samples)
                sample.MatchesFirstCompleteAccounting = sample.Outcome == "Complete" && first is not null
                    ? sample.Accounting == first.Accounting : null;
            string json = JsonSerializer.Serialize(new { options.Volume, options.Probe, Runtime = Environment.Version.ToString(),
                ProcessId = Environment.ProcessId, Samples = samples,
                Scope = "Full live-volume native metadata acquisition plus streaming per-batch parsing and accounting fingerprinting; no final inventory, directory aggregation, helper IPC, opportunity analysis or UI. Probe mode intentionally cancels after the first parsed batch and is not equal work across buffer sizes. Query metrics count delivered blocks; cancelled read-ahead can finish an undelivered query. Query and consumer times overlap with read-ahead and must not be added. Configurations are interleaved and reverse each pass; disk cache and external activity are uncontrolled. Fingerprints help detect changed work but are not cryptographic snapshot proof. Working-set samples may miss short peaks; the OS peak covers this entire process. DATA totals sum returned records, not directory-attributed disk usage." },
                new JsonSerializerOptions { WriteIndented = true });
            if (options.Output is not null) File.WriteAllText(options.Output, json);
            // Progress is on stderr; keep stdout a single complete JSON document.
            if (final) Console.WriteLine(json);
        }
    }

    private static Sample Measure(string volume, Configuration configuration, int pass, bool probe, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var process = Process.GetCurrentProcess();
        var sample = new Sample { Pass = pass, BufferMiB = configuration.MiB, ReadAhead = configuration.ReadAhead,
            StartedUtc = DateTime.UtcNow, StartWorkingSetBytes = Environment.WorkingSet, SampledPeakWorkingSetBytes = Environment.WorkingSet };
        long allocatedBefore = GC.GetTotalAllocatedBytes(true), cancellationStarted = 0, lastMemorySample = 0;
        long files = 0, names = 0, directories = 0, logical = 0, allocated = 0;
        ulong identityXor = 0, identitySum = 0;
        var timer = Stopwatch.StartNew();
        try
        {
            new NativeNtfsLayoutSource(configuration.MiB * 1024 * 1024, configuration.ReadAhead).Acquire(volume, block =>
            {
                sample.QueryCount++; sample.QueryMilliseconds += block.QueryMilliseconds; sample.ReturnedBytes += block.Data.Length;
                if (sample.QueryCount == 1) sample.FirstBlockDeliveryMilliseconds = timer.Elapsed.TotalMilliseconds;
                if (block.Data.IsEmpty) return;
                long started = Stopwatch.GetTimestamp();
                var builder = new ScanBuilder();
                LayoutParser.Parse(block.Data.Span, builder);
                if (builder.Count == 0)
                    throw new InvalidDataException("The NTFS enumeration made no forward progress.");
                if (builder.Count != LayoutParser.U32(block.Data.Span, 0))
                    throw new InvalidDataException("Duplicate file metadata within a native batch.");
                files += builder.Count; names += builder.NameCount;
                foreach (var file in builder.Files)
                {
                    if (file.IsDirectory) directories++;
                    logical = checked(logical + file.LogicalBytes); allocated = checked(allocated + file.AllocatedBytes);
                    ulong hash = Mix(file.Identity.Low) ^ Mix(file.Identity.High) ^ Mix((ulong)file.LogicalBytes)
                        ^ Mix((ulong)file.AllocatedBytes + 1) ^ Mix((ulong)file.Attributes)
                        ^ Mix(unchecked((ulong)file.LastWriteFileTime)) ^ Mix(unchecked((ulong)file.CreationFileTime) + 1);
                    identityXor ^= hash; identitySum = unchecked(identitySum + hash);
                }
                sample.BatchCount++; sample.ConsumerMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (timer.ElapsedMilliseconds - lastMemorySample >= 500)
                {
                    lastMemorySample = timer.ElapsedMilliseconds;
                    sample.SampledPeakWorkingSetBytes = Math.Max(sample.SampledPeakWorkingSetBytes, Environment.WorkingSet);
                }
                if (probe)
                {
                    cancellationStarted = Stopwatch.GetTimestamp();
                    stop.Cancel();
                }
            }, stop.Token);
            sample.Outcome = "Complete";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { sample.Outcome = token.IsCancellationRequested ? "Cancelled" : "ProbeCancelled"; }
        catch (Exception error)
        {
            sample.Outcome = "Failed"; sample.Error = error.Message; sample.ErrorType = error.GetType().FullName;
            sample.Win32Error = (error as Win32Exception)?.NativeErrorCode;
        }
        finally
        {
            timer.Stop(); sample.ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
            if (cancellationStarted != 0) sample.CancellationDrainMilliseconds = Stopwatch.GetElapsedTime(cancellationStarted).TotalMilliseconds;
            sample.ManagedAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            sample.Accounting = new(files, names, directories, logical, allocated, identityXor.ToString("X16"), identitySum.ToString("X16"));
            sample.EndWorkingSetBytes = Environment.WorkingSet;
            sample.SampledPeakWorkingSetBytes = Math.Max(sample.SampledPeakWorkingSetBytes, sample.EndWorkingSetBytes);
            sample.ProcessLifetimePeakWorkingSetBytes = process.PeakWorkingSet64;
        }
        return sample;
    }

    private static ulong Mix(ulong value)
    {
        value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111ebUL);
        return value ^ (value >> 31);
    }

    private static Options Parse(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("--ntfs-native requires a local drive root.");
        int passes = 1; int? buffer = null; bool? ahead = null; bool probe = false; string? output = null;
        for (int i = 1; i < args.Length; i++)
        {
            string argument = args[i];
            if (argument == "--probe") { probe = true; continue; }
            if (++i >= args.Length) throw new ArgumentException($"{argument} requires a value.");
            switch (argument)
            {
                case "--passes" when int.TryParse(args[i], out int count) && count is >= 1 and <= 10: passes = count; break;
                case "--buffer-mib" when int.TryParse(args[i], out int size) && size is 1 or 4 or 8: buffer = size; break;
                case "--read-ahead" when args[i] is "on" or "off": ahead = args[i] == "on"; break;
                case "--output": output = Path.GetFullPath(args[i]); break;
                default: throw new ArgumentException($"Invalid native benchmark option: {argument} {args[i]}");
            }
        }
        if (buffer.HasValue != ahead.HasValue) throw new ArgumentException("Select both --buffer-mib and --read-ahead, or neither for the full matrix.");
        Configuration[] cases = buffer is { } selected
            ? new[] { new Configuration(selected, ahead!.Value) }
            : [new(1, false), new(4, true), new(8, false), new(1, true), new(4, false), new(8, true)];
        return new(args[0], passes, cases, probe, output);
    }

    private sealed record Configuration(int MiB, bool ReadAhead);
    private sealed record Options(string Volume, int Passes, Configuration[] Cases, bool Probe, string? Output);
    private sealed record Accounting(long Files, long Names, long Directories, long LogicalBytes, long AllocatedBytes,
        string RecordHashXor, string RecordHashSum);
    private sealed class Sample
    {
        public int Pass { get; init; }
        public int BufferMiB { get; init; }
        public bool ReadAhead { get; init; }
        public DateTime StartedUtc { get; init; }
        public string Outcome { get; set; } = "Running";
        public string? Error { get; set; }
        public string? ErrorType { get; set; }
        public int? Win32Error { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public double QueryMilliseconds { get; set; }
        public double ConsumerMilliseconds { get; set; }
        public double FirstBlockDeliveryMilliseconds { get; set; }
        public double? CancellationDrainMilliseconds { get; set; }
        public long QueryCount { get; set; }
        public long BatchCount { get; set; }
        public long ReturnedBytes { get; set; }
        public long ManagedAllocatedBytes { get; set; }
        public long StartWorkingSetBytes { get; init; }
        public long EndWorkingSetBytes { get; set; }
        public long SampledPeakWorkingSetBytes { get; set; }
        public long ProcessLifetimePeakWorkingSetBytes { get; set; }
        public Accounting? Accounting { get; set; }
        public bool? MatchesFirstCompleteAccounting { get; set; }
    }
}
