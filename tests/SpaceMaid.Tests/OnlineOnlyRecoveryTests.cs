using System.IO;
using SpaceMaid.App;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OnlineOnlyRecoveryTests
{
    [Fact]
    public void FailedInventoryRefreshPreservesAcceptedAndConfirmedReceipt()
    {
        var receipt = new OnlineOnlyResult(7, 2, 3, false, ["A locked file was skipped."]);
        var completed = OnlineOnlyPresentation.Completed(receipt);
        var displayed = OnlineOnlyPresentation.RefreshFailed(completed, new IOException("Namespace accounting unavailable."), true);

        Assert.Same(receipt, displayed.Receipt);
        Assert.True(displayed.RequestsAccepted);
        Assert.True(displayed.RefreshIncomplete);
        Assert.Contains("7 online-only requests accepted", displayed.Message);
        Assert.Contains("2 confirmed online-only", displayed.Message);
        Assert.Contains("3 skipped", displayed.Message);
        Assert.Contains("provider may still be processing", displayed.Message);
        Assert.Contains("Rescan inventory", displayed.Message);
        Assert.DoesNotContain("Recheck the folder", displayed.Message);
        Assert.DoesNotContain("Online-only operation could not finish", displayed.Message);
        Assert.False(completed.RefreshIncomplete);
    }

    [Fact]
    public void RefreshCancellationDoesNotCancelPreviouslyAcceptedRequests()
    {
        var receipt = new OnlineOnlyResult(4, 0, 0, false, []);
        var displayed = OnlineOnlyPresentation.RefreshFailed(OnlineOnlyPresentation.Completed(receipt), new OperationCanceledException(), false);

        Assert.Same(receipt, displayed.Receipt);
        Assert.True(displayed.RequestsAccepted);
        Assert.Contains("Inventory refresh cancelled", displayed.Message);
        Assert.Contains("0 confirmed online-only", displayed.Message);
        Assert.Contains("will not be sent again", displayed.Message);
    }

    [Fact]
    public void ZeroAcceptedRequestsRemainUnacceptedWhenDisplayingARefreshFailure()
    {
        var displayed = OnlineOnlyPresentation.RefreshFailed(
            OnlineOnlyPresentation.Completed(new(0, 0, 5, false, [])), new IOException("Unavailable"), false);
        Assert.False(displayed.RequestsAccepted);
        Assert.Equal(0, displayed.Receipt!.Requested);
    }

    [Fact]
    public void NoEligibleItemsReportsActualReasonsWithoutGuessingEligibility()
    {
        var plan = new OnlineOnlyPlan(@"G:\owned-cloud", "Fixture", [], 2,
            [@"G:\owned-cloud\private: Access denied.", @"G:\owned-cloud\offline: Provider unavailable."]);
        var displayed = OnlineOnlyPresentation.NoEligible(plan);

        Assert.False(displayed.RequestsAccepted);
        Assert.Null(displayed.Receipt);
        Assert.Contains("2 skipped", displayed.Message);
        Assert.Contains("No requests were sent", displayed.Message);
        Assert.Contains("Details", displayed.Message);
        Assert.DoesNotContain("Unsynced, local-only", displayed.Message);
        Assert.Contains("Access denied", displayed.Details);
        Assert.Contains("Provider unavailable", displayed.Details);
        Assert.Contains("Inspect reported items in Explorer", displayed.Message);
        Assert.Contains(displayed.Details, OnlineOnlyPresentation.Status(displayed));
    }

    [Fact]
    public void PreparationAndExecutionWarningsRemainAvailableAfterRefreshFailure()
    {
        var plan = new OnlineOnlyPlan(@"G:\owned-cloud", "Fixture", [], 1,
            [@"G:\owned-cloud\locked: Access denied."]);
        var receipt = new OnlineOnlyResult(1, 0, 2, false, [@"G:\owned-cloud\changed: File changed."]);
        var completed = OnlineOnlyPresentation.CompletedWithPreparation(receipt, plan);
        var displayed = OnlineOnlyPresentation.RefreshFailed(completed, new IOException("Accounting unavailable"), true);

        Assert.Same(receipt, displayed.Receipt);
        Assert.Equal(completed.Details, displayed.Details);
        Assert.Contains("locked", displayed.Details);
        Assert.Contains("changed", displayed.Details);
        Assert.Contains("1 online-only requests accepted", displayed.Message);
        Assert.Contains("0 confirmed online-only", displayed.Message);
        Assert.Contains("2 skipped", displayed.Message);
        Assert.True(displayed.RefreshIncomplete);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 0, true)]
    [InlineData(2, 2, false)]
    public void PendingProviderWorkIsNotInventedWhenNoAcceptedRequestsRemain(int accepted, int confirmed, bool cancelled)
    {
        var displayed = OnlineOnlyPresentation.Completed(new(accepted, confirmed, 1, cancelled, ["Unavailable item."]));
        Assert.DoesNotContain("provider may still be processing", displayed.Message);
        if (accepted == 0) Assert.DoesNotContain("earlier requests remain active", displayed.Message);
    }

    [Fact]
    public void MissingOrOversizedReasonsHaveHonestBoundedDiagnostics()
    {
        var missing = OnlineOnlyPresentation.Completed(new(0, 0, 1, false, []));
        Assert.Contains("No further reason was reported", missing.Details);
        var oversized = OnlineOnlyPresentation.Completed(new(0, 0, 1, false, [new string('x', 9000)]));
        Assert.True(oversized.Details.Length < 2500);
        Assert.Contains("activity log", oversized.Details);
        Assert.Equal(9000, oversized.Receipt!.Warnings[0].Length);
    }

    [Fact]
    public void UnconfirmedAcceptedRequestOffersAReadOnlyRefresh()
    {
        var receipt = new OnlineOnlyResult(1, 0, 0, false, [@"G:\owned-cloud\file: Request accepted; local storage could not be confirmed."]);
        var displayed = OnlineOnlyPresentation.Completed(receipt);
        Assert.Same(receipt, displayed.Receipt);
        Assert.Contains("Refresh the folder", displayed.Message);
        Assert.DoesNotContain("before reviewing again", displayed.Message);
        Assert.Contains("Request accepted", displayed.Details);
        Assert.DoesNotContain("not changed", displayed.Details);
    }

    [Fact]
    public async Task UnsupportedIncrementalInventoryPublishesRecoveryWithoutChangingExceptionType()
    {
        var builder = new ScanBuilder { AllocationData = false };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        var scan = builder.Build(@"G:\owned-recovery", "Fixture", TimeSpan.Zero, 0, default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ScanRechecker.RefreshAsync(scan, [new(scan.RootPath)]));
        Assert.True(ScanRechecker.RequiresFullRescan(error));
        Assert.False(ScanRechecker.RequiresFullRescan(new IOException("Temporary sharing violation.")));
    }
}
