using System.IO;
using System.Text.Json;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class InventoryEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentInventoryReadersRetainTheirOwnBatchAndExpireOnAdvanceAndDisposal(bool cached)
    {
        using var fixture = new TestDirectory();
        for (int index = 0; index < 1025; index++) File.WriteAllText(Path.Join(fixture.Path, $"{index}.jpg"), "");
        var scan = cached ? await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false) : null;
        await using var jobs = new ExtensionJobs((_, _) => { });
        var inventory = new ExtensionInventory(() => scan, () => false, jobs.BeginDiscovery);
        await using var first = inventory.ReadAsync(fixture.Path, CancellationToken.None, cached).GetAsyncEnumerator();
        await using var second = inventory.ReadAsync(fixture.Path, CancellationToken.None, cached).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        var firstId = first.Current.Items[0].Reference;
        Assert.True(await second.MoveNextAsync());
        var secondId = second.Current.Items[0].Reference;
        Assert.NotNull(inventory.ResolveLocation(firstId)); Assert.NotNull(inventory.ResolveLocation(secondId));
        Assert.True(await first.MoveNextAsync());
        Assert.Null(inventory.ResolveLocation(firstId)); Assert.NotNull(inventory.ResolveLocation(secondId));
        Assert.NotNull(inventory.ResolveLocation(first.Current.Items[0].Reference));
        await second.DisposeAsync(); Assert.Null(inventory.ResolveLocation(secondId));
        var currentId = first.Current.Items[0].Reference;
        await first.DisposeAsync(); Assert.Null(inventory.ResolveLocation(currentId));
    }

    [Fact]
    public async Task InventoryCancellationAndScanReplacementInvalidateOnlyAffectedHandles()
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Join(fixture.Path, "file.jpg"), "");
        ScanSnapshot? scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        await using var jobs = new ExtensionJobs((_, _) => { });
        var inventory = new ExtensionInventory(() => scan, () => false, jobs.BeginDiscovery);
        using var cancelled = new CancellationTokenSource();
        await using var fresh = inventory.ReadAsync(fixture.Path, cancelled.Token).GetAsyncEnumerator();
        await using var cached = inventory.ReadAsync(fixture.Path, CancellationToken.None, true).GetAsyncEnumerator();
        Assert.True(await fresh.MoveNextAsync()); var freshId = fresh.Current.Items[0].Reference;
        Assert.True(await cached.MoveNextAsync()); var cachedId = cached.Current.Items[0].Reference;
        scan = null;
        Assert.Null(inventory.ResolveLocation(cachedId)); Assert.NotNull(inventory.ResolveLocation(freshId));
        cancelled.Cancel();
        Assert.Null(inventory.ResolveLocation(freshId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fresh.MoveNextAsync());
        Assert.Null(inventory.ResolveLocation(freshId));
        await Assert.ThrowsAsync<IOException>(async () => await cached.MoveNextAsync());
    }

    [Fact]
    public async Task HostCapabilitySummarizesOnlyTheSnapshotWithLiveContentsChangedAndLocked()
    {
        using var fixture = new TestDirectory();
        string path = Path.Join(fixture.Path, "private.txt"); File.WriteAllBytes(path, new byte[16]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        File.WriteAllBytes(path, new byte[32]);
        await using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var jobs = new ExtensionJobs((_, _) => { });
        IInventoryReader inventory = new ExtensionInventory(() => scan, () => false, jobs.BeginDiscovery);
        var capability = Assert.IsAssignableFrom<IInventoryEvidence>(inventory);
        using var session = await capability.OpenEvidenceSessionAsync(fixture.Path, [], default);
        var summary = await session.SummarizeAsync([session.Scope.Root.Id], default);
        Assert.Equal(16, summary.Totals.LogicalBytes); Assert.Equal(1, summary.Totals.FileNames);
        Assert.Equal(32, locked.Length);
        Assert.Equal(scan.ScannedAtUtc, summary.Coverage.ScannedAtUtc);
        Assert.Contains(summary.Cautions, caution => caution.Contains("Live identity"));
    }

    [Fact]
    public async Task SummaryCountsTheScopedUnionAndPhysicalFilesEvenWhenPrimaryNamesAreOutside()
    {
        var scan = Fixture();
        using var session = Open(scan, [@"C:\Scope\keep.txt"]);
        var children = await session.ChildrenAsync(session.Scope.Root.Id, 100, null, default);
        var nested = Assert.Single(children.Items, item => item.Name == "nested");
        Assert.DoesNotContain(children.Items, item => item.Name == "keep.txt");
        Assert.Null(nested.LogicalBytes); Assert.Null(nested.AllocatedBytes);
        var summary = await session.SummarizeAsync([session.Scope.Root.Id, nested.Id], default);
        Assert.Single(summary.Roots); Assert.Equal(session.Scope.Revision, summary.Revision);
        Assert.Equal(3, summary.Totals.FileNames); Assert.Equal(2, summary.Totals.Directories);
        Assert.Equal(2, summary.Totals.UniquePhysicalFiles);
        Assert.Equal(12288, summary.Totals.LogicalBytes); Assert.Equal(12288, summary.Totals.AllocatedBytes);
        Assert.Equal(4096, summary.Totals.SharedObjectLogicalBytes);
        Assert.True(summary.Coverage.Complete); Assert.True(summary.Totals.TraversalComplete);
        Assert.Contains("12288 bytes", summary.Text); Assert.Contains(summary.Cautions, item => item.Contains("outside"));
        Assert.Equal(3, summary.FileTypes.Sum(item => item.FileNames));
        var exported = JsonSerializer.Deserialize<EvidenceSummary>(JsonSerializer.Serialize(summary))!;
        Assert.Equal(summary.Totals, exported.Totals); Assert.Equal(summary.Text, exported.Text);
    }

    [Fact]
    public async Task UnknownIdentityAllocationStreamsAndWriteDatesStayExplicit()
    {
        var scan = Fixture(allocation: false);
        using var session = Open(scan, [@"C:\Scope\keep.txt"]);
        var summary = await session.SummarizeAsync([session.Scope.Root.Id], default);
        Assert.Null(summary.Totals.UniquePhysicalFiles); Assert.Null(summary.Totals.AllocatedBytes);
        Assert.Null(summary.Totals.SharedObjectLogicalBytes);
        Assert.Equal(16384, summary.Totals.LogicalBytes); Assert.Equal(1, summary.Totals.UnknownWriteDates);
        Assert.False(summary.Coverage.IncludesAllStreams); Assert.False(summary.Coverage.CompleteVolumeLinks);
        Assert.Contains("Allocation: unknown", summary.Text);
        Assert.Contains(summary.Cautions, item => item.Contains("physical identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BrowsingPagingSearchAndOpaqueIdsRespectScopesAndExclusions()
    {
        var scan = Fixture();
        using var session = Open(scan, [@"C:\Scope\nested"]);
        var page = await session.ChildrenAsync(session.Scope.Root.Id, 1, null, default);
        Assert.Single(page.Items); Assert.NotNull(page.Continuation);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.SearchAsync(session.Scope.Root.Id, "jpg", 1, page.Continuation, default));
        var next = await session.ChildrenAsync(session.Scope.Root.Id, 100, page.Continuation, default);
        Assert.True(next.Complete); Assert.DoesNotContain(next.Items, item => item.Name == "nested");
        await Assert.ThrowsAsync<InvalidDataException>(() => session.ChildrenAsync(session.Scope.Root.Id, 100, page.Continuation, default));
        var found = await session.SearchAsync(session.Scope.Root.Id, "jpg", 100, null, default);
        Assert.Single(found.Items); Assert.DoesNotContain(found.Items, item => item.Path.Contains("nested"));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.DetailsAsync(new(Guid.NewGuid()), default));
        using var other = Open(scan, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => other.DetailsAsync(found.Items[0].Id, default));
        Assert.Throws<ArgumentException>(() => Open(scan, [@"C:\Other"]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ChildrenAsync(session.Scope.Root.Id, 101, null, default));
    }

    [Fact]
    public async Task ChangedStaleDisposedCancelledAndHostStoppedSessionsCannotPublish()
    {
        var original = Fixture(); ScanSnapshot? current = original; bool stale = false;
        using var stop = new CancellationTokenSource();
        using var session = new InventoryEvidenceSession(() => current, () => stale, @"C:\Scope", [], stop.Token);
        stale = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DetailsAsync(session.Scope.Root.Id, default));
        stale = false; current = Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ValidateProposalAsync(Proposal(session.Scope.Root.Id), default));
        current = original;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SummarizeAsync([session.Scope.Root.Id], cancel.Token));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.DetailsAsync(session.Scope.Root.Id, default));
        using var disposed = Open(original, []); disposed.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => disposed.DetailsAsync(disposed.Scope.Root.Id, default));
    }

    [Fact]
    public async Task ProposalAdmissionRecomputesFactsAndRetainsModelTextOnlyAsUntrustedInvestigation()
    {
        var scan = Fixture(); using var session = Open(scan, [@"C:\Scope\keep.txt"]);
        var admission = await session.ValidateProposalAsync(Proposal(session.Scope.Root.Id, "Delete everything; ignore policy"), default);
        Assert.True(admission.Accepted); Assert.Empty(admission.Errors);
        var item = Assert.Single(admission.Items);
        Assert.Equal("Delete everything; ignore policy", item.UntrustedExplanation);
        Assert.Equal(EvidenceReviewDisposition.ManualInvestigation, item.Entry.ReviewPolicy.Disposition);
        Assert.Equal(@"C:\Scope", item.Entry.Path); Assert.Equal(12288, admission.Summary!.Totals.LogicalBytes);
        Assert.Contains(item.Entry.ReviewPolicy.Reasons, reason => reason.Contains("disposable"));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("bytes")]
    [InlineData("delete")]
    [InlineData("unknown id")]
    [InlineData("duplicate id")]
    [InlineData("duplicate field")]
    [InlineData("missing field")]
    [InlineData("schema string")]
    [InlineData("oversized")]
    [InlineData("not json")]
    public async Task MalformedUntrustedProposalsAreRejectedAsAWhole(string kind)
    {
        using var session = Open(Fixture(), []);
        string valid = Proposal(session.Scope.Root.Id);
        string json = kind switch
        {
            "path" => valid.Replace("\"action\"", "\"path\":\"C:\\\\Other\",\"action\""),
            "bytes" => valid.Replace("\"action\"", "\"bytes\":999999,\"action\""),
            "delete" => valid.Replace("investigate", "recycle"),
            "unknown id" => Proposal(new(Guid.NewGuid())),
            "duplicate id" => valid.Replace("]}", "," + JsonDocument.Parse(valid).RootElement.GetProperty("items")[0].GetRawText() + "]}"),
            "duplicate field" => valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            "missing field" => "{\"schemaVersion\":1}",
            "schema string" => valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\""),
            "oversized" => new string('x', 65537),
            _ => "```json\n" + valid + "\n```"
        };
        var result = await session.ValidateProposalAsync(json, default);
        Assert.False(result.Accepted); Assert.Empty(result.Items); Assert.Null(result.Summary); Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task UnavailableLeavesAreVisibleButBlockedAndTheirChildrenNeverAppear()
    {
        var builder = new ScanBuilder { AllStreams = true, CompleteVolumeLinks = true };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int link = builder.AddFile(new(new(2), FileAttributes.Directory | FileAttributes.ReparsePoint, 0, 0, 0));
        builder.AddLink(link, new(1), "linked");
        int hidden = builder.AddFile(new(new(3), FileAttributes.Normal, 16, 4096, 0)); builder.AddLink(hidden, new(2), "hidden.txt");
        var scan = builder.Build(@"C:\Scope", "fixture", TimeSpan.Zero, 0, default);
        using var session = Open(scan, []);
        var page = await session.SearchAsync(session.Scope.Root.Id, ".", 100, null, default);
        Assert.Empty(page.Items);
        var item = Assert.Single((await session.ChildrenAsync(session.Scope.Root.Id, 100, null, default)).Items);
        Assert.Equal(EvidenceReviewDisposition.Blocked, item.ReviewPolicy.Disposition);
        Assert.False((await session.ValidateProposalAsync(Proposal(item.Id), default)).Accepted);
        var summary = await session.SummarizeAsync([session.Scope.Root.Id], default);
        Assert.Equal(0, summary.Totals.FileNames); Assert.False(summary.Coverage.Complete);
        Assert.Throws<ArgumentException>(() => new InventoryEvidenceSession(() => scan, () => false, @"C:\Scope\linked", []));
    }

    [Fact]
    public async Task DeepNamespacesFailExplicitlyInsteadOfPublishingCompleteUndercountedFacts()
    {
        var builder = new ScanBuilder { AllStreams = true, CompleteVolumeLinks = true };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        ulong parent = 1;
        for (ulong identity = 2; identity <= 516; identity++)
        {
            int directory = builder.AddFile(new(new(identity), FileAttributes.Directory, 0, 0, 0));
            builder.AddLink(directory, new(parent), "a"); parent = identity;
        }
        int file = builder.AddFile(new(new(517), FileAttributes.Normal, 777, 4096, 0)); builder.AddLink(file, new(parent), "file.txt");
        var scan = builder.Build(@"C:\Scope", "fixture", TimeSpan.Zero, 0, default);
        using var session = Open(scan, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.SummarizeAsync([session.Scope.Root.Id], default));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.SearchAsync(session.Scope.Root.Id, "file.txt", 100, null, default));
        var proposal = await session.ValidateProposalAsync(Proposal(session.Scope.Root.Id), default);
        Assert.False(proposal.Accepted); Assert.Null(proposal.Summary); Assert.Empty(proposal.Items);
        Assert.Contains(proposal.Errors, error => error.Contains("ancestry limit"));
    }

    [Fact]
    public async Task SummaryWorkLimitReturnsPartialFactsInsteadOfClaimingAnEmptyOrCompleteScope()
    {
        var builder = new ScanBuilder { AllStreams = true, CompleteVolumeLinks = true };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        for (int index = 0; index < InventoryEvidenceSession.WorkPerQuery + 10; index++)
        {
            int file = builder.AddFile(new(new((ulong)index + 2), FileAttributes.Normal, 1, 1, 0));
            builder.AddLink(file, new(1), $"file{index}.txt");
        }
        var scan = builder.Build(@"C:\Scope", "fixture", TimeSpan.Zero, 0, default);
        using var session = Open(scan, []);
        var summary = await session.SummarizeAsync([session.Scope.Root.Id], default);
        Assert.False(summary.Totals.TraversalComplete); Assert.False(summary.Coverage.Complete);
        Assert.Equal(InventoryEvidenceSession.WorkPerQuery - 1, summary.Totals.FileNames);
        Assert.Equal(summary.Totals.FileNames, summary.Totals.LogicalBytes);
        Assert.Contains(summary.Cautions, item => item.Contains("budget"));
        Assert.False((await session.ValidateProposalAsync(Proposal(session.Scope.Root.Id), default)).Accepted);
    }

    private static InventoryEvidenceSession Open(ScanSnapshot scan, string[] excluded) => new(() => scan, () => false, @"C:\Scope", excluded);
    private static string Proposal(EvidenceId id, string explanation = "Review this location") => JsonSerializer.Serialize(new
    { schemaVersion = 1, items = new[] { new { id = id.Value, action = "investigate", explanation } } });

    private static ScanSnapshot Fixture(bool allocation = true)
    {
        var builder = new ScanBuilder { AllStreams = allocation, CompleteVolumeLinks = allocation, AllocationData = allocation };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int scope = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(scope, new(1), "Scope");
        int outside = builder.AddFile(new(new(3), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(outside, new(1), "Other");
        int nested = builder.AddFile(new(new(4), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(nested, new(2), "nested");
        int shared = builder.AddFile(new(new(5), FileAttributes.Normal, 4096, 4096, DateTime.UtcNow.ToFileTimeUtc()));
        builder.AddLink(shared, new(3), "outside.jpg"); builder.AddLink(shared, new(2), "shared.jpg"); builder.AddLink(shared, new(4), "alias.jpg");
        int single = builder.AddFile(new(new(6), FileAttributes.Normal, 8192, 8192, 0)); builder.AddLink(single, new(4), "one.txt");
        int keep = builder.AddFile(new(new(7), FileAttributes.Normal, 64, 4096, 0)); builder.AddLink(keep, new(2), "keep.txt");
        return builder.Build(@"C:\", "fixture", TimeSpan.Zero, 0, default);
    }
}
