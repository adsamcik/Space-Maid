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
