namespace SpaceMaid.Extensions.Contracts;

public sealed record EvidenceCleanupReviewResult(int MatchedLocations, IReadOnlyList<string> UnmatchedPaths, string Message);

// Optional trusted-view action. The human explicitly selects investigation
// items and asks for review. The host maps only its own issued IDs to existing
// deterministic cleanup rules, then uses its separate live review workflow.
// This interface must never be offered to the model as a tool.
public interface IExtensionEvidenceReview
{
    Task<EvidenceCleanupReviewResult> ReviewEvidenceAsync(IInventoryEvidenceSession session,
        IReadOnlyList<EvidenceId> selectedItems, CancellationToken token);
}
