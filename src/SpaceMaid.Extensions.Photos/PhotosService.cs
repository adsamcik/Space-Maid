using System.Diagnostics;
using System.Security.Cryptography;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Photos;

internal sealed class PhotosService(IExtensionHost host, PhotoCatalog catalog)
{
    public async Task DiscoverAsync(PhotoCollection collection, IProgress<ExtensionJobProgress>? progress, CancellationToken token)
    {
        var generation = Guid.NewGuid().ToString("N");
        // An accepted refresh always revokes prior content authorization, even
        // when cancellation arrives before the worker gets CPU time.
        var job = await Task.Run(() => { catalog.BeginDiscovery(collection.Id, generation); return catalog.StartJob(collection.Id, "Discovery"); });
        long count = 0, unresolved = 0; var complete = false; var resolvedAll = true; var observedCoverage = false;
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            await foreach (var batch in host.Inventory.ReadAsync(collection.Root, token))
            {
                token.ThrowIfCancellationRequested();
                // Streaming discovery publishes completeness only on its final batch.
                observedCoverage = true; complete = batch.Coverage.Complete;
                foreach (var warning in batch.Coverage.Warnings) if (warnings.Count < 8) warnings.Add(warning);
                var observations = new List<PhotoObservation>(Math.Min(512, batch.Items.Count));
                foreach (var item in batch.Items)
                {
                    token.ThrowIfCancellationRequested();
                    if (item.IsDirectory || PhotoKinds.Role(item.Name) is not { } role) continue;
                    var path = host.Inventory.ResolveLocation(item.Reference);
                    if (path is null) { resolvedAll = false; unresolved++; continue; }
                    if (!Within(collection.Root, path)) { resolvedAll = false; unresolved++; if (warnings.Count < 8) warnings.Add("An inventory location was outside the selected collection and was skipped."); continue; }
                    if (Within(host.DataDirectory, path) || Within(host.CacheDirectory, path)) continue;
                    try
                    {
                        using var file = NativeMediaFile.Open(path, false);
                        var revision = file.Revision;
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
                    count++;
                    if (observations.Count == 512)
                    {
                        await Task.Run(() => catalog.Observe(collection.Id, generation, observations), token);
                        observations = new(512);
                    }
                }
                if (observations.Count != 0) await Task.Run(() => catalog.Observe(collection.Id, generation, observations), token);
                await Task.Run(() => catalog.JobState(job, "Running", count), token);
                progress?.Report(new("Discovering photo metadata", count, Message: $"{unresolved:N0} unavailable identities"));
            }
            complete &= observedCoverage && resolvedAll;
            await Task.Run(() =>
            {
                catalog.EndDiscovery(collection.Id, generation, complete, complete ? $"Discovered · {unresolved:N0} metadata-only files" : "Incomplete coverage · Older locations retained", warnings.Count == 0 ? null : string.Join("\n", warnings));
                catalog.JobState(job, complete ? "Completed" : "NeedsAttention", count, complete ? null : "Discovery did not cover every location.");
            });
            host.Log("photos-discovery", new { collection.Id, Files = count, Unresolved = unresolved, Complete = complete, ElapsedMs = watch.ElapsedMilliseconds });
        }
        catch (Exception error)
        {
            await Task.Run(() =>
            {
                catalog.EndDiscovery(collection.Id, generation, false, error is OperationCanceledException ? "Interrupted discovery · Refresh to validate coverage" : "Discovery failed · Older locations retained");
                catalog.JobState(job, error is OperationCanceledException ? "Interrupted" : "Failed", count, "Discovery did not complete.");
            });
            throw;
        }
    }

    private static bool Within(string directory, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return relative == "." || !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public Task AnalyzeAsync(PhotoCollection collection, IProgress<ExtensionJobProgress>? progress, CancellationToken token) =>
        host.Jobs.RunAsync("photos-analyze", JobResource.Content, context => AnalyzeCoreAsync(collection, context), progress, token);

    internal async Task AnalyzeCoreAsync(PhotoCollection collection, IExtensionJobContext context)
    {
        var job = catalog.StartJob(collection.Id, "Analysis"); long count = 0, failures = 0, bytes = 0; var watch = Stopwatch.StartNew();
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
                            { catalog.MarkUnresolved(row.ObjectId, row.ContentAvailable ? "Unresolved · File changed or cannot be read; Refresh to validate" : "Metadata only · Offline/reparse content unavailable"); failures++; }
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
                                    catalog.StoreAnalysis(pair.Row.ObjectId, pair.File.Revision, details, null,
                                        details is { Error: null } ? "Metadata verified · Default stream not compared" : "Default size verified · ExifTool metadata unavailable");
                                }
                                catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
                                { catalog.MarkUnresolved(pair.Row.ObjectId, "Unresolved · File changed during metadata analysis"); failures++; }
                            }
                        }
                        count += chunk.Length; catalog.JobState(job, "Running", count);
                        context.Report(new("Inspecting metadata", count, BytesRead: bytes, Message: $"{failures:N0} unavailable or unresolved"));
                    }
                    finally { foreach (var pair in leases) pair.File.Dispose(); }
                }
                after = batch[^1].ObjectId;
            }
            after = null; long hashed = 0;
            var update = Stopwatch.StartNew();
            while (true)
            {
                await context.CheckpointAsync(); context.Cancellation.ThrowIfCancellationRequested();
                var batch = catalog.AnalysisBatch(collection.Id, after, true, context.Cancellation);
                if (batch.Count == 0) break;
                foreach (var row in batch)
                {
                    await context.CheckpointAsync(); context.Cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        using var lease = NativeMediaFile.Open(row.Path, true);
                        if (lease.Revision.Identity != row.ObjectId || lease.Revision.Key != row.Revision)
                            throw new IOException("The candidate changed after metadata inspection.");
                        var hash = await HashAsync(lease, context, read =>
                        {
                            bytes += read;
                            if (update.ElapsedMilliseconds < 1000) return;
                            context.Report(new("Comparing full default streams", hashed, BytesRead: bytes, Message: $"{failures:N0} unavailable or unresolved")); update.Restart();
                        });
                        lease.VerifyUnchanged(); catalog.StoreHash(row.ObjectId, lease.Revision, hash); hashed++;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    { catalog.MarkUnresolved(row.ObjectId, "Unresolved · Default stream could not be stably verified"); failures++; }
                    context.Report(new("Comparing full default streams", hashed, BytesRead: bytes, Message: $"{failures:N0} unavailable or unresolved"));
                    catalog.JobState(job, "Running", count + hashed);
                }
                after = batch[^1].ObjectId;
            }
            catalog.JobState(job, failures > 0 ? "NeedsAttention" : "Completed", count + hashed, failures > 0 ? $"{failures} unavailable or unresolved objects." : null);
            host.Log("photos-analysis", new { collection.Id, Inspected = count, Hashed = hashed, BytesRead = bytes, Failures = failures, ElapsedMs = watch.ElapsedMilliseconds });
            context.Report(new("Analysis complete", count, BytesRead: bytes, Message: $"{hashed:N0} candidates hashed · {failures:N0} unavailable or unresolved", NeedsAttention: failures > 0));
        }
        catch (Exception error)
        {
            catalog.JobState(job, error is OperationCanceledException ? "Interrupted" : "Failed", count, "Analyze revalidates inputs before continuing.");
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
