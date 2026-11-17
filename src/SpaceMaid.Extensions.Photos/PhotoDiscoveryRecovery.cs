namespace SpaceMaid.Extensions.Photos;

// Namespace coverage is independent of whether content comparison completed.
// An empty filtered page cannot establish absence when discovery was incomplete.
internal sealed record PhotoDiscoveryRecovery(string Title, string Message, string Badge)
{
    internal static PhotoDiscoveryRecovery? For(PhotoCollection collection, PhotoJobStatus? lastJob)
    {
        var discovery = lastJob?.Kind == "Discovery" ? lastJob : null;
        PhotoDiscoveryRecovery? recovery = collection.Status == "Not discovered"
            ? new("Photos haven't been checked", "Refresh this folder to discover photo filenames without reading photo contents.", "Not discovered")
            : collection.Status.Contains("Interrupted", StringComparison.OrdinalIgnoreCase) || discovery?.State == "Interrupted"
                ? new("Discovery interrupted", "Some locations were not checked. Refresh this folder to validate coverage.", "Interrupted")
            : collection.Status.Contains("failed", StringComparison.OrdinalIgnoreCase) || discovery?.State == "Failed"
                ? new("Folder could not be fully checked", "Check that the folder is connected and accessible, then refresh.", "Needs attention")
            : collection.Status.StartsWith("Incomplete", StringComparison.OrdinalIgnoreCase) || discovery?.State == "NeedsAttention"
                ? new("Discovery incomplete", "Some locations were not checked. Refresh to retry the unavailable locations.", "Incomplete")
            : collection.Status == "Discovering" || discovery?.State == "Running"
                ? new("Discovery unfinished", "The previous discovery has not completed. Refresh this folder to validate coverage.", "Interrupted")
            : null;
        if (recovery is null) return null;
        var details = collection.Coverage ?? discovery?.Message;
        return recovery with { Message = recovery.Message
            + (collection.Count > 0 ? " Earlier indexed locations remain in Browse photos; their content needs revalidation." : " This does not establish that the folder is empty.")
            + (string.IsNullOrWhiteSpace(details) ? "" : "\n" + details) };
    }
}
