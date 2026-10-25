using System.Diagnostics;
using System.Security.Cryptography;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Photos;

internal sealed class PhotosService(IExtensionHost host, PhotoCatalog catalog)
{
    // Own the worker here so callers never perform synchronous native/catalog
    // work on the dispatcher. Do not cancel scheduling: accepted refreshes must
    // revoke old content authorization even with an already-cancelled token.
    public Task DiscoverAsync(PhotoCollection collection, IProgress<ExtensionJobProgress>? progress, CancellationToken token) =>
        Task.Run(() => DiscoverCoreAsync(collection, progress, token));

    private async Task DiscoverCoreAsync(PhotoCollection collection, IProgress<ExtensionJobProgress>? progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var initializing = Stopwatch.GetTimestamp();
        var generation = Guid.NewGuid().ToString("N");
        // An accepted refresh always revokes prior content authorization, even
        // when cancellation arrives before the worker gets CPU time.
        catalog.BeginDiscovery(collection.Id, generation);
        var job = catalog.StartJob(collection.Id, "Discovery");
        var initializationTicks = Stopwatch.GetTimestamp() - initializing;
        long count = 0, unresolved = 0; var complete = false; var resolvedAll = true; var observedCoverage = false;
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new NativeMediaFile.DiscoveryReader();
        var observations = new List<PhotoObservation>(512);
        long inventoryTicks = 0, metadataTicks = 0, catalogTicks = initializationTicks, batches = 0, commits = 0, committed = 0;
        long lastCommitMs = 0, lastReportMs = 0;
        var state = "Failed";
        void Flush()
        {
            if (observations.Count == 0) return;
            token.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            try { catalog.Observe(collection.Id, generation, observations); }
            finally { catalogTicks += Stopwatch.GetTimestamp() - started; }
            committed += observations.Count; commits++; observations.Clear(); lastCommitMs = watch.ElapsedMilliseconds;
        }
        try
        {
            token.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(collection.Root);
            var data = Path.GetFullPath(host.DataDirectory);
            var cache = Path.GetFullPath(host.CacheDirectory);
            var waiting = Stopwatch.GetTimestamp();
            await foreach (var batch in host.Inventory.ReadAsync(collection.Root, token).ConfigureAwait(false))
            {
                inventoryTicks += Stopwatch.GetTimestamp() - waiting; batches++;
                token.ThrowIfCancellationRequested();
                // Streaming discovery publishes completeness only on its final batch.
                observedCoverage = true; complete = batch.Coverage.Complete;
                foreach (var warning in batch.Coverage.Warnings) if (warnings.Count < 8) warnings.Add(warning);
                foreach (var item in batch.Items)
                {
                    token.ThrowIfCancellationRequested();
                    if (item.IsDirectory || PhotoKinds.Role(item.Name) is not { } role) continue;
                    var path = host.Inventory.ResolveLocation(item.Reference);
                    if (path is null) { resolvedAll = false; unresolved++; continue; }
                    path = Path.GetFullPath(path);
                    if (!Within(root, path)) { resolvedAll = false; unresolved++; if (warnings.Count < 8) warnings.Add("An inventory location was outside the selected collection and was skipped."); continue; }
                    if (Within(data, path) || Within(cache, path)) continue;
                    var inspecting = Stopwatch.GetTimestamp();
                    try
                    {
                        var revision = reader.Read(path);
                        observations.Add(new(path, revision.Identity, revision.Key, revision.Length, item.LogicalBytes,
                            batch.Coverage.HasAllocationData && item.IsPrimaryName ? item.AllocatedBytes : null, revision.Links, revision.ContentAvailable,
                            revision.ContentAvailable ? "Metadata observed · Analyze to verify content" : "Metadata only · Offline/reparse preview unavailable", role, PhotoKinds.Stem(path)));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        unresolved++;
                        observations.Add(new(path, PhotoCatalog.UnknownIdentity(path), $"unverified:{generation}", null,
                            item.LogicalBytes, batch.Coverage.HasAllocationData && item.IsPrimaryName ? item.AllocatedBytes : null, 0, false,
                            "Metadata only · Current identity could not be verified", role, PhotoKinds.Stem(path)));
                    }
                    finally { metadataTicks += Stopwatch.GetTimestamp() - inspecting; }
                    count++;
                    if (observations.Count == 512) Flush();
                    // Also publish bounded partial batches during long sparse
                    // inventories, without one transaction per inventory batch.
                    if (watch.ElapsedMilliseconds - lastCommitMs >= 1000) Flush();
                }
                if (watch.ElapsedMilliseconds - lastCommitMs >= 1000) Flush();
                if (watch.ElapsedMilliseconds - lastReportMs >= 1000)
                {
                    var started = Stopwatch.GetTimestamp();
                    catalog.JobState(job, "Running", count); catalogTicks += Stopwatch.GetTimestamp() - started;
                    progress?.Report(new("Discovering photo metadata", count, Message: $"{unresolved:N0} unavailable identities"));
                    lastReportMs = watch.ElapsedMilliseconds;
                }
                waiting = Stopwatch.GetTimestamp();
            }
            inventoryTicks += Stopwatch.GetTimestamp() - waiting;
            Flush(); token.ThrowIfCancellationRequested();
            complete &= observedCoverage && resolvedAll;
            var finishing = Stopwatch.GetTimestamp();
            catalog.EndDiscovery(collection.Id, generation, complete, complete ? $"Discovered · {unresolved:N0} metadata-only files" : "Incomplete coverage · Older locations retained", warnings.Count == 0 ? null : string.Join("\n", warnings));
            state = complete ? "Completed" : "NeedsAttention";
            catalog.JobState(job, state, count, complete ? null : "Discovery did not cover every location.");
            catalogTicks += Stopwatch.GetTimestamp() - finishing;
            progress?.Report(new("Photo discovery complete", count, Message: $"{unresolved:N0} unavailable identities", NeedsAttention: !complete));
        }
        catch (Exception error)
        {
            state = error is OperationCanceledException ? "Interrupted" : "Failed";
            complete = false;
            catalog.EndDiscovery(collection.Id, generation, false, error is OperationCanceledException ? "Interrupted discovery · Refresh to validate coverage" : "Discovery failed · Older locations retained");
            catalog.JobState(job, state, count, "Discovery did not complete.");
            throw;
        }
        finally
        {
            watch.Stop();
            host.Log("photos-discovery", new
            {
                collection.Id, Files = count, Committed = committed, Unresolved = unresolved, Complete = complete, State = state,
                ElapsedMs = watch.Elapsed.TotalMilliseconds, FilesPerSecond = count / Math.Max(watch.Elapsed.TotalSeconds, 0.001),
                InventoryWaitMs = inventoryTicks * 1000d / Stopwatch.Frequency, MetadataMs = metadataTicks * 1000d / Stopwatch.Frequency,
                CatalogMs = catalogTicks * 1000d / Stopwatch.Frequency, CatalogInitializationMs = initializationTicks * 1000d / Stopwatch.Frequency,
                InventoryBatches = batches, CatalogCommits = commits,
                reader.ParentHandleOpens, reader.ParentChecks, reader.PeakParentHandles, reader.FilesInspected, reader.UncachedFiles
            });
        }
    }

    private static bool Within(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "." || !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public Task AnalyzeAsync(PhotoCollection collection, IProgress<ExtensionJobProgress>? progress, CancellationToken token) =>
        host.Jobs.RunAsync("photos-analyze", JobResource.Content, context => AnalyzeCoreAsync(collection, context), progress, token);

    internal async Task AnalyzeCoreAsync(PhotoCollection collection, IExtensionJobContext context)
    {
        var job = catalog.StartJob(collection.Id, "Analysis"); long count = 0, hashed = 0, failures = 0, bytes = 0;
        var watch = Stopwatch.StartNew(); long lastCheckpointMs = 0;
        void CheckpointProgress()
        {
            if (watch.ElapsedMilliseconds - lastCheckpointMs < 1000) return;
            catalog.JobState(job, "Running", count + hashed); lastCheckpointMs = watch.ElapsedMilliseconds;
        }
        try
        {
            string? after = null;
            while (true)
            {
                await context.CheckpointAsync(); context.Cancellation.ThrowIfCancellationRequested();
                var batch = catalog.AnalysisBatch(collection.Id, after, token: context.Cancellation);
                if (batch.Count == 0) break;
                foreach (var chunk in batch.Chunk(16))
                {
                    await context.CheckpointAsync(); context.Cancellation.ThrowIfCancellationRequested();
                    var leases = new List<(PhotoRow Row, NativeMediaFile File)>(16);
                    var updates = new List<PhotoAnalysisUpdate>(16);
                    try
                    {
                        foreach (var row in chunk)
                        {
                            await context.CheckpointAsync(); context.Cancellation.ThrowIfCancellationRequested();
                            try
                            {
                                var lease = NativeMediaFile.Open(row.Path, true);
                                if (lease.Revision.Identity != row.ObjectId) { lease.Dispose(); throw new IOException("The location now belongs to a different file. Refresh this collection."); }
                                leases.Add((row, lease));
                            }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                            { updates.Add(new(row.ObjectId, row.Revision, null, null, null, row.ContentAvailable ? "Unresolved · File changed or cannot be read; Refresh to validate" : "Metadata only · Offline/reparse content unavailable")); failures++; }
                        }
                        if (leases.Count > 0)
                        {
                            IReadOnlyList<MediaToolMetadata> metadata;
                            try { metadata = await host.Tools.ReadExifMetadataAsync(leases.Select(x => x.Row.Path).ToArray(), context.Cancellation); }
                            catch (Exception error) when (error is not OperationCanceledException)
                            {
                                host.Log("photos-metadata-unavailable", new { Error = error.GetType().Name });
                                metadata = [];
                            }
                            foreach (var pair in leases)
                            {
                                context.Cancellation.ThrowIfCancellationRequested();
                                try
                                {
                                    pair.File.VerifyUnchanged();
                                    var details = metadata.FirstOrDefault(x => string.Equals(x.Path, pair.Row.Path, StringComparison.Ordinal));
                                    if (details is null || details.Error is not null) failures++;
                                    updates.Add(new(pair.Row.ObjectId, pair.Row.Revision, pair.File.Revision, details, null,
                                        details is { Error: null } ? "Metadata verified · Default stream not compared" : "Default size verified · ExifTool metadata unavailable"));
                                }
                                catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
                                { updates.Add(new(pair.Row.ObjectId, pair.Row.Revision, null, null, null, "Unresolved · File changed during metadata analysis")); failures++; }
                            }
                        }
                        failures += updates.Count - catalog.StoreAnalysisBatch(updates, context.Cancellation);
                        count += chunk.Length; CheckpointProgress();
                        context.Report(new("Inspecting metadata", count, BytesRead: bytes, Message: $"{failures:N0} unavailable or unresolved"));
                    }
                    finally { foreach (var pair in leases) pair.File.Dispose(); }
                }
                after = batch[^1].ObjectId;
            }
            after = null;
            var update = Stopwatch.StartNew();
            while (true)
            {
                await context.CheckpointAsync(); context.Cancellation.ThrowIfCancellationRequested();
                var batch = catalog.AnalysisBatch(collection.Id, after, true, context.Cancellation);
                if (batch.Count == 0) break;
                foreach (var chunk in batch.Chunk(16))
                {
                    await context.CheckpointAsync(); context.Cancellation.ThrowIfCancellationRequested();
                    var leases = new List<NativeMediaFile>(16);
                    var updates = new List<PhotoAnalysisUpdate>(16);
                    long chunkHashed = 0;
                    try
                    {
                        foreach (var row in chunk)
                        {
                            await context.CheckpointAsync(); context.Cancellation.ThrowIfCancellationRequested();
                            NativeMediaFile? lease = null;
                            try
                            {
                                lease = NativeMediaFile.Open(row.Path, true);
                                if (lease.Revision.Identity != row.ObjectId || lease.Revision.Key != row.Revision)
                                    throw new IOException("The candidate changed after metadata inspection.");
                                var hash = await HashAsync(lease, context, read =>
                                {
                                    bytes += read;
                                    if (update.ElapsedMilliseconds < 1000) return;
                                    context.Report(new("Comparing full default streams", hashed + chunkHashed, BytesRead: bytes, Message: $"{failures:N0} unavailable or unresolved")); update.Restart();
                                });
                                lease.VerifyUnchanged();
                                updates.Add(new(row.ObjectId, row.Revision, lease.Revision, null, hash, "Default stream verified", true));
                                leases.Add(lease); lease = null; chunkHashed++;
                            }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                            { updates.Add(new(row.ObjectId, row.Revision, null, null, null, "Unresolved · Default stream could not be stably verified")); failures++; }
                            finally { lease?.Dispose(); }
                        }
                        // Keep successful source leases stable until the bounded
                        // transaction publishes every result in this chunk.
                        failures += updates.Count - catalog.StoreAnalysisBatch(updates, context.Cancellation);
                        hashed += chunkHashed; CheckpointProgress();
                        context.Report(new("Comparing full default streams", hashed, BytesRead: bytes, Message: $"{failures:N0} unavailable or unresolved"));
                    }
                    finally { foreach (var lease in leases) lease.Dispose(); }
                }
                after = batch[^1].ObjectId;
            }
            catalog.JobState(job, failures > 0 ? "NeedsAttention" : "Completed", count + hashed, failures > 0 ? $"{failures} unavailable or unresolved objects." : null);
            host.Log("photos-analysis", new { collection.Id, Inspected = count, Hashed = hashed, BytesRead = bytes, Failures = failures, ElapsedMs = watch.ElapsedMilliseconds });
            context.Report(new("Analysis complete", count, BytesRead: bytes, Message: $"{hashed:N0} candidates hashed · {failures:N0} unavailable or unresolved", NeedsAttention: failures > 0));
        }
        catch (Exception error)
        {
            catalog.JobState(job, error is OperationCanceledException ? "Interrupted" : "Failed", count + hashed, "Analyze revalidates inputs before continuing.");
            throw;
        }
    }

    internal static async Task<string> HashAsync(NativeMediaFile file, IExtensionJobContext context, Action<int>? read = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long consumed = 0;
        while (true)
        {
            await context.CheckpointAsync(); context.Cancellation.ThrowIfCancellationRequested();
            var count = file.Stream!.Read(buffer);
            if (count == 0) break;
            hash.AppendData(buffer, 0, count); consumed += count; read?.Invoke(count);
        }
        if (consumed != file.Revision.Length) throw new IOException("The read did not cover the complete default file stream.");
        file.VerifyUnchanged(); return Convert.ToHexString(hash.GetHashAndReset());
    }
}
