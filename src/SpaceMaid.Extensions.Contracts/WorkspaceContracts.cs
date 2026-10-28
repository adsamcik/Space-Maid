namespace SpaceMaid.Extensions.Contracts;

// Optional API v1 capabilities. A CreateView() result is automatically available
// in the main workspace; existing extensions need no changes.
public sealed record ExtensionViewContext(string FolderPath, string? DriveRoot,
    bool HasInventory, bool InventoryStale, bool IsBusy);

public interface IExtensionContextView
{
    // Delivered on the UI thread at startup and when host context changes.
    // Context is informational; it never authorizes analysis or cleanup.
    void SetContext(ExtensionViewContext context);
}

public interface IExtensionNavigationHost
{
    IExtensionNavigation? Navigation { get; }
}

public interface IExtensionNavigation
{
    // Safe to call from a worker. False means unavailable, closing or busy.
    Task<bool> OpenExtensionAsync(string extensionId);
    // Uses the normal folder navigation/scan flow and its existing safeguards.
    Task<bool> ExploreFolderAsync(string path);
}
