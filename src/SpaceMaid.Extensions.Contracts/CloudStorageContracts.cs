using SpaceMaid.Core.Scanning;

namespace SpaceMaid.Extensions.Contracts;

// Optional API v1 capability. The host owns live provider checks and the
// confirmation dialog; an extension cannot execute a prepared request itself.
public interface ICloudStorageHost
{
    ICloudStorageServices? CloudStorage { get; }
}

public sealed record CloudStorageReviewResult(string Message, bool RequestsAccepted);

public interface ICloudStorageServices
{
    Task<CloudDirectoryListing> ReadDirectoryAsync(string path, CancellationToken token);
    Task<CloudStorageReviewResult> ReviewOnlineOnlyAsync(string path, CancellationToken token);
}
