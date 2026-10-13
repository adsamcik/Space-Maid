using System.Windows;

namespace SpaceMaid.Extensions.Contracts;

public static class ExtensionApi { public const int Version = 1; }
public sealed record ExtensionDescriptor(string Id, string Name, string Version, int ContractVersion = ExtensionApi.Version);
public interface ISpaceMaidExtension : IAsyncDisposable
{
    ExtensionDescriptor Descriptor { get; }
    Task InitializeAsync(IExtensionHost host, CancellationToken token);
    FrameworkElement? CreateView();
}

public interface IExtensionHost
{
    string DataDirectory { get; }
    string CacheDirectory { get; }
    IInventoryReader Inventory { get; }
    IExtensionJobs Jobs { get; }
    IToolBroker Tools { get; }
    void RevealLocation(string path);
    void Log(string eventName, object data);
}

public readonly record struct InventoryReference(Guid Generation, int EntryIndex);
public sealed record InventoryItem(InventoryReference Reference, int ObjectIndex, string Name, bool IsDirectory,
    long LogicalBytes, long AllocatedBytes, bool IsPrimaryName, uint Attributes, long LastWriteFileTime,
    ulong? NativeIdLow, ulong? NativeIdHigh);
public sealed record InventoryCoverage(string Root, DateTime ObservedAtUtc, bool Complete, bool HasAllocationData,
    bool IncludesAllStreams, bool CompleteVolumeLinks, IReadOnlyList<string> Warnings);
public sealed record InventoryBatch(InventoryCoverage Coverage, IReadOnlyList<InventoryItem> Items);
public interface IInventoryReader
{
    // References belong to this reader's current batch and expire on its next
    // MoveNextAsync or disposal. Other readers do not invalidate them. Scan
    // replacement may invalidate cached handles. Resolve while processing the
    // batch; persist catalog identity and location, never this handle.
    IAsyncEnumerable<InventoryBatch> ReadAsync(string root, CancellationToken token, bool preferExistingInventory = false);
    string? ResolveLocation(InventoryReference reference);
}

// Optional API v1 capabilities. Existing extensions and inventory readers do
// not need to implement these; mutable scanner storage stays inside the host.
public sealed record InventoryFolderSize(long LogicalBytes, long? AllocatedBytes, bool Complete,
    DateTime ObservedAtUtc, string Source, IReadOnlyList<string> Warnings);
public interface IInventoryFolderSizes
{
    // Without refresh, return a current scan summary or null without scanning.
    // With refresh, measure current folder metadata through the host's available backend.
    Task<InventoryFolderSize?> ReadFolderSizeAsync(string path, bool refresh, CancellationToken token);
}
public interface IDriveExtensionView
{
    void SetDrive(string root);
}

public enum JobResource { Content, Thumbnail }
public enum ExtensionJobState { Queued, Running, Completed, Failed, Cancelled, NeedsAttention }
public sealed record ExtensionJobProgress(string Phase, long Completed, long? Total = null, long BytesRead = 0,
    string? Message = null, bool NeedsAttention = false);
public interface IExtensionJobContext
{
    CancellationToken Cancellation { get; }
    Task CheckpointAsync();
    void Report(ExtensionJobProgress progress);
}
public interface IExtensionJobs
{
    Task RunAsync(string workflow, JobResource resource, Func<IExtensionJobContext, Task> work,
        IProgress<ExtensionJobProgress>? progress, CancellationToken token);
}

public sealed record MediaToolMetadata(string Path, string? Format, int? Width, int? Height, int? Orientation,
    string? CaptureTime, string ToolVersion, string? Error = null);
public interface IToolBroker
{
    // Fixed read-only adapter; extensions cannot submit shell strings or
    // arbitrary command options. Failure/missing prerequisites stay explicit.
    Task<IReadOnlyList<MediaToolMetadata>> ReadExifMetadataAsync(IReadOnlyList<string> paths, CancellationToken token);
    Task<string?> ExifToolVersionAsync(CancellationToken token);
}
