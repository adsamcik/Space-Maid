using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SpaceMaid.Extensions.Models;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OllamaCleanupTests
{
    private static readonly string DigestA = new('a', 64);
    private static readonly string DigestB = new('b', 64);
    private static readonly OllamaModel Selected = new("research:local", DigestA, 4096, false);

    [Fact]
    public void InspectionRequiresCompleteStateAndProtectsAliasesSharingALoadedDigest()
    {
        byte[] tags = Tags(new { name = Selected.Name, model = Selected.Name, digest = DigestA, size = 4096 },
            new { name = "alias:latest", model = "alias:latest", digest = "sha256:" + DigestA, size = 4096 });
        byte[] loaded = Tags(new { name = "alias:latest", digest = DigestA });
        var snapshot = OllamaCleanup.Parse(tags, loaded);
        Assert.Equal(2, snapshot.Models.Count); Assert.All(snapshot.Models, model => Assert.True(model.Loaded));
        Assert.Throws<InvalidDataException>(() => OllamaCleanup.Parse(tags, Encoding.UTF8.GetBytes("{}")));
        Assert.Throws<InvalidDataException>(() => OllamaCleanup.Parse(Tags(new { name = "bad", digest = DigestA, size = -1 }), Tags()));
        Assert.Throws<InvalidDataException>(() => OllamaCleanup.Parse(Tags(new { name = "bad", digest = "short", size = 1 }), Tags()));
        Assert.Throws<InvalidDataException>(() => OllamaCleanup.Parse(Tags(new { name = "a", model = "b", digest = DigestA, size = 1 }), Tags()));
        Assert.Throws<InvalidDataException>(() => OllamaCleanup.Parse(Tags(new { name = "same", digest = DigestA, size = 1 }, new { name = "same", digest = DigestB, size = 1 }), Tags()));
        Assert.Throws<OperationCanceledException>(() => OllamaCleanup.Parse(tags, loaded, new(true)));
    }

    [Fact]
    public async Task PreviewAndExecutionRecheckIdentityAndUseOnlyTheExactLocalDeletePayload()
    {
        int tagReads = 0;
        using var handler = new Handler(request => request.Method == HttpMethod.Delete ? Json("{}")
            : request.RequestUri!.AbsolutePath == "/api/ps" ? Json(Tags())
            : Json(++tagReads <= 2 ? Present() : Tags()));
        using var client = new HttpClient(handler);
        var preview = await OllamaCleanup.PrepareAsync(client, Selected, default);
        Assert.Equal(Selected, preview.Model);
        var result = await OllamaCleanup.ExecuteAsync(client, preview, default);
        Assert.True(result.Deleted); Assert.True(result.Attempted); Assert.NotNull(result.Snapshot);
        Assert.Empty(result.Snapshot.Models); Assert.Equal(7, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.StartsWith(OllamaCleanup.LocalEndpoint + "/api/", request.Url));
        var deletion = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
        Assert.Equal(OllamaCleanup.LocalEndpoint + "/api/delete", deletion.Url);
        using var body = JsonDocument.Parse(deletion.Body!);
        var property = Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal("model", property.Name); Assert.Equal(Selected.Name, property.Value.GetString());
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("size")]
    [InlineData("loaded")]
    [InlineData("missing")]
    [InlineData("unknown-loaded")]
    public async Task ChangesOrUncertainLoadedStateAfterConfirmationPreventAnyDelete(string change)
    {
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/ps")
                return Json(change == "loaded" ? Tags(new { name = "alias:latest", digest = DigestA })
                    : change == "unknown-loaded" ? Encoding.UTF8.GetBytes("{\"models\":null}") : Tags());
            return Json(change == "missing" ? Tags() : change == "digest" ? Present(DigestB)
                : change == "size" ? Present(bytes: 8192) : Present());
        });
        using var client = new HttpClient(handler);
        var result = await OllamaCleanup.ExecuteAsync(client, new(Selected, DateTimeOffset.UtcNow), default);
        Assert.False(result.Attempted); Assert.False(result.Deleted); Assert.Null(result.Snapshot);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
        Assert.StartsWith("No deletion started.", result.Message);
    }

    [Fact]
    public async Task ADeletionWhoseResultCannotBeVerifiedIsReportedAsUncertainAndNeverRetried()
    {
        bool deleted = false;
        using var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Delete) { deleted = true; return Json("{}"); }
            if (deleted) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return Json(request.RequestUri!.AbsolutePath == "/api/ps" ? Tags() : Present());
        });
        using var client = new HttpClient(handler);
        var result = await OllamaCleanup.ExecuteAsync(client, new(Selected, DateTimeOffset.UtcNow), default);
        Assert.True(result.Attempted); Assert.False(result.Deleted); Assert.Null(result.Snapshot);
        Assert.Contains("could not be verified", result.Message);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ANameStillPresentAfterSuccessfulDeleteResponseIsNotReportedAsRemoved()
    {
        using var handler = new Handler(request => Json(request.Method == HttpMethod.Delete ? Encoding.UTF8.GetBytes("{}")
            : request.RequestUri!.AbsolutePath == "/api/ps" ? Tags() : Present()));
        using var client = new HttpClient(handler);
        var result = await OllamaCleanup.ExecuteAsync(client, new(Selected, DateTimeOffset.UtcNow), default);
        Assert.True(result.Attempted); Assert.False(result.Deleted); Assert.NotNull(result.Snapshot);
        Assert.Contains("present after refresh", result.Message);
    }

    [Fact]
    public async Task ResponseLimitsAndTheWholeBodyDeadlineFailClosed()
    {
        using var oversized = new Handler(_ =>
        {
            var response = Json(Present()); response.Content.Headers.ContentLength = OllamaCleanup.MaximumResponseBytes + 1L; return response;
        });
        using var oversizedClient = new HttpClient(oversized);
        await Assert.ThrowsAsync<InvalidDataException>(() => OllamaCleanup.InspectAsync(oversizedClient, default));
        using var stalled = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        using var stalledClient = new HttpClient(stalled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OllamaCleanup.InspectAsync(stalledClient, default,
            TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(2)));
        var many = Enumerable.Range(0, OllamaCleanup.MaximumModels + 1).Select(index => (object)new { name = "model" + index, digest = DigestA, size = 1 }).ToArray();
        Assert.Throws<InvalidDataException>(() => OllamaCleanup.Parse(Tags(many), Tags()));
    }

    private static byte[] Present(string? digest = null, long bytes = 4096) => Tags(new { name = Selected.Name, model = Selected.Name, digest = digest ?? DigestA, size = bytes });
    private static byte[] Tags(params object[] models) => JsonSerializer.SerializeToUtf8Bytes(new { models });
    private static HttpResponseMessage Json(string json) => Json(Encoding.UTF8.GetBytes(json));
    private static HttpResponseMessage Json(byte[] json) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(json) };
    private sealed record Request(HttpMethod Method, string Url, string? Body);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method, request.RequestUri!.AbsoluteUri, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
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
