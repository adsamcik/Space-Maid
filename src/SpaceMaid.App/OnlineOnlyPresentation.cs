using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App;

internal static class OnlineOnlyPresentation
{
    internal static CloudStorageReviewResult Completed(OnlineOnlyResult result)
        => new($"{result.Requested:N0} online-only requests accepted · {result.ConfirmedOnlineOnly:N0} confirmed online-only · {result.Skipped:N0} skipped"
            + (result.Cancelled ? " · Cancelled; earlier requests remain active." : ". The provider may still be processing requests."), result.Requested > 0)
        { Receipt = result };

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
