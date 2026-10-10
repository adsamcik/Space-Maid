using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO.Enumeration;

namespace SpaceMaid.Core.Scanning;

public sealed class VolumeScanner
{
    public Task<ScanSnapshot> ScanAsync(string path, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default, bool preferFast = true, Action<ScanDiagnostic>? diagnostic = null,
        INtfsLayoutSource? layoutSource = null, string? fastUnavailableReason = null) =>
        Task.Run(() => Scan(path, progress, cancellationToken, preferFast, diagnostic, layoutSource, fastUnavailableReason), cancellationToken);

    // Parent rechecks read immediate names only, including at a drive root.
    internal Task<ScanSnapshot> ReadDirectoryAsync(string path, bool recursive, CancellationToken token, bool allStreams = false) => Task.Run(() =>
    {
        var timer = Stopwatch.StartNew();
        long allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var builder = new ScanBuilder();
        var reporter = new ScanReporter(null, null, timer, allocatedBefore, path);
        AcquireDirectories(path, builder, reporter, token, recursive, validateIdentity: true);
        var inventory = builder.Build(path, "Targeted Windows metadata", timer.Elapsed, allocatedBefore, token);
        if (allStreams)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                for (int i = 0; i < inventory.Records.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    string filePath = inventory.EntryPath(inventory.PrimaryLink(i)) ?? throw new IOException("A refreshed file path is unresolved.");
                    builder.ReplaceMetadata(ReadStreamMetadata(filePath, inventory.Records[i], ref buffer));
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            builder.AllStreams = true;
            return builder.Build(path, "Targeted Windows stream metadata", timer.Elapsed, allocatedBefore, token);
        }
        return inventory;
    }, token);

    // Attribute-only handles enumerate all DATA stream sizes, including named
    // and sparse streams. Hold the identity while querying; never read content.
    private static unsafe FileRecord ReadStreamMetadata(string path, FileRecord record, ref byte[] buffer)
    {
        using var handle = NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), 0x80, 3, 0, 3,
            NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        byte[] identity = new byte[24];
        fixed (byte* info = identity)
            if (!NativeMethods.GetFileInformationByHandleEx(handle, 18, info, 24)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (new FileIdentity(LayoutParser.U64(identity, 8), LayoutParser.U64(identity, 16)) != record.Identity)
            throw new IOException("A file changed during its metadata recheck. Recheck the folder again.");
        while (true)
        {
            bool success;
            fixed (byte* info = buffer)
                success = NativeMethods.GetFileInformationByHandleEx(handle, 7, info, (uint)buffer.Length);
            int error = Marshal.GetLastPInvokeError();
            if (success) break;
            if (error == NativeMethods.HandleEof) return record with { LogicalBytes = 0, AllocatedBytes = 0 };
            if (error is 122 or 234 && buffer.Length < 16 * 1024 * 1024)
            {
                int size = buffer.Length * 2;
                ArrayPool<byte>.Shared.Return(buffer); buffer = ArrayPool<byte>.Shared.Rent(size);
                continue;
            }
            throw new Win32Exception(error);
        }
        int offset = 0; long logical = 0, allocated = 0;
        while (true)
        {
            LayoutParser.Require(buffer, offset, 24);
            uint next = LayoutParser.U32(buffer, offset), nameBytes = LayoutParser.U32(buffer, offset + 4);
            int end = next == 0 ? buffer.Length : checked(offset + (int)next);
            if (end > buffer.Length || next != 0 && (next < 24 || next % 8 != 0) || (nameBytes & 1) != 0)
                throw new InvalidDataException("Invalid stream metadata.");
            LayoutParser.Require(buffer.AsSpan(0, end), offset + 24, checked((int)nameBytes));
            var name = MemoryMarshal.Cast<byte, char>(buffer.AsSpan(offset + 24, (int)nameBytes));
            if (name.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase))
            {
                long length = LayoutParser.I64(buffer, offset + 8), allocation = LayoutParser.I64(buffer, offset + 16);
                if (length < 0 || allocation < 0) throw new InvalidDataException("Invalid stream size.");
                logical = checked(logical + length); allocated = checked(allocated + allocation);
            }
            if (next == 0) break;
            offset = end;
        }
        return record with { LogicalBytes = logical, AllocatedBytes = allocated };
    }

    private static ScanSnapshot Scan(string path, IProgress<ScanProgress>? progress,
        CancellationToken token, bool preferFast, Action<ScanDiagnostic>? diagnostic, INtfsLayoutSource? layoutSource, string? fastUnavailableReason)
    {
        string target = Path.GetFullPath(path);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("The selected drive or folder is unavailable.");
        target = Path.TrimEndingDirectorySeparator(target);
        string root = Path.GetPathRoot(target)!;
        var timer = Stopwatch.StartNew();
        long allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var reporter = new ScanReporter(progress, diagnostic, timer, allocatedBefore, target);
        reporter.Event("scan-start", $"Target: {target}; preferFast: {preferFast}");
        string? fallbackReason = fastUnavailableReason;
        ScanBuilder? active = null;
        try
        {
            if (preferFast && string.Equals(target, root, StringComparison.OrdinalIgnoreCase)
                && root.Length == 3 && new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                var fast = new ScanBuilder(progress is not null) { AllStreams = true, CompleteVolumeLinks = true };
                active = fast;
                fast.WarningReported = message => reporter.Event("coverage-warning", message, fast);
                reporter.UseBackend("Fast NTFS scan", "Attempting FSCTL_QUERY_FILE_LAYOUT (0x00090277).", fast);
                try
                {
                    AcquireLayout(root, fast, reporter, token, layoutSource ?? new NativeNtfsLayoutSource());
                    return Finish(fast, "Fast NTFS scan");
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidDataException or IOException)
                {
                    fallbackReason = ex is Win32Exception { NativeErrorCode: 5 }
                        ? "Fast NTFS access was unavailable. Standard scanning was used; administrator access can enable the fast scan."
                        : $"Fast NTFS scanning was unavailable ({ex.Message}). Standard scanning was used.";
                    reporter.Fallback(fallbackReason, fast, (ex as Win32Exception)?.NativeErrorCode);
                    active = null; // The discarded backend must not be republished on cancellation.
                }
            }
            token.ThrowIfCancellationRequested();
            var standard = new ScanBuilder(progress is not null) { FallbackReason = fallbackReason };
            active = standard;
            standard.WarningReported = message => reporter.Event("coverage-warning", message, standard);
            string backend = "Standard Windows scan";
            reporter.UseBackend(backend, fallbackReason ?? "Using directory metadata enumeration; fast mode was not requested or the target is not a whole local NTFS volume.", standard);
            try { AcquireDirectories(target, standard, reporter, token); }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 1 or 50 or 87)
            {
                standard = new ScanBuilder(progress is not null) { AllocationData = false, FallbackReason =
                    (fallbackReason is null ? "" : fallbackReason + " ") +
                    "This filesystem does not provide usable file identities/allocation. Basic logical-size scanning was used." };
                active = standard;
                standard.WarningReported = message => reporter.Event("coverage-warning", message, standard);
                reporter.Fallback(standard.FallbackReason, standard, ex.NativeErrorCode);
                reporter.UseBackend("Basic folder scan", "Using logical-size-only enumeration.", standard);
                AcquireBasic(target, standard, reporter, token);
                backend = "Basic folder scan";
            }
            return Finish(standard, backend);
        }
        catch (OperationCanceledException)
        {
            if (active is not null) reporter.Report("Scan cancelled", active, force: true);
            throw;
        }

        ScanSnapshot Finish(ScanBuilder builder, string actualBackend)
        {
            reporter.Report("Calculating totals", builder, force: true);
            reporter.Event("aggregation-start", "Resolving parents and calculating folder totals.", builder);
            var snapshot = builder.Build(target, actualBackend, timer.Elapsed, allocatedBefore, token);
            reporter.Event("scanner-complete", $"Acquisition: {snapshot.Metrics.Acquisition.TotalMilliseconds:0.###} ms; aggregation: {snapshot.Metrics.Aggregation.TotalMilliseconds:0.###} ms; allocation data: {snapshot.HasAllocationData}; all streams: {snapshot.IncludesAllDataStreams}; complete links: {snapshot.HasCompleteVolumeLinks}.", builder);
            return snapshot;
        }
    }

    private static void AcquireLayout(string root, ScanBuilder builder, ScanReporter reporter, CancellationToken token, INtfsLayoutSource source)
    {
        source.Acquire(root, block =>
        {
            token.ThrowIfCancellationRequested();
            reporter.QueryObserved(block.QueryMilliseconds, block.Data.Length);
            if (block.Data.IsEmpty) return;
            int before = builder.Count;
            long started = Stopwatch.GetTimestamp();
            LayoutParser.Parse(block.Data.Span, builder);
            reporter.ParseCompleted(started);
            if (builder.Count == before) throw new InvalidDataException("The NTFS enumeration made no forward progress.");
            reporter.Report("Reading NTFS metadata", builder);
        }, token);
    }
    private static unsafe void AcquireDirectories(string target, ScanBuilder builder, ScanReporter reporter, CancellationToken token,
        bool recursive = true, bool validateIdentity = false)
    {
        using var rootHandle = OpenDirectory(target);
        if (rootHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        byte[] idBuffer = new byte[24];
        fixed (byte* pointer = idBuffer)
            if (!NativeMethods.GetFileInformationByHandleEx(rootHandle, 18, pointer, 24))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        var rootId = new FileIdentity(LayoutParser.U64(idBuffer, 8), LayoutParser.U64(idBuffer, 16));
        if (rootId == default) throw new Win32Exception(50);
        builder.RootIndex = builder.AddFile(new(rootId, FileAttributes.Directory, 0, 0, 0));
        var pending = new Queue<(string Path, FileIdentity Id)>();
        var visited = new HashSet<FileIdentity> { rootId };
        pending.Enqueue((target, rootId));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (pending.TryDequeue(out var directory))
            {
                token.ThrowIfCancellationRequested();
                using var handle = OpenDirectory(directory.Path);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastPInvokeError();
                    builder.Warn($"Could not read {directory.Path}: Win32 {error}: {new Win32Exception(error).Message}", directory.Id);
                    continue;
                }
                if (validateIdentity)
                {
                    fixed (byte* pointer = idBuffer)
                        if (!NativeMethods.GetFileInformationByHandleEx(handle, 18, pointer, 24)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    if (new FileIdentity(LayoutParser.U64(idBuffer, 8), LayoutParser.U64(idBuffer, 16)) != directory.Id)
                        throw new IOException("A folder moved or was replaced during the recheck. Recheck its parents again.");
                }
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    bool success;
                    long queryStarted = Stopwatch.GetTimestamp();
                    fixed (byte* pointer = buffer)
                        success = NativeMethods.GetFileInformationByHandleEx(handle, 19, pointer, (uint)buffer.Length);
                    int queryError = success ? 0 : Marshal.GetLastPInvokeError();
                    reporter.QueryCompleted(queryStarted, 0); // This API does not return a byte count.
                    if (!success)
                    {
                        int error = queryError;
                        if (error == NativeMethods.NoMoreFiles) break;
                        if (directory.Id == rootId && error is 1 or 50 or 87) throw new Win32Exception(error);
                        builder.Warn($"Could not finish reading {directory.Path}: Win32 {error}: {new Win32Exception(error).Message}", directory.Id);
                        break;
                    }
                    int offset = 0;
                    long parseStarted = Stopwatch.GetTimestamp();
                    while (true)
                    {
                        LayoutParser.Require(buffer, offset, 88);
                        uint next = LayoutParser.U32(buffer, offset);
                        int end = next == 0 ? buffer.Length : checked(offset + (int)next);
                        if (next != 0 && next < 88 || end > buffer.Length) throw new InvalidDataException("Invalid directory metadata.");
                        uint nameBytes = LayoutParser.U32(buffer, offset + 60);
                        if ((nameBytes & 1) != 0) throw new InvalidDataException("Invalid filename length.");
                        LayoutParser.Require(buffer.AsSpan(0, end), offset + 88, checked((int)nameBytes));
                        var name = MemoryMarshal.Cast<byte, char>(buffer.AsSpan(offset + 88, (int)nameBytes));
                        if (!name.SequenceEqual(".") && !name.SequenceEqual(".."))
                        {
                            var id = new FileIdentity(LayoutParser.U64(buffer, offset + 72), LayoutParser.U64(buffer, offset + 80));
                            // Some providers return successful records without usable IDs.
                            // Restart in basic mode rather than merging unrelated files under ID zero.
                            if (id == default) throw new Win32Exception(50);
                            var attributes = (FileAttributes)LayoutParser.U32(buffer, offset + 56);
                            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                            long logical = isDirectory ? 0 : LayoutParser.I64(buffer, offset + 40);
                            long allocation = isDirectory ? 0 : LayoutParser.I64(buffer, offset + 48);
                            if (logical < 0 || allocation < 0) throw new InvalidDataException("Invalid directory size.");
                            int index = builder.AddFile(new(id, attributes, logical, allocation, LayoutParser.I64(buffer, offset + 24)));
                            builder.AddLink(index, directory.Id, name);
                            if (isDirectory && recursive)
                            {
                                uint tag = LayoutParser.U32(buffer, offset + 68);
                                if ((tag & 0x20000000) != 0)
                                    builder.Warn($"Junction/symbolic-link target not followed: {Path.Join(directory.Path, new string(name))}", id);
                                else if (visited.Add(id)) pending.Enqueue((Path.Join(directory.Path, new string(name)), id));
                            }
                        }
                        if (next == 0) break;
                        offset = end;
                    }
                    reporter.ParseCompleted(parseStarted);
                    reporter.Report("Reading folders", builder);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path) =>
        NativeMethods.CreateFile(NativeMethods.ExtendedPath(path), 0x81, 7, 0, 3,
            NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, 0);

    private readonly record struct BasicEntry(string Name, FileAttributes Attributes, long Size, long WriteTime);

    private static void AcquireBasic(string target, ScanBuilder builder, ScanReporter reporter, CancellationToken token)
    {
        ulong sequence = 1;
        var rootId = new FileIdentity(sequence++);
        builder.RootIndex = builder.AddFile(new(rootId, FileAttributes.Directory, 0, 0, 0));
        var pending = new Queue<(string Path, FileIdentity Id)>();
        pending.Enqueue((target, rootId));
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        while (pending.TryDequeue(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var entries = new FileSystemEnumerable<BasicEntry>(directory.Path,
                    (ref FileSystemEntry entry) => new(entry.FileName.ToString(), entry.Attributes,
                        entry.IsDirectory ? 0 : entry.Length, entry.LastWriteTimeUtc.ToFileTime()), options);
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    var id = new FileIdentity(sequence++);
                    int index = builder.AddFile(new(id, entry.Attributes, entry.Size, 0, entry.WriteTime));
                    builder.AddLink(index, directory.Id, entry.Name);
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            builder.Warn($"Reparse directory not followed: {Path.Join(directory.Path, entry.Name)}", id);
                        else pending.Enqueue((Path.Join(directory.Path, entry.Name), id));
                    }
                    reporter.Report("Reading folders", builder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { builder.Warn($"Could not finish reading {directory.Path}: {ex.Message}", directory.Id); }
        }
    }

    private sealed class ScanReporter(IProgress<ScanProgress>? progress, Action<ScanDiagnostic>? diagnostic,
        Stopwatch timer, long allocatedBefore, string rootPath)
    {
        private long _lastUpdate = -250;
        private long _lastPreliminary = -1000;
        private long _lastDiagnostic = -1000, _queries, _batches, _returnedBytes;
        private double _queryMs, _parseMs;
        private string _backend = "Selecting backend";
        private string? _fallback;
        public void QueryCompleted(long started, int bytes) { _queries++; _returnedBytes += bytes; _queryMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
        public void QueryObserved(double milliseconds, int bytes) { _queries++; _returnedBytes += bytes; _queryMs += milliseconds; }
        public void ParseCompleted(long started) { _batches++; _parseMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
        public void UseBackend(string backend, string message, ScanBuilder builder)
        {
            _backend = backend;
            _lastPreliminary = timer.ElapsedMilliseconds;
            Event("backend-selected", message);
            progress?.Report(new("Starting metadata acquisition", 0, timer.Elapsed, _backend, _fallback, builder.Preliminary(rootPath)));
        }
        public void Fallback(string reason, ScanBuilder builder, int? error)
        {
            _fallback = reason;
            Event("backend-fallback", reason, builder, error);
            progress?.Report(new("Switching scanner", 0, timer.Elapsed, _backend, _fallback,
                new(0, 0, builder.AllocationData, Array.Empty<PreliminaryFile>())));
        }
        public void Event(string eventName, string message, ScanBuilder? builder = null, int? error = null)
        {
            if (diagnostic is null) return;
            using var process = Process.GetCurrentProcess();
            diagnostic(new(eventName, _backend, message, timer.ElapsedMilliseconds, builder?.Count ?? 0,
                builder?.NameCount ?? 0, builder?.WarningCount ?? 0, _queries, _batches, _returnedBytes, _queryMs, _parseMs,
                process.WorkingSet64, GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes(false) - allocatedBefore, error));
        }
        public void Report(string phase, ScanBuilder builder, bool force = false)
        {
            if (!force && timer.ElapsedMilliseconds - _lastUpdate < 250) return;
            _lastUpdate = timer.ElapsedMilliseconds;
            ScanPreliminary? preliminary = null;
            if (progress is not null && (force || timer.ElapsedMilliseconds - _lastPreliminary >= 1000))
            {
                _lastPreliminary = timer.ElapsedMilliseconds;
                preliminary = builder.Preliminary(rootPath);
            }
            progress?.Report(new(phase, builder.Count, timer.Elapsed, _backend, _fallback, preliminary));
            if (force || timer.ElapsedMilliseconds - _lastDiagnostic >= 1000)
            { _lastDiagnostic = timer.ElapsedMilliseconds; Event("progress", phase, builder); }
        }
    }
}
