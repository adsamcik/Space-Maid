using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

internal static class OnlineOnlyPresentation
{
    internal static CloudStorageReviewResult Completed(OnlineOnlyResult result)
    {
        string message = $"{result.Requested:N0} online-only requests accepted · {result.ConfirmedOnlineOnly:N0} confirmed online-only · {result.Skipped:N0} skipped";
        message += result.Cancelled
            ? result.Requested > 0 ? " · Cancelled; accepted requests were not withdrawn." : " · Cancelled; no requests were accepted."
            : ".";
        if (result.Requested > result.ConfirmedOnlineOnly) message += " The provider may still be processing accepted requests.";
        return WithReasons(new(message, result.Requested > 0) { Receipt = result }, result.Warnings, result.Skipped);
    }

    internal static CloudStorageReviewResult CompletedWithPreparation(OnlineOnlyResult result, OnlineOnlyPlan plan)
    {
        // Keep the exact execution receipt; preparation warnings describe items
        // that never entered execution and must survive the review as well.
        var completed = Completed(result);
        return WithReasons(completed, plan.Warnings.Concat(result.Warnings), result.Skipped);
    }

    internal static CloudStorageReviewResult NoEligible(OnlineOnlyPlan plan)
        => WithReasons(new($"No eligible synced local files were found · {plan.SkippedCount:N0} skipped. No requests were sent.", false),
            plan.Warnings, plan.SkippedCount);

    private static CloudStorageReviewResult WithReasons(CloudStorageReviewResult result, IEnumerable<string> warnings, int skipped)
    {
        var reasons = warnings.Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct(StringComparer.Ordinal).ToArray();
        if (skipped == 0 && reasons.Length == 0) return result;
        string hint = " Review item details in Details. " + (skipped > 0
            ? "Inspect reported items in Explorer before reviewing again."
            : "Refresh the folder to recheck current local storage.");
        string message = result.Message.EndsWith(hint, StringComparison.Ordinal) ? result.Message : result.Message + hint;
        string details = reasons.Length == 0 ? "No further reason was reported for the skipped items."
            : string.Join("\n", reasons.Take(16).Select(reason => "• " + (reason.Length <= 2048 ? reason : reason[..2048] + "…")));
        if (reasons.Length > 16 || reasons.Any(reason => reason.Length > 2048)) details += "\nFurther diagnostic text is available in the activity log.";
        return result with { Message = message, Details = "Reported item details:\n" + details };
    }

    internal static string Status(CloudStorageReviewResult result)
        => result.Message + (result.Details.Length == 0 ? "" : "\n\n" + result.Details);

    internal static CloudStorageReviewResult RefreshFailed(CloudStorageReviewResult completed, Exception error, bool fullRescan)
        => completed with
        {
            Message = completed.Message + (error is OperationCanceledException
                ? " Inventory refresh cancelled; previous sizes remain."
                : " Inventory refresh could not finish; previous sizes remain. " + error.Message)
                + (fullRescan ? " Choose Rescan inventory to update sizes. Existing requests will not be sent again."
                    : " Recheck the folder to update sizes. Existing requests will not be sent again."),
            RefreshIncomplete = true
        };
}
