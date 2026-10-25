using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Extensions.Containers;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ContainerStorageTests
{
    [Fact]
    public void DockerReportKeepsUnsafeObjectsOutOfCleanup()
    {
        var rows = DockerUsage.Parse(ContainerStorageFixture.Report);
        Assert.Equal(7, rows.Count);
        var stopped = Assert.Single(rows, row => row.State == "exited");
        Assert.Equal(2048, stopped.Bytes); Assert.Contains("Dev container", stopped.Details);
        Assert.True(stopped.CanClean);
        Assert.False(Assert.Single(rows, row => row.State == "running").CanClean);
        Assert.False(Assert.Single(rows, row => row.State == "Usage unknown" && row.Kind == "Images").CanClean);
        var unused = Assert.Single(rows, row => row.State == "Unused by containers");
        Assert.True(unused.CanClean); Assert.Contains("shared layers", unused.Details);
        var cache = Assert.Single(rows, row => row.Kind == "Build cache" && row.CanClean);
        Assert.Contains("exact cache record", cache.Details);
        Assert.False(Assert.Single(rows, row => row.Kind == "Build cache" && row.State == "In use").CanClean);
        var volume = Assert.Single(rows, row => row.Kind == "Volumes");
        Assert.Equal("Unattached", volume.State); Assert.True(volume.CanClean);
        Assert.Contains("Back it up first", volume.Details);
    }

    [Fact]
    public void MissingUsageUnknownStateNegativeSizesAndUnsafeIdsNeverEnableRemoval()
    {
        byte[] json = Encoding.UTF8.GetBytes("""
            {"Containers":[{"Id":"x; remove-everything","State":"exited","SizeRw":-1},{"Id":"123","State":"paused"}],
             "Images":[{"Id":"sha256:bad","Containers":0},{"Id":"sha256:bad","Containers":"0"}],
             "BuildCache":[{"ID":"cache","Size":"huge"}],"Volumes":[{"Name":"models","UsageData":{"Size":-1,"RefCount":-1}}]}
            """);
        var rows = DockerUsage.Parse(json);
        Assert.All(rows, row => { Assert.False(row.CanClean); Assert.Null(row.Bytes); });
        Assert.Throws<InvalidDataException>(() => DockerUsage.Parse(Encoding.UTF8.GetBytes("{\"Containers\":[]}")));
        Assert.Throws<OperationCanceledException>(() => DockerUsage.Parse(json, new(true)));
    }

    [Fact]
    public async Task DockerInspectionUsesOneFixedReadOnlyRequestAndRejectsOversizedReports()
    {
        using var handler = new ReportHandler(); using var client = new HttpClient(handler);
        Assert.Equal(7, (await DockerUsage.InspectAsync(client, default)).Count);
        Assert.Equal(1, handler.Calls); Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("http://localhost/v1.47/system/df", handler.Url);
        handler.Oversized = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerUsage.InspectAsync(client, default));
    }

    [Fact]
    public async Task InspectionDeadlineAlsoCancelsAResponseBodyThatStallsAfterHeaders()
    {
        using var handler = new ReportHandler { StallBody = true }; using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DockerUsage.InspectAsync(client, default, TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("AppData/Local/Docker/wsl/disk/docker_data.vhdx", "Docker virtual disk storage")]
    [InlineData("AppData/Local/Docker/wsl/data/ext4.vhdx", "Docker virtual disk storage")]
    [InlineData("AppData/Local/Packages/Ubuntu/LocalState/ext4.vhdx", "Linux virtual disk storage")]
    [InlineData("AppData/Local/wsl/distro/ext4.vhdx", "Linux virtual disk storage")]
    [InlineData("relocated/agent/ext4.vhdx", "Linux virtual disk storage")]
    [InlineData("machines/worker.vhdx", "Virtual disk storage")]
    [InlineData("legacy/worker.vhd", "Virtual disk storage")]
    public void BackingDisksAreBrowsableButKeptAtAnyScannedLocation(string file, string title)
    {
        var scan = AiStorageFixture.Inventory([file]);
        var finding = Assert.Single(OpportunityDetector.Find(scan));
        Assert.Equal(title, finding.Title); Assert.True(finding.RequiresManualReview); Assert.NotEmpty(finding.CleanupBlockReason);
        Assert.Equal("Containers & virtual disks", finding.Category);
        Assert.Contains("capacity differ", finding.Caution); Assert.Contains("compaction", finding.SuggestedAction);
        var group = Assert.Single(CleanupGrouping.Build(scan, [finding])); Assert.False(group.CanSelectGroup);
        var plan = CleanupPlanner.Prepare(scan, [finding with { CleanupBlockReason = "" }], discover: DeveloperOpportunityFixture.Find);
        Assert.False(plan.CanExecute); Assert.Single(plan.BlockedLocations);
    }

    [Fact]
    public void EngineStoresRequireCorroborationAndBackingDisksOverrideProjectMarkersAndProtectAncestors()
    {
        var ordinary = AiStorageFixture.Inventory(["Docker/settings.json", ".docker/config.json"], ["data/volumes", "data/containers"]);
        Assert.Empty(OpportunityDetector.Find(ordinary));
        var engine = AiStorageFixture.Inventory([], ["custom/containers", "custom/image", "custom/volumes", "custom/overlay2"]);
        Assert.Equal("Docker engine data store", Assert.Single(OpportunityDetector.Find(engine)).Title);
        var scan = AiStorageFixture.Inventory(["environment/pyvenv.cfg", "environment/project/package.json", "environment/project/node_modules/agent.vhdx"]);
        var findings = OpportunityDetector.Find(scan);
        Assert.Contains(findings, item => item.Title == "Virtual disk storage");
        Assert.DoesNotContain(findings, item => item.Title == "Node dependencies");
        var plan = CleanupPlanner.Prepare(scan, findings, discover: DeveloperOpportunityFixture.Find);
        Assert.False(plan.CanExecute); Assert.Equal(2, plan.BlockedLocations.Count);
    }

    private sealed class ReportHandler : HttpMessageHandler
    {
        internal int Calls; internal HttpMethod? Method; internal string? Url; internal bool Oversized; internal bool StallBody;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Method = request.Method; Url = request.RequestUri!.AbsoluteUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = StallBody ? new StreamContent(new StalledStream()) : new ByteArrayContent(ContainerStorageFixture.Report) };
            if (Oversized) response.Content.Headers.ContentLength = DockerUsage.MaximumResponseBytes + 1L;
            return Task.FromResult(response);
        }
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal static class ContainerStorageFixture
{
    internal static readonly string ContainerId = new('a', 64);
    internal static readonly byte[] Report = JsonSerializer.SerializeToUtf8Bytes(new
    {
        Containers = new object[]
        {
            new { Id = ContainerId, Names = new[] { "/agent-research-worker" }, State = "exited", SizeRw = 2048, Labels = new Dictionary<string, string> { ["devcontainer.local_folder"] = "G:/work/research" } },
            new { Id = new string('b', 64), Names = new[] { "/active-coding-agent" }, State = "running", SizeRw = 1024 }
        },
        Images = new object[]
        {
            new { Id = "sha256:" + new string('c', 64), RepoTags = new[] { "agent-base:local" }, Containers = 0, Size = 2L << 30, SharedSize = 1L << 30 },
            new { Id = "sha256:" + new string('d', 64), RepoTags = new[] { "research:latest" }, Containers = -1, Size = 3L << 30 }
        },
        BuildCache = new[]
        {
            new { ID = "cache-one", Description = "Install agent dependencies", InUse = false, Shared = true, Size = 4L << 30 },
            new { ID = "cache-two", Description = "Current research build", InUse = true, Shared = false, Size = 1L << 30 }
        },
        Volumes = new[] { new { Name = "agent-models-and-results", Driver = "local", CreatedAt = "2026-10-24T10:00:00Z", Mountpoint = "/var/lib/docker/volumes/agent-models-and-results/_data", Options = new Dictionary<string, string>(), UsageData = new { Size = 8L << 30, RefCount = 0 } } }
    });
}
