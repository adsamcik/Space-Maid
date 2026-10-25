using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using SpaceMaid.Extensions.Containers;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class DockerCleanupTests
{
    [Theory]
    [InlineData("Containers")]
    [InlineData("Images")]
    [InlineData("Build cache")]
    [InlineData("Volumes")]
    public async Task ConfirmedCleanupTargetsOnlyTheReviewedObjectWithoutForceOrBroadPruning(string kind)
    {
        using var handler = new Engine(); using var client = new HttpClient(handler);
        var target = DockerUsage.Parse(ContainerStorageFixture.Report).Single(item => item.Kind == kind && item.CanClean);
        var plan = await DockerCleanup.PrepareAsync(client, target, default);
        Assert.Empty(handler.Mutations);
        handler.Target = target;
        var result = await DockerCleanup.ExecuteAsync(client, plan, default);
        Assert.True(result.ConfirmedSuccess, result.Message);
        var mutation = Assert.Single(handler.Mutations);
        Assert.Equal("localhost", mutation.Url.Host);
        Assert.DoesNotContain("force=true", mutation.Url.Query);
        Assert.DoesNotContain("system/prune", mutation.Url.AbsolutePath);
        if (kind == "Build cache")
        {
            Assert.Equal(HttpMethod.Post, mutation.Method);
            Assert.Equal("/v1.47/build/prune", mutation.Url.AbsolutePath);
            string query = Uri.UnescapeDataString(mutation.Url.Query);
            Assert.Contains("all=false", query);
            var filters = JsonNode.Parse(query[(query.IndexOf("filters=", StringComparison.Ordinal) + 8)..])!;
            Assert.Equal("^" + target.Id + "$", filters["id"]![0]!.GetValue<string>());
            Assert.Single(filters.AsObject()); Assert.Single(filters["id"]!.AsArray());
        }
        else
        {
            Assert.Equal(HttpMethod.Delete, mutation.Method);
            Assert.EndsWith("/" + target.Id, Uri.UnescapeDataString(mutation.Url.AbsolutePath));
            if (kind == "Containers") Assert.Contains("v=false", mutation.Url.Query);
            if (kind == "Images") Assert.Contains("noprune=true", mutation.Url.Query);
        }
    }

    [Theory]
    [InlineData("Containers", "State", "running")]
    [InlineData("Volumes", "CreatedAt", "2026-10-25T11:00:00Z")]
    public async Task ChangedOrReplacedTargetsAreRejectedBeforeAnyMutation(string kind, string field, string value)
    {
        using var handler = new Engine(); using var client = new HttpClient(handler);
        var target = DockerUsage.Parse(handler.Report).Single(item => item.Kind == kind && item.CanClean);
        var plan = await DockerCleanup.PrepareAsync(client, target, default);
        var report = JsonNode.Parse(handler.Report)!; report[kind]![0]![field] = value;
        handler.Report = Encoding.UTF8.GetBytes(report.ToJsonString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => DockerCleanup.ExecuteAsync(client, plan, default));
        Assert.Empty(handler.Mutations);
    }

    [Fact]
    public async Task EngineSwitchAfterConfirmationAndReferencedVolumesFailClosed()
    {
        using var handler = new Engine(); using var client = new HttpClient(handler);
        var target = DockerUsage.Parse(handler.Report).First(item => item.CanClean);
        var plan = await DockerCleanup.PrepareAsync(client, target, default);
        handler.EngineId = "different-local-engine";
        await Assert.ThrowsAsync<InvalidOperationException>(() => DockerCleanup.ExecuteAsync(client, plan, default));
        Assert.Empty(handler.Mutations);
        var report = JsonNode.Parse(handler.Report)!; report["Volumes"]![0]!["UsageData"]!["RefCount"] = 1;
        handler.Report = Encoding.UTF8.GetBytes(report.ToJsonString());
        var volume = DockerUsage.Parse(handler.Report).Single(item => item.Kind == "Volumes");
        await Assert.ThrowsAsync<InvalidOperationException>(() => DockerCleanup.PrepareAsync(client, volume, default));
        Assert.Empty(handler.Mutations);
    }

    [Fact]
    public async Task LostMutationResponseNeverClaimsSuccessOrRetries()
    {
        using var handler = new Engine(); using var client = new HttpClient(handler);
        var target = DockerUsage.Parse(handler.Report).First(item => item.CanClean);
        var plan = await DockerCleanup.PrepareAsync(client, target, default);
        handler.Disconnect = true;
        var result = await DockerCleanup.ExecuteAsync(client, plan, default);
        Assert.False(result.ConfirmedSuccess); Assert.Contains("uncertain", result.Message);
        Assert.Single(handler.Mutations);
    }

    private sealed class Engine : HttpMessageHandler
    {
        internal byte[] Report = ContainerStorageFixture.Report;
        internal string EngineId = "local-engine";
        internal bool Disconnect;
        internal DockerStorageItem? Target;
        internal List<(HttpMethod Method, Uri Url)> Mutations = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.EndsWith("/info", StringComparison.Ordinal)
                    ? new StringContent("{\"ID\":\"" + EngineId + "\"}") : new ByteArrayContent(Report) });
            Mutations.Add((request.Method, request.RequestUri!));
            if (Disconnect) throw new HttpRequestException("Owned simulated disconnect");
            return Task.FromResult(Target!.Kind switch
            {
                "Build cache" => new(HttpStatusCode.OK) { Content = new StringContent("{\"CachesDeleted\":[\"cache-one\"],\"SpaceReclaimed\":1024}") },
                "Images" => new(HttpStatusCode.OK) { Content = new StringContent("[{\"Deleted\":\"" + Target.Id + "\"}]") },
                _ => new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) }
            });
        }
    }
}
