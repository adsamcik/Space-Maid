using System.IO;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class AssistantReviewTests
{
    [Fact]
    public async Task OnlyHostOwnedKnownFoldersCanRequestTheSeparateHumanReview()
    {
        using var fixture = new TestDirectory();
        var folder = Directory.CreateDirectory(Path.Join(fixture.Path, "output")).FullName;
        File.WriteAllText(Path.Join(folder, "custom.txt"), "keep me");
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        int reviews = 0;
        await using var runtime = Create(fixture.Path, () => scan, () => false, (_, _, _) => { reviews++; return Task.FromResult(new EvidenceCleanupReviewResult(1, [], "Human review requested")); });
        using var session = await runtime.Evidence.OpenEvidenceSessionAsync(fixture.Path, [], default);
        var item = Assert.Single((await session.ChildrenAsync(session.Scope.Root.Id, 20, null, default)).Items);
        Assert.Equal(1, (await runtime.ReviewEvidenceAsync(session, [item.Id], default)).MatchedLocations);
        Assert.Equal(1, reviews); Assert.Equal("keep me", File.ReadAllText(Path.Join(folder, "custom.txt")));
        using var fabricated = new InventoryEvidenceSession(() => scan, () => false, fixture.Path, []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ReviewEvidenceAsync(fabricated, [fabricated.Scope.Root.Id], default));
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ReviewEvidenceAsync(session, [new(Guid.NewGuid())], default));
        var file = Assert.Single((await session.ChildrenAsync(item.Id, 20, null, default)).Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ReviewEvidenceAsync(session, [file.Id], default));
        Assert.Equal(1, reviews);
    }

    [Fact]
    public async Task WholeFolderReviewCannotIncludeExcludedDescendants()
    {
        using var fixture = new TestDirectory();
        var folder = Directory.CreateDirectory(Path.Join(fixture.Path, "output")).FullName;
        string excluded = Path.Join(folder, "custom.txt"); File.WriteAllText(excluded, "keep me");
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        await using var runtime = Create(fixture.Path, () => scan, () => false, (_, _, _) => throw new Exception("Exclusion bypass reached human review."));
        using var session = await runtime.Evidence.OpenEvidenceSessionAsync(fixture.Path, [excluded], default);
        var item = Assert.Single((await session.ChildrenAsync(session.Scope.Root.Id, 20, null, default)).Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ReviewEvidenceAsync(session, [item.Id], default));
        Assert.Equal("keep me", File.ReadAllText(excluded));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("replaced")]
    [InlineData("cancelled")]
    [InlineData("administrator")]
    [InlineData("host stopped")]
    public async Task RevokedOrElevatedEvidenceCannotRequestReview(string state)
    {
        using var fixture = new TestDirectory();
        Directory.CreateDirectory(Path.Join(fixture.Path, "output"));
        ScanSnapshot? scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        bool stale = false;
        await using var runtime = Create(fixture.Path, () => scan, () => stale, (_, _, _) => throw new Exception("Revoked evidence reached human review."), state == "administrator");
        using var session = await runtime.Evidence.OpenEvidenceSessionAsync(fixture.Path, [], default);
        var item = Assert.Single((await session.ChildrenAsync(session.Scope.Root.Id, 20, null, default)).Items);
        using var cancel = new CancellationTokenSource();
        if (state == "stale") stale = true;
        if (state == "replaced") scan = null;
        if (state == "cancelled") cancel.Cancel();
        if (state == "host stopped") await runtime.DisposeAsync();
        var error = await Record.ExceptionAsync(() => runtime.ReviewEvidenceAsync(session, [item.Id], cancel.Token));
        Assert.True(error is InvalidOperationException or OperationCanceledException, error?.ToString());
    }

    private static ExtensionRuntime Create(string root, Func<ScanSnapshot?> scan, Func<bool> stale,
        Func<IInventoryEvidenceSession, IReadOnlyList<EvidenceId>, CancellationToken, Task<EvidenceCleanupReviewResult>> review, bool administrator = false)
        => new(scan, stale, _ => throw new Exception("Unexpected reveal"), Path.Join(root, "settings"), Path.Join(root, "bundles"), Path.Join(root, "logs"), administrator, review);
}
