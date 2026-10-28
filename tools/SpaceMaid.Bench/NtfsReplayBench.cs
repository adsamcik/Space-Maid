using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SpaceMaid.Core.Scanning;

// Deterministic, synthetic Windows layout records. Preparation is outside the
// measurement; neither a raw-volume handle nor administrator access is needed.
internal static class NtfsReplayBench
{
    private const int FileCount = 1_000_000;
    private const int BatchSize = 1024;

    public static void Run()
    {
        object whole = Measure(names: true);
        object scoped = Measure(names: false);
        Console.WriteLine(JsonSerializer.Serialize(new { FileCount, BatchSize, WholeVolumeHost = whole, ScopedParsing = scoped,
            Scope = "Synthetic metadata replay only. Whole-volume mode includes parsing, interactive ranking and aggregation; scoped mode creates one temporary parser per batch. Excludes native queries, IPC, namespace validation, analysis and UI. One warmup and five samples per mode; fixture preparation and explicit between-sample GC are untimed." },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Measure(bool names)
    {
        var batches = new List<byte[]>();
        for (int first = 0; first < FileCount; first += BatchSize)
            batches.Add(CreateBatch(first, Math.Min(BatchSize, FileCount - first), names));
        Execute(); // JIT and allocation warmup.
        var samples = new List<object>();
        for (int sample = 0; sample < 5; sample++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            Execute();
            timer.Stop();
            samples.Add(new { Milliseconds = timer.Elapsed.TotalMilliseconds,
                AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before });
        }
        return samples;

        void Execute()
        {
            if (names)
            {
                var builder = new ScanBuilder(trackPreliminary: true) { AllStreams = true, CompleteVolumeLinks = true };
                builder.RootIndex = builder.AddFile(new(new(5), FileAttributes.Directory, 0, 0, 0));
                foreach (byte[] batch in batches) LayoutParser.Parse(batch, builder);
                var scan = builder.Build(@"C:\", "Synthetic NTFS replay", TimeSpan.Zero, 0, default);
                if (scan.FileCount != FileCount + 1 || scan.NameCount != FileCount || scan.WarningCount != 0
                    || scan.LogicalBytes != FileCount * 9216L || scan.AllocatedBytes != FileCount * 8192L
                    || builder.Preliminary(@"C:\").LargestFiles.Count != 100)
                    throw new InvalidDataException("Replay accounting changed.");
            }
            else
            {
                long files = 0, logical = 0, allocated = 0;
                foreach (byte[] batch in batches)
                {
                    var builder = new ScanBuilder();
                    LayoutParser.Parse(batch, builder);
                    files += builder.Count;
                    foreach (var file in builder.Files) { logical += file.LogicalBytes; allocated += file.AllocatedBytes; }
                }
                if (files != FileCount || logical != FileCount * 9216L || allocated != FileCount * 8192L)
                    throw new InvalidDataException("Scoped replay accounting changed.");
            }
        }
    }

    private static byte[] CreateBatch(int first, int count, bool names)
    {
        int size = names ? 224 : 160;
        byte[] batch = new byte[16 + count * size];
        Put32(0, (uint)count); Put32(4, 16); Put32(8, 1);
        for (int i = 0; i < count; i++)
        {
            int file = 16 + i * size, stream = file + (names ? 104 : 40);
            Put32(file, 1); Put32(file + 4, i + 1 == count ? 0 : (uint)size);
            Put32(file + 12, (uint)FileAttributes.Normal); Put64(file + 16, (ulong)(first + i + 16));
            Put32(file + 28, (uint)(stream - file)); Put32(file + 32, (uint)(size - 24)); Put32(file + 36, 24);
            if (names)
            {
                Put32(file + 24, 40); Put32(file + 44, 1); Put64(file + 48, 5);
                string name = $"file-{first + i:D10}.dat";
                Put32(file + 56, (uint)(name.Length * 2));
                Encoding.Unicode.GetBytes(name, batch.AsSpan(file + 64));
            }
            Put32(stream, 2); Put32(stream + 4, 48); Put64(stream + 16, 4096); Put64(stream + 24, 8192); Put32(stream + 36, 0x80);
            Put32(stream + 48, 2); Put64(stream + 64, 4096); Put64(stream + 72, 1024); Put32(stream + 84, 0x80);
        }
        return batch;

        void Put32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(offset), value);
        void Put64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(batch.AsSpan(offset), value);
    }
}
